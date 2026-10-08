using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using HandRaise.Application.Training;
using HandRaise.Domain.Training;

namespace HandRaise.Infrastructure.Windows.Training;

/// <summary>Reusable JSONL transport, also exercised by a lightweight PowerShell stub.</summary>
public sealed class StructuredTrainingProcess
{
    public async Task<TrainingResult> RunAsync(ProcessStartInfo start, string logPath, Func<TrainingProgress, Task> progress,
        TimeSpan grace, TimeSpan? timeout, CancellationToken ct)
    {
        start.UseShellExecute = false;
        start.CreateNoWindow = true;
        start.RedirectStandardInput = start.RedirectStandardOutput = start.RedirectStandardError = true;
        start.StandardOutputEncoding = start.StandardErrorEncoding = Encoding.UTF8;
        using var process = new Process { StartInfo = start };
        ct.ThrowIfCancellationRequested();
        if (!process.Start()) throw new TrainingWorkerException("worker_start_failed", "No se pudo iniciar el worker.", logPath);
        WindowsTrainingProcessLease? lease = null;
        Task? stderr = null;
        Task? exitCleanup = null;
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        if (timeout.HasValue) deadline.CancelAfter(timeout.Value);
        TrainingResult? result = null;
        try
        {
            lease = new WindowsTrainingProcessLease(process);
            exitCleanup = Task.Run(async () => { await process.WaitForExitAsync(); lease.Dispose(); });
            stderr = DrainLogAsync(process.StandardError, logPath);
            await progress(Observe(new(TrainingJobStatus.Training, 0, Message: "Iniciando el entorno de entrenamiento.")
            { IsIndeterminate = true }, logPath));
            // Worker waits for this handshake before importing torch or creating descendants.
            await process.StandardInput.WriteLineAsync("{\"type\":\"start\",\"protocol_version\":1}");
            await process.StandardInput.FlushAsync();
            while (await ReadBoundedLineAsync(process.StandardOutput, deadline.Token) is { } line)
            {
                var message = TrainingWorkerProtocol.Parse(line);
                if (result != null) throw new InvalidDataException("El worker envió mensajes después del resultado final.");
                switch (message.Type)
                {
                    case "capabilities": await progress(Observe(new(TrainingJobStatus.Training, 0,
                        Message: "Cargando el modelo inicial.", Device: message.Device, WorkerVersion: message.WorkerVersion)
                        { IsIndeterminate = true }, logPath)); break;
                    case "progress": await progress(Observe(MapProgress(message), logPath)); break;
                    case "completed": result = new(message.ModelPath!, message.WorkerVersion!, message.Device!, message.Metrics ?? []); break;
                    case "error": throw new TrainingWorkerException(message.ErrorCode ?? "worker_error",
                        message.Message ?? "El worker falló.", logPath);
                }
            }
            await process.WaitForExitAsync(deadline.Token);
            await stderr.WaitAsync(deadline.Token);
            if (process.ExitCode != 0 || result == null)
                throw new TrainingWorkerException("worker_crashed", $"El worker terminó sin resultado válido (código {process.ExitCode}).", logPath);
            return result;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        { throw new TrainingWorkerException("worker_timeout", "El entrenamiento superó el tiempo máximo configurado.", logPath); }
        finally
        {
            if (!process.HasExited)
            {
                try
                {
                    await process.StandardInput.WriteLineAsync("{\"type\":\"cancel\",\"protocol_version\":1}").WaitAsync(TimeSpan.FromSeconds(1));
                    await process.StandardInput.FlushAsync().WaitAsync(TimeSpan.FromSeconds(1));
                    await process.WaitForExitAsync().WaitAsync(grace);
                }
                catch (Exception ex) when (ex is IOException or InvalidOperationException or TimeoutException) { }
                finally
                {
                    if (!process.HasExited) process.Kill(entireProcessTree: true);
                    await process.WaitForExitAsync();
                }
            }
            lease?.Dispose(); // also terminates any remaining descendants after an ordinary worker exit
            if (exitCleanup != null) await exitCleanup;
            if (stderr != null) await stderr;
        }
    }

    private static TrainingProgress Observe(TrainingProgress value, string logPath) => value with
    { ActivityAtUtc = DateTimeOffset.UtcNow, ProcessState = "running", LogPath = logPath };
    private static TrainingProgress MapProgress(TrainingWorkerMessage message)
    {
        var stage = message.Stage!.Value;
        var epochProgress = stage == TrainingJobStatus.Training && message.TotalEpochs is > 0 && message.CurrentEpoch.HasValue;
        var percent = epochProgress ? 100.0 * message.CurrentEpoch!.Value / message.TotalEpochs!.Value : message.Percent!.Value;
        var text = stage switch
        {
            TrainingJobStatus.Training => "Entrenando el modelo.",
            TrainingJobStatus.Validating => "Validando los resultados.",
            TrainingJobStatus.Exporting => "Exportando el modelo.",
            _ => message.Message
        };
        return new(stage, percent, message.CurrentEpoch, message.TotalEpochs, text, message.Metrics)
        { IsIndeterminate = message.IsIndeterminate || stage is TrainingJobStatus.Validating or TrainingJobStatus.Exporting };
    }

    private static async Task<string?> ReadBoundedLineAsync(StreamReader reader, CancellationToken ct)
    {
        var result = new StringBuilder();
        var buffer = new char[1];
        while (await reader.ReadAsync(buffer.AsMemory(), ct) != 0)
        {
            if (buffer[0] == '\n') return result.ToString().TrimEnd('\r');
            result.Append(buffer[0]);
            if (result.Length > 65536) throw new InvalidDataException("El worker excedió el límite del protocolo.");
        }
        return result.Length == 0 ? null : throw new InvalidDataException("El worker interrumpió un mensaje JSONL.");
    }
    private static async Task DrainLogAsync(StreamReader reader, string path)
    {
        await using var log = new StreamWriter(new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read));
        var buffer = new char[4096];
        long written = 0;
        int count;
        while ((count = await reader.ReadAsync(buffer)) > 0)
        {
            if (written < 8 * 1024 * 1024) await log.WriteAsync(buffer.AsMemory(0, count));
            written += count; // keep draining after log cap to avoid blocking the process
        }
    }
}

