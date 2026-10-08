namespace HandRaise.Domain.Training;

public enum TrainingProjectStatus { Draft, Preparing, Ready, Training, Completed, Failed }
public enum TrainingSourceType { Image, VideoFrame, CameraCapture }
public enum TrainingProfile { Quick, Balanced, HighAccuracy }
public enum TrainingDeviceKind { Cpu, Gpu }
public sealed record TrainingRuntimeIdentity(string Version, string ManifestSha256);
public sealed record TrainingDevice(TrainingDeviceKind Kind, int DeviceId = 0, string? DisplayName = null)
{
    public string WorkerDevice => Kind switch
    {
        TrainingDeviceKind.Cpu when DeviceId == 0 => "cpu",
        TrainingDeviceKind.Gpu when DeviceId >= 0 => "cuda:" + DeviceId.ToString(System.Globalization.CultureInfo.InvariantCulture),
        _ => throw new ArgumentException("Seleccione un dispositivo de entrenamiento válido.")
    };
}
public sealed record TrainingDeviceCapability(TrainingDevice Device, bool Available, long? MemoryBytes = null,
    string? UnavailableReason = null)
{
    public string Label => Device.Kind == TrainingDeviceKind.Cpu ? "CPU — Procesador del equipo"
        : Available ? $"GPU {Device.DeviceId + 1} — {Device.DisplayName}" : "GPU — No disponible para entrenamiento";
}
public enum TrainingResourcePolicy { PreferSystemAvailability, Balanced, TrainingPriority }
public enum TrainingJobStatus { Queued, PreparingDataset, Training, Validating, Exporting, Registering, Completed, Failed, Cancelled }

public sealed record TrainingClass(Guid Id, string Name);
public sealed record BoundingBoxAnnotation(Guid ClassId, double XCenter, double YCenter, double Width, double Height)
{
    public void Validate(IReadOnlyList<TrainingClass> classes)
    {
        if (!classes.Any(c => c.Id == ClassId)) throw new ArgumentException("La clase de la anotación no existe.");
        if (!double.IsFinite(XCenter) || !double.IsFinite(YCenter) || !double.IsFinite(Width) || !double.IsFinite(Height)
            || Width <= 0 || Height <= 0 || XCenter - Width / 2 < 0 || YCenter - Height / 2 < 0
            || XCenter + Width / 2 > 1 || YCenter + Height / 2 > 1)
            throw new ArgumentException("La caja debe tener tamaño positivo y quedar dentro de la imagen (0..1).");
    }
}

// The image is the V1 asset; video/camera producers supply decoded frames, never camera ownership.
public sealed record TrainingImage(Guid Id, Guid ProjectId, string OriginalFileName, string StoredPath,
    int Width, int Height, DateTimeOffset AddedAtUtc, TrainingSourceType SourceType, string Sha256,
    string? SourceId, double? SourceTimeSeconds, IReadOnlyList<BoundingBoxAnnotation> Annotations,
    bool Reviewed = false);
public sealed record TrainingProject(Guid Id, string Name, string Description, IReadOnlyList<TrainingClass> Classes,
    IReadOnlyList<TrainingImage> Images, TrainingProjectStatus Status, DateTimeOffset CreatedAtUtc, int Revision = 1)
{
    public DateTimeOffset? UpdatedAtUtc { get; init; }
    public void Validate()
    {
        if (Id == Guid.Empty || string.IsNullOrWhiteSpace(Name) || Name.Length > 200)
            throw new ArgumentException("El proyecto necesita un ID y un nombre válido (hasta 200 caracteres).");
        if (Classes.Count == 0 || Classes.Any(c => c.Id == Guid.Empty || string.IsNullOrWhiteSpace(c.Name) || c.Name.Length > 100)
            || Classes.Select(c => c.Id).Distinct().Count() != Classes.Count
            || Classes.Select(c => c.Name.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).Count() != Classes.Count)
            throw new ArgumentException("Defina clases con identificadores y nombres únicos.");
        if (Images.Select(i => i.Id).Distinct().Count() != Images.Count) throw new ArgumentException("Hay imágenes duplicadas por ID.");
        foreach (var image in Images)
        {
            if (image.Id == Guid.Empty || image.ProjectId != Id || image.Width <= 0 || image.Height <= 0)
                throw new ArgumentException("La imagen no pertenece al proyecto o tiene dimensiones inválidas.");
            foreach (var box in image.Annotations) box.Validate(Classes);
        }
    }
}

