using HandRaise.Domain.Training;
using HandRaise.Application.Inference;

namespace HandRaise.Application.Training;

public interface ITrainingStore
{
    Task<IReadOnlyList<TrainingProject>> ListProjectsAsync(CancellationToken ct = default);
    Task<TrainingProject> LoadProjectAsync(Guid id, CancellationToken ct = default);
    Task SaveProjectAsync(TrainingProject project, CancellationToken ct = default);
    Task DeleteProjectAsync(Guid id, CancellationToken ct = default);
    Task<IReadOnlyList<TrainingJob>> ListJobsAsync(CancellationToken ct = default);
    Task SaveJobAsync(TrainingJob job, CancellationToken ct = default);
    Task<TrainingJob> LoadJobAsync(Guid id, CancellationToken ct = default);
}
public interface ITrainingAssets
{
    Task<TrainingImage> ImportAsync(TrainingProject project, string path, TrainingSourceType source,
        string? sourceId, double? sourceTimeSeconds, CancellationToken ct);
    Task<TrainingImage> ImportFrameAsync(TrainingProject project, ImageFrame frame, TrainingSourceType source,
        string sourceId, double? sourceTimeSeconds, CancellationToken ct);
}
public interface ITrainingDatasetExporter
{
    Task<TrainingDataset> ExportAsync(TrainingJob job, CancellationToken ct);
}
public interface ITrainingWorker
{
    Task<TrainingResult> RunAsync(TrainingJob job, Func<TrainingProgress, Task> progress, CancellationToken ct);
}
public interface ITrainingDeviceValidator
{
    Task<TrainingRuntimeIdentity> ValidateDeviceAsync(TrainingDevice device, CancellationToken ct);
}
public interface ITrainingModelPublisher
{
    Task<ModelDescriptor> PublishAsync(TrainingJob job, CancellationToken ct);
    ModelDescriptor? FindPublishedModel(Guid jobId);
}
public interface ITrainingArtifactValidator
{
    Task ValidateAsync(string path, int inputSize, IReadOnlyList<TrainingClass> classes, CancellationToken ct);
}
public interface ITrainingResourceMonitor
{
    Task<IReadOnlyList<TrainingResourceWarning>> AssessAsync(TrainingResourcePolicy policy, CancellationToken ct);
}
public sealed class TrainingWorkerException(string code, string message, string? logPath = null) : Exception(message)
{
    public string Code { get; } = code;
    public string? LogPath { get; } = logPath;
}

public static class TrainingProfiles
{
    public const string BaseModel = "yolo26n";
    public static TrainingConfiguration Resolve(TrainingProfile profile, TrainingResourcePolicy policy, int seed = 42)
    {
        if (!Enum.IsDefined(policy)) throw new ArgumentOutOfRangeException(nameof(policy));
        var epochs = profile switch { TrainingProfile.Quick => 20, TrainingProfile.Balanced => 60,
            TrainingProfile.HighAccuracy => 120, _ => throw new ArgumentOutOfRangeException(nameof(profile)) };
        return new(epochs, policy == TrainingResourcePolicy.PreferSystemAvailability ? 4 : 8,
            640, 15, 0, "cpu", seed,
            policy switch { TrainingResourcePolicy.PreferSystemAvailability => 2,
                TrainingResourcePolicy.Balanced => Math.Max(2, Environment.ProcessorCount / 2),
                _ => Math.Max(2, Environment.ProcessorCount) });
    }
}

public sealed class DatasetSplitService
{
    public (IReadOnlyList<TrainingImage> Train, IReadOnlyList<TrainingImage> Validation, IReadOnlyList<string> Warnings)
        Split(TrainingProject project, int seed = 42, double validationFraction = .2)
    {
        project.Validate();
        if (!double.IsFinite(validationFraction) || validationFraction <= 0 || validationFraction >= 1)
            throw new ArgumentOutOfRangeException(nameof(validationFraction));
        if (project.Images.Any(i => !i.Reviewed)) throw new ArgumentException("Revise las anotaciones de todas las imágenes; confirme también los ejemplos sin objetos.");
        // Entire source groups stay together: conservative leakage protection for video and bursts.
        var groups = project.Images.GroupBy(i => i.SourceId is { Length: > 0 } s ? "source:" + s : "image:" + i.Id)
            .OrderBy(g => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
                System.Text.Encoding.UTF8.GetBytes($"{seed}:{g.Key}"))), StringComparer.Ordinal).ToArray();
        if (groups.Length < 2) throw new ArgumentException("Se necesitan al menos dos orígenes independientes para separar entrenamiento y validación.");
        var count = Math.Clamp((int)Math.Round(groups.Length * validationFraction), 1, groups.Length - 1);
        var validation = groups.Take(count).SelectMany(g => g).OrderBy(i => i.Id).ToArray();
        var train = groups.Skip(count).SelectMany(g => g).OrderBy(i => i.Id).ToArray();
        var warnings = new List<string>();
        if (project.Images.Count < 20) warnings.Add("Hay pocos ejemplos; la calidad y las métricas pueden ser poco representativas.");
        foreach (var cls in project.Classes)
        {
            if (!train.Any(i => i.Annotations.Any(a => a.ClassId == cls.Id)))
                throw new ArgumentException($"La clase '{cls.Name}' no tiene ejemplos en entrenamiento. Agregue ejemplos de otros orígenes.");
            if (!validation.Any(i => i.Annotations.Any(a => a.ClassId == cls.Id)))
                warnings.Add($"La clase '{cls.Name}' no tiene ejemplos en validación.");
        }
        return (train, validation, warnings);
    }
}
