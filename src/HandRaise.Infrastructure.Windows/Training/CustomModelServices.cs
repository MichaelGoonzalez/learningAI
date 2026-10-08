using System.Security.Cryptography;
using System.Text.Json;
using HandRaise.Application.Hardware;
using HandRaise.Application.Inference;
using HandRaise.Application.Training;
using HandRaise.Domain.Analytics;
using HandRaise.Domain.Models;
using HandRaise.Domain.Training;
using HandRaise.Infrastructure.Windows.Inference;
using Microsoft.ML.OnnxRuntime;

namespace HandRaise.Infrastructure.Windows.Training;

public sealed class OnnxTrainingArtifactValidator : ITrainingArtifactValidator
{
    public Task ValidateAsync(string path, int inputSize, IReadOnlyList<TrainingClass> classes, CancellationToken ct) => Task.Run(() =>
    {
        ct.ThrowIfCancellationRequested();
        if (!File.Exists(path) || new FileInfo(path).Length == 0) throw new InvalidDataException("No se encontró el ONNX exportado.");
        using var options = new SessionOptions { IntraOpNumThreads = 1, InterOpNumThreads = 1 };
        using var session = new InferenceSession(path, options); // explicit default CPU; never starts DirectML
        if (session.InputNames.Count != 1 || session.OutputNames.Count != 1) throw new InvalidDataException("Se requiere una entrada y una salida ONNX.");
        var input = session.InputMetadata[session.InputNames[0]];
        var output = session.OutputMetadata[session.OutputNames[0]];
        ValidateShape(input.Dimensions, output.Dimensions, inputSize, classes.Count,
            input.ElementType == typeof(float) && output.ElementType == typeof(float));
        var metadata = session.ModelMetadata.CustomMetadataMap;
        if (!metadata.TryGetValue("visioncontrol_classes", out var names)
            || !(JsonSerializer.Deserialize<string[]>(names) ?? []).SequenceEqual(classes.Select(c => c.Name))
            || !metadata.TryGetValue("task", out var task) || task != "detect")
            throw new InvalidDataException("La metadata ONNX no coincide con las clases del proyecto o la tarea de detección.");
        using var tensor = OrtValue.CreateTensorValueFromMemory(new float[checked(3 * inputSize * inputSize)], [1, 3, inputSize, inputSize]);
        using var run = new RunOptions();
        using var registration = ct.Register(() => run.Terminate = true);
        using var results = session.Run(run, new Dictionary<string, OrtValue> { [session.InputNames[0]] = tensor }, session.OutputNames);
        foreach (var value in results[0].GetTensorDataAsSpan<float>())
            if (!float.IsFinite(value)) throw new InvalidDataException("La inferencia de validación produjo valores no finitos.");
        ct.ThrowIfCancellationRequested();
    }, ct);

    public static void ValidateShape(IReadOnlyList<int> input, IReadOnlyList<int> output, int size, int classes, bool float32)
    {
        if (classes <= 0 || !float32 || !input.SequenceEqual(new[] { 1, 3, size, size }) || output.Count != 3
            || output[0] != 1 || output[1] != 4 + classes || output[2] <= 6)
            throw new InvalidDataException("ONNX incompatible: se requiere float32 [1,3,S,S] → [1,4+clases,N], sin NMS integrado.");
    }
}

