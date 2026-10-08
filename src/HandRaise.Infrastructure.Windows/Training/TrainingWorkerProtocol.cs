using System.Text.Json;
using HandRaise.Application.Training;
using HandRaise.Domain.Training;

namespace HandRaise.Infrastructure.Windows.Training;

public sealed record TrainingWorkerMessage(int ProtocolVersion, string Type, TrainingJobStatus? Stage = null,
    double? Percent = null, int? CurrentEpoch = null, int? TotalEpochs = null, string? Message = null,
    string? ModelPath = null, string? WorkerVersion = null, string? Device = null,
    Dictionary<string, double>? Metrics = null, string? ErrorCode = null, bool IsIndeterminate = false);

public static class TrainingWorkerProtocol
{
    public static TrainingWorkerMessage Parse(string line)
    {
        if (line.Length > 65536) throw new InvalidDataException("El mensaje del worker excede el límite.");
        TrainingWorkerMessage message;
        try { message = JsonSerializer.Deserialize<TrainingWorkerMessage>(line, TrainingJson.Options)
            ?? throw new InvalidDataException("Mensaje vacío del worker."); }
        catch (JsonException ex) { throw new InvalidDataException("El worker envió JSON inválido.", ex); }
        if (message.ProtocolVersion != 1 || message.Type is not ("progress" or "completed" or "error" or "capabilities"))
            throw new InvalidDataException("Versión o tipo de mensaje no compatible.");
        if (message.Type == "progress" && (message.Stage is not (TrainingJobStatus.Training or TrainingJobStatus.Validating or TrainingJobStatus.Exporting)
            || message.Percent is not { } percent || !double.IsFinite(percent) || percent is < 0 or > 100
            || message.CurrentEpoch < 0 || message.TotalEpochs <= 0 || message.CurrentEpoch > message.TotalEpochs))
            throw new InvalidDataException("Progreso del worker inválido.");
        if (message.Metrics?.Values.Any(v => !double.IsFinite(v)) == true) throw new InvalidDataException("Métricas inválidas.");
        if (message.Type == "completed" && (string.IsNullOrWhiteSpace(message.ModelPath) || string.IsNullOrWhiteSpace(message.WorkerVersion)
            || string.IsNullOrWhiteSpace(message.Device))) throw new InvalidDataException("Resultado incompleto del worker.");
        return message;
    }
}

public sealed record TrainingRuntimeOptions(string PythonExecutable, string WorkerScript, string BaseModelPath,
    TimeSpan? CancellationTimeout = null, TimeSpan? JobTimeout = null)
{
    public static TrainingRuntimeOptions FromManagedDirectory(TrainingPaths paths) => TrainingRuntimeProvisioner.Options(
        TrainingPaths.Within(paths.DataRoot, "training", "runtime", TrainingRuntimeProvisioner.Version));

    public void Validate()
    {
        foreach (var path in new[] { PythonExecutable, WorkerScript, BaseModelPath })
            if (!Path.IsPathFullyQualified(path) || !File.Exists(path))
                throw new TrainingWorkerException("training_runtime_missing", "Falta un componente del entorno de entrenamiento configurado.");
        if (!string.Equals(Path.GetFileName(BaseModelPath), "yolo26n.pt", StringComparison.OrdinalIgnoreCase))
            throw new TrainingWorkerException("unsupported_base_model", "V1 requiere el modelo base de detección yolo26n.pt.");
        if (CancellationTimeout <= TimeSpan.Zero || JobTimeout <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(JobTimeout));
    }
}
