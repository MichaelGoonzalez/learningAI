using System.Diagnostics;
using System.Text.Json;
using HandRaise.Application.Training;
using HandRaise.Domain.Training;

namespace HandRaise.Infrastructure.Windows.Training;

public sealed class ManagedTrainingRuntimeProbe : ITrainingRuntimeProbe
{
    public async Task<IReadOnlyList<TrainingDeviceCapability>> ProbeAsync(TrainingRuntimeOptions runtime, CancellationToken ct)
    {
        runtime.Validate();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromMinutes(2));
        var start = new ProcessStartInfo(runtime.PythonExecutable) { UseShellExecute = false, CreateNoWindow = true,
            WorkingDirectory = Path.GetDirectoryName(runtime.WorkerScript)!, RedirectStandardInput = true,
            RedirectStandardOutput = true, RedirectStandardError = true };
        start.ArgumentList.Add("-I"); start.ArgumentList.Add("-B"); start.ArgumentList.Add("-u");
        start.ArgumentList.Add(runtime.WorkerScript); start.ArgumentList.Add("--probe");
        start.Environment["YOLO_OFFLINE"] = "True"; start.Environment["YOLO_AUTOINSTALL"] = "False";
        start.Environment["YOLO_CONFIG_DIR"] = Path.Combine(Path.GetDirectoryName(runtime.WorkerScript)!, "settings");
        using var process = new Process { StartInfo = start };
        ct.ThrowIfCancellationRequested(); process.Start();
        Task<string>? stdout = null, stderr = null;
        try
        {
            using var lease = new WindowsTrainingProcessLease(process);
            stdout = process.StandardOutput.ReadToEndAsync(timeout.Token); stderr = process.StandardError.ReadToEndAsync(timeout.Token);
            await process.StandardInput.WriteLineAsync("{\"type\":\"start\",\"protocol_version\":1}");
            await process.StandardInput.FlushAsync();
            await process.WaitForExitAsync(timeout.Token);
            if (process.ExitCode != 0) throw new TrainingWorkerException("training_runtime_invalid", "No se pudo validar el entorno de entrenamiento.");
            var result = JsonSerializer.Deserialize<ProbeResult>(await stdout, TrainingJson.Options)
                ?? throw new InvalidDataException("Respuesta de entorno vacía.");
            if (result.WorkerVersion != "2.0.0" || !result.CpuAvailable || result.Devices.Count == 0)
                throw new InvalidDataException("El entorno no supera la validación de componentes.");
            return result.Devices;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        { throw new TrainingWorkerException("training_runtime_timeout", "La comprobación del entorno tardó demasiado. Vuelva a comprobar."); }
        finally
        {
            if (!process.HasExited) { process.Kill(true); await process.WaitForExitAsync(); }
            if (stdout != null && stderr != null) try { await Task.WhenAll(stdout, stderr); } catch (OperationCanceledException) { }
        }
    }
    private sealed record ProbeResult(string WorkerVersion, bool CpuAvailable, IReadOnlyList<TrainingDeviceCapability> Devices);
}

/// <summary>Detects training devices without importing Ultralytics/ONNX or loading the base model.</summary>
public sealed class ManagedTrainingRuntimeDeviceProbe : ITrainingRuntimeDeviceProbe
{
    private const string Script = """
import json
import torch

devices = [{"device": {"kind": "cpu", "device_id": 0, "display_name": "Procesador del equipo"}, "available": True}]
if torch.cuda.is_available():
    for index in range(torch.cuda.device_count()):
        try:
            name = torch.cuda.get_device_name(index)
            value = torch.ones(1, device=f"cuda:{index}", requires_grad=True)
            (value * 2).sum().backward()
            torch.cuda.synchronize(index)
            with torch.cuda.device(index):
                free, _ = torch.cuda.mem_get_info()
            devices.append({"device": {"kind": "gpu", "device_id": index, "display_name": name}, "available": True, "memory_bytes": free})
        except Exception as error:
            devices.append({"device": {"kind": "gpu", "device_id": index}, "available": False, "unavailable_reason": str(error)[:500]})
else:
    devices.append({"device": {"kind": "gpu", "device_id": 0}, "available": False, "unavailable_reason": "CUDA no está disponible en el runtime de entrenamiento."})
print(json.dumps({"devices": devices}))
""";

    public async Task<IReadOnlyList<TrainingDeviceCapability>> ProbeDevicesAsync(TrainingRuntimeOptions runtime, CancellationToken ct)
    {
        runtime.Validate();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromMinutes(2));
        var start = new ProcessStartInfo(runtime.PythonExecutable)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = Path.GetDirectoryName(runtime.WorkerScript)!,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        start.ArgumentList.Add("-I"); start.ArgumentList.Add("-B"); start.ArgumentList.Add("-u");
        start.ArgumentList.Add("-c"); start.ArgumentList.Add(Script);
        start.Environment["PYTHONNOUSERSITE"] = "1";
        using var process = new Process { StartInfo = start };
        ct.ThrowIfCancellationRequested(); process.Start();
        Task<string>? stdout = null, stderr = null;
        try
        {
            using var lease = new WindowsTrainingProcessLease(process);
            stdout = process.StandardOutput.ReadToEndAsync(timeout.Token);
            stderr = process.StandardError.ReadToEndAsync(timeout.Token);
            await process.WaitForExitAsync(timeout.Token);
            var error = await stderr;
            if (process.ExitCode != 0)
                throw new InvalidDataException("El probe de dispositivos terminó con error. "
                    + (error.Length > 4096 ? error[..4096] : error));
            var result = JsonSerializer.Deserialize<DeviceProbeResult>(await stdout, TrainingJson.Options)
                ?? throw new InvalidDataException("Respuesta de dispositivos vacía.");
            if (!result.Devices.Any(device => device.Device.Kind == TrainingDeviceKind.Cpu && device.Available))
                throw new InvalidDataException("El runtime no confirmó disponibilidad de CPU.");
            return result.Devices;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        { throw new TrainingWorkerException("training_device_probe_timeout", "La comprobación de GPU tardó demasiado. Puede reintentar."); }
        finally
        {
            if (!process.HasExited) { process.Kill(true); await process.WaitForExitAsync(); }
            if (stdout != null && stderr != null) try { await Task.WhenAll(stdout, stderr); } catch (OperationCanceledException) { }
        }
    }

    private sealed record DeviceProbeResult(IReadOnlyList<TrainingDeviceCapability> Devices);
}

public static class TrainingDeviceSelection
{
    public static void RequireAvailable(TrainingDevice device, IReadOnlyList<TrainingDeviceCapability> devices)
    {
        _ = device.WorkerDevice;
        if (!devices.Any(d => d.Available && d.Device.Kind == device.Kind && d.Device.DeviceId == device.DeviceId
            && (device.Kind == TrainingDeviceKind.Cpu || device.DisplayName == null || device.DisplayName == d.Device.DisplayName)))
            throw new TrainingWorkerException(device.Kind == TrainingDeviceKind.Gpu ? "training_gpu_unavailable" : "training_cpu_unavailable",
                device.Kind == TrainingDeviceKind.Gpu ? "La GPU seleccionada ya no está disponible." : "El procesador no está disponible para entrenamiento.");
    }
}