public sealed record TrainingWorkerRequest(int ProtocolVersion, string Dataset, string Output, string BaseModel,
    string BaseModelSha256, TrainingConfiguration Configuration, IReadOnlyList<string> Classes,
    string WorkerVersion, string? RuntimeVersion, string? RuntimeManifestSha256, TrainingDevice SelectedDevice);

public sealed class PythonYoloTrainingWorker(TrainingPaths paths, TrainingRuntimeOptions runtime) : ITrainingWorker, ITrainingDeviceValidator
{
    public static TrainingWorkerRequest CreateRequest(TrainingJob job, string output, string baseModel, string hash)
    {
        if (job.SelectedDevice == null || job.Configuration.Device != job.SelectedDevice.WorkerDevice)
            throw new InvalidDataException("Dispositivo del job inconsistente.");
        return new(1, job.Dataset?.YamlPath ?? throw new InvalidOperationException("Falta el dataset."), output, baseModel,
            hash, job.Configuration, job.Snapshot.Classes.Select(c => c.Name).ToArray(), "2.0.0", job.RuntimeVersion, job.RuntimeManifestSha256, job.SelectedDevice);
    }
    public async Task<TrainingRuntimeIdentity> ValidateDeviceAsync(TrainingDevice device, CancellationToken ct)
    {
        runtime.Validate();
        var managed = TrainingRuntimeOptions.FromManagedDirectory(paths);
        if (runtime.PythonExecutable != managed.PythonExecutable || runtime.WorkerScript != managed.WorkerScript || runtime.BaseModelPath != managed.BaseModelPath)
            throw new TrainingWorkerException("training_runtime_invalid", "El entrenamiento requiere el entorno privado administrado por VisionControl.");
        var provisioner = new TrainingRuntimeProvisioner(paths);
        using var lease = provisioner.Acquire(false);
        await provisioner.VerifyQuickAsync(provisioner.Current, ct);
        TrainingDeviceSelection.RequireAvailable(device, await new ManagedTrainingRuntimeProbe().ProbeAsync(runtime, ct));
        return new(TrainingRuntimeProvisioner.Version,
            await TrainingRuntimeProvisioner.HashAsync(TrainingPaths.Within(provisioner.Current, "installed.json"), ct));
    }
    public async Task<TrainingResult> RunAsync(TrainingJob job, Func<TrainingProgress, Task> progress, CancellationToken ct)
    {
        runtime.Validate();
        var provisioner = new TrainingRuntimeProvisioner(paths);
        using var lease = provisioner.Acquire(false);
        var identity = await ValidateDeviceAsync(job.SelectedDevice ?? throw new ArgumentException("Seleccione CPU o GPU para entrenar."), ct);
        if (identity.Version != job.RuntimeVersion || identity.ManifestSha256 != job.RuntimeManifestSha256)
            throw new TrainingWorkerException("training_runtime_changed", "El entorno cambió después de preparar el entrenamiento. Inicie un nuevo entrenamiento.");
        if (job.Configuration.Device != job.SelectedDevice.WorkerDevice) throw new InvalidDataException("Dispositivo del job inconsistente.");
        runtime.Validate();
        if (job.BaseModel != TrainingProfiles.BaseModel) throw new TrainingWorkerException("unsupported_base_model", "Modelo base no compatible.");
        if (job.Dataset == null) throw new InvalidOperationException("Falta el dataset exportado.");
        var root = paths.Job(job.Id);
        var baseCopy = TrainingPaths.Within(root, "yolo26n.pt");
        await using (var input = File.OpenRead(runtime.BaseModelPath))
        await using (var output = new FileStream(baseCopy, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            await input.CopyToAsync(output, ct);
        string hash;
        await using (var input = File.OpenRead(baseCopy)) hash = Convert.ToHexString(await SHA256.HashDataAsync(input, ct));
        var request = TrainingPaths.Within(root, "request.json");
        await TrainingJson.WriteAsync(request, CreateRequest(job, TrainingPaths.Within(root, "output"), baseCopy, hash), ct);
        var start = new ProcessStartInfo(runtime.PythonExecutable) { WorkingDirectory = root };
        start.ArgumentList.Add("-I"); start.ArgumentList.Add("-B"); start.ArgumentList.Add("-u"); start.ArgumentList.Add(runtime.WorkerScript); start.ArgumentList.Add(request);
        start.Environment["YOLO_AUTOINSTALL"] = "False";
        start.Environment["YOLO_OFFLINE"] = "True";
        start.Environment["YOLO_CONFIG_DIR"] = TrainingPaths.Within(root, "worker-config");
        return await new StructuredTrainingProcess().RunAsync(start, TrainingPaths.Within(root, "worker.log"), progress,
            runtime.CancellationTimeout ?? TimeSpan.FromSeconds(8), runtime.JobTimeout, ct);
    }
}