public sealed class TrainingModelPublisher(TrainingPaths paths, IModelRegistry registry, ITrainingArtifactValidator validator) : ITrainingModelPublisher
{
    public ModelDescriptor? FindPublishedModel(Guid jobId) => registry.ListModels().FirstOrDefault(m => m.CustomTraining?.TrainingJobId == jobId);
    public async Task<ModelDescriptor> PublishAsync(TrainingJob job, CancellationToken ct)
    {
        var previous = FindPublishedModel(job.Id);
        if (previous != null) return previous; // idempotent recovery after publication
        var result = job.Result ?? throw new InvalidOperationException("Falta el resultado del entrenamiento.");
        var outputRoot = TrainingPaths.Within(paths.Job(job.Id), "output");
        var source = TrainingPaths.Within(outputRoot, Path.GetRelativePath(outputRoot, result.OnnxPath));
        await validator.ValidateAsync(source, job.Configuration.ImageSize, job.Snapshot.Classes, ct);
        Directory.CreateDirectory(paths.CustomModels);
        // Exclusive lease covers version allocation and registry commit across Edge processes.
        await using var lease = new FileStream(TrainingPaths.Within(paths.CustomModels, "publication.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        var versions = registry.ListModels().Where(m => m.CustomTraining?.TrainingProjectId == job.ProjectId)
            .Select(m => int.TryParse(m.Version, out var v) ? v : 0);
        var version = versions.DefaultIfEmpty(0).Max() + 1;
        // Retain the monotonic counter even if models are explicitly unregistered later.
        var counterPath = TrainingPaths.Within(paths.CustomModels, $"{job.ProjectId:N}.version.json");
        if (File.Exists(counterPath)) version = Math.Max(version, await TrainingJson.ReadAsync<int>(counterPath, ct) + 1);
        await TrainingJson.WriteAsync(counterPath, version, ct);
        var id = $"custom-{job.ProjectId:N}-v{version}";
        var directory = TrainingPaths.Within(paths.CustomModels, id);
        Directory.CreateDirectory(directory);
        var destination = TrainingPaths.Within(directory, "model.onnx");
        await using (var input = File.OpenRead(source))
        await using (var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            await input.CopyToAsync(output, ct);
        // Validate the copied artifact, not an external path that could change during publication.
        await validator.ValidateAsync(destination, job.Configuration.ImageSize, job.Snapshot.Classes, ct);
        string hash;
        await using (var stream = File.OpenRead(destination)) hash = Convert.ToHexString(await SHA256.HashDataAsync(stream, ct)).ToLowerInvariant();
        var model = new ModelDescriptor(id, job.Snapshot.Name, version.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ModelFormat.Onnx, [InferenceCapability.ObjectDetection], job.Configuration.ImageSize, job.Configuration.ImageSize,
            destination, ClassCount: job.Snapshot.Classes.Count, KeypointCount: 0, Sha256: hash)
        { CustomTraining = new(job.ProjectId, job.Id, job.Snapshot.Description, job.Snapshot.Classes, DateTimeOffset.UtcNow,
            job.BaseModel, job.Dataset!.SnapshotId, result.WorkerVersion, result.Metrics) };
        ct.ThrowIfCancellationRequested();
        registry.RegisterModel(model); // registry persists the manifest as the final publication marker
        return model;
    }
}

public sealed record CustomModelDetection(Guid ClassId, string ClassName, float Confidence, BoundingBox Box);

public sealed class CustomModelTestService(IModelRegistry registry)
{
    public Task<IReadOnlyList<CustomModelDetection>> TestAsync(string modelId, ImageFrame image, CancellationToken ct = default) => Task.Run(async () =>
    {
        var model = registry.GetModel(modelId) ?? throw new ArgumentException("No se encontró el modelo.");
        var metadata = model.CustomTraining ?? throw new ArgumentException("Seleccione un detector personalizado.");
        await new OnnxTrainingArtifactValidator().ValidateAsync(model.Path, model.InputWidth, metadata.Classes, ct);
        await using var backend = new WindowsMlOnnxBackend(new DeviceInfo("cpu", "CPU", HardwareVendor.Cpu,
            InferenceBackend.Cpu, null, null, null, true));
        await backend.LoadAsync(model, ct);
        var result = await backend.InferAsync(image, ct);
        return (IReadOnlyList<CustomModelDetection>)result.People.Select(d => new CustomModelDetection(
            metadata.Classes[d.ClassId].Id, metadata.Classes[d.ClassId].Name, d.Confidence, d.Box)).ToArray();
    }, ct);
}

/// <summary>Desktop composition seam: call once with the SAME registry used by the embedded host.</summary>
public static class TrainingCore
{
    public static async Task<TrainingApplicationService> CreateAsync(IModelRegistry registry, TrainingPaths? paths = null,
        TrainingRuntimeOptions? runtime = null, Func<int>? activeCameraCount = null, CancellationToken ct = default)
    {
        paths ??= new TrainingPaths();
        if (registry is not StandardModelRegistry standard || !string.Equals(standard.CustomDirectory, paths.CustomModels, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("TrainingCore necesita el registro existente con persistencia en el mismo directorio de modelos personalizados.", nameof(registry));
        var lockPath = TrainingPaths.Within(paths.DataRoot, "training", "owner.lock");
        Directory.CreateDirectory(Path.GetDirectoryName(lockPath)!);
        var ownership = new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        try
        {
            var service = new TrainingApplicationService(new JsonTrainingStore(paths), new TrainingAssetService(paths),
                new YoloDatasetExporter(paths, new DatasetSplitService()), new PythonYoloTrainingWorker(paths,
                    runtime ?? TrainingRuntimeOptions.FromManagedDirectory(paths)),
                new TrainingModelPublisher(paths, registry, new OnnxTrainingArtifactValidator()), new TrainingResourceMonitor(activeCameraCount), ownership);
            await service.RecoverInterruptedJobsAsync(ct);
            return service;
        }
        catch { ownership.Dispose(); throw; }
    }
}