public sealed record TrainingConfiguration(int Epochs, int Batch, int ImageSize, int Patience, int Workers,
    string Device, int Seed, int CpuThreads);
public sealed record TrainingResourceWarning(string Code, string Message);
public sealed record TrainingProgress(TrainingJobStatus Stage, double Percent, int? CurrentEpoch = null,
    int? TotalEpochs = null, string? Message = null, IReadOnlyDictionary<string, double>? Metrics = null,
    string? Device = null, string? WorkerVersion = null)
{
    public bool IsIndeterminate { get; init; }
    public DateTimeOffset ActivityAtUtc { get; init; } = DateTimeOffset.UtcNow;
    public string ProcessState { get; init; } = "running";
    public string? LogPath { get; init; }
    public IReadOnlyList<string> RecentMessages { get; init; } = [];
}
public sealed record TrainingError(string ErrorCode, string FriendlyMessage, string? TechnicalDetails = null, string? LogPath = null);
public sealed record TrainingDataset(string SnapshotId, string YamlPath, IReadOnlyList<Guid> TrainImages,
    IReadOnlyList<Guid> ValidationImages, IReadOnlyList<string> Warnings);
public sealed record TrainingResult(string OnnxPath, string WorkerVersion, string Device,
    IReadOnlyDictionary<string, double> Metrics);
public sealed record TrainingJob(Guid Id, Guid ProjectId, TrainingJobStatus Status, TrainingProfile Profile,
    TrainingResourcePolicy ResourcePolicy, DateTimeOffset CreatedAtUtc, TrainingConfiguration Configuration,
    string BaseModel, TrainingProject Snapshot, DateTimeOffset? StartedAtUtc = null,
    DateTimeOffset? CompletedAtUtc = null, TrainingProgress? Progress = null, TrainingError? Error = null,
    TrainingDataset? Dataset = null, TrainingResult? Result = null, string? ModelId = null,
    IReadOnlyList<TrainingResourceWarning>? Warnings = null)
{
    public TrainingDevice? SelectedDevice { get; init; }
    public string? RuntimeVersion { get; init; }
    public string? RuntimeManifestSha256 { get; init; }
    public bool IsTerminal => Status is TrainingJobStatus.Completed or TrainingJobStatus.Failed or TrainingJobStatus.Cancelled;

    public TrainingJob Transition(TrainingJobStatus next)
    {
        var valid = !IsTerminal && (next is TrainingJobStatus.Failed or TrainingJobStatus.Cancelled || (Status, next) switch
        {
            (TrainingJobStatus.Queued, TrainingJobStatus.PreparingDataset) => true,
            (TrainingJobStatus.PreparingDataset, TrainingJobStatus.Training) => true,
            (TrainingJobStatus.Training, TrainingJobStatus.Validating) => true,
            (TrainingJobStatus.Validating, TrainingJobStatus.Exporting) => true,
            (TrainingJobStatus.Exporting, TrainingJobStatus.Registering) => true,
            (TrainingJobStatus.Registering, TrainingJobStatus.Completed) => true,
            _ => false
        });
        if (!valid) throw new InvalidOperationException($"Transición de entrenamiento inválida: {Status} → {next}.");
        return this with { Status = next,
            StartedAtUtc = StartedAtUtc ?? (next == TrainingJobStatus.PreparingDataset ? DateTimeOffset.UtcNow : null),
            CompletedAtUtc = next is TrainingJobStatus.Completed or TrainingJobStatus.Failed or TrainingJobStatus.Cancelled
                ? DateTimeOffset.UtcNow : null };
    }
}

public sealed record CustomModelMetadata(Guid TrainingProjectId, Guid TrainingJobId, string Description,
    IReadOnlyList<TrainingClass> Classes, DateTimeOffset CreatedAtUtc, string BaseModel,
    string DatasetSnapshotId, string WorkerVersion, IReadOnlyDictionary<string, double> ValidationMetrics);
