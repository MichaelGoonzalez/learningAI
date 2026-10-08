using System.Collections.Concurrent;
using HandRaise.Application.Inference;
using HandRaise.Domain.Analytics;
using HandRaise.Domain.Models;
using HandRaise.Infrastructure.Windows.Training;

namespace HandRaise.Infrastructure.Windows.Inference;

public sealed class StandardModelRegistry : IModelRegistry
{
    private readonly ConcurrentDictionary<string, ModelDescriptor> _models = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, IInferenceCapabilityProvider> _providers = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _customGate = new();
    private readonly string? _customDirectory;
    public string? CustomDirectory => _customDirectory;
    public IReadOnlyList<string> CustomModelLoadErrors { get; }

    public StandardModelRegistry(string? customDirectory = null)
    {
        _customDirectory = customDirectory == null ? null : Path.GetFullPath(customDirectory);
        var errors = new List<string>();
        if (_customDirectory != null && Directory.Exists(_customDirectory))
        {
            foreach (var directory in Directory.EnumerateDirectories(_customDirectory))
            {
                try
                {
                    var manifest = TrainingPaths.Within(_customDirectory, Path.GetFileName(directory), "model.json");
                    if (!File.Exists(manifest)) continue; // uncommitted artifact, never advertised
                    var model = TrainingJson.ReadAsync<ModelDescriptor>(manifest).GetAwaiter().GetResult();
                    ValidateCustom(model);
                    var artifact = TrainingPaths.Within(directory, "model.onnx");
                    if (!string.Equals(Path.GetFullPath(model.Path), artifact, StringComparison.OrdinalIgnoreCase) || !File.Exists(artifact))
                        throw new InvalidDataException("Falta el artefacto del modelo personalizado.");
                    using var stream = File.OpenRead(artifact);
                    var hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(stream));
                    if (!string.Equals(hash, model.Sha256, StringComparison.OrdinalIgnoreCase))
                        throw new InvalidDataException("El artefacto personalizado cambió después de su publicación.");
                    if (!_models.TryAdd(model.Id, model)) throw new InvalidDataException("ID de modelo personalizado duplicado.");
                }
                catch (Exception ex) when (ex is IOException or InvalidDataException or ArgumentException or System.Text.Json.JsonException or UnauthorizedAccessException)
                { errors.Add($"{Path.GetFileName(directory)}: {ex.Message}"); }
            }
        }
        CustomModelLoadErrors = errors;
    }

    public IReadOnlyList<ModelDescriptor> ListModels()
    {
        return _models.Values.ToList();
    }

    public ModelDescriptor? GetModel(string id)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        _models.TryGetValue(id, out var model);
        return model;
    }

    public IReadOnlyList<IInferenceCapabilityProvider> GetProviders()
    {
        return _providers.Values.ToList();
    }

    public IInferenceCapabilityProvider? ResolveProvider(InferenceCapability capability)
    {
        return _providers.Values.FirstOrDefault(p => p.Capabilities.Contains(capability));
    }

    public void RegisterModel(ModelDescriptor model, IInferenceCapabilityProvider? provider = null)
    {
        ArgumentNullException.ThrowIfNull(model);
        model.Validate();

        if (model.CustomTraining != null)
        {
            ValidateCustom(model);
            lock (_customGate)
            {
                if (_models.ContainsKey(model.Id)) throw new InvalidOperationException("No se puede sobrescribir un modelo personalizado publicado.");
                if (_customDirectory != null)
                {
                    var directory = TrainingPaths.Within(_customDirectory, model.Id);
                    var artifact = TrainingPaths.Within(directory, "model.onnx");
                    if (!string.Equals(Path.GetFullPath(model.Path), artifact, StringComparison.OrdinalIgnoreCase) || !File.Exists(artifact))
                        throw new InvalidDataException("El modelo debe residir dentro del almacenamiento del registro.");
                    var manifest = TrainingPaths.Within(directory, "model.json");
                    if (File.Exists(manifest)) throw new IOException("Esta versión ya fue publicada.");
                    TrainingJson.WriteAsync(manifest, model).GetAwaiter().GetResult();
                }
                _models[model.Id] = model;
            }
            // Metadata availability is not automatic live activation; no synthetic capability provider.
            return;
        }
        if (_models.TryGetValue(model.Id, out var existing) && existing.CustomTraining != null)
            throw new InvalidOperationException("No se puede reemplazar un modelo personalizado.");
        _models[model.Id] = model;

        if (provider != null)
        {
            _providers[provider.ProviderId] = provider;
        }
    }

    public bool UnregisterModel(string id)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        lock (_customGate)
        {
            if (_customDirectory != null && _models.TryGetValue(id, out var custom) && custom.CustomTraining != null)
            {
                var manifest = TrainingPaths.Within(_customDirectory, custom.Id, "model.json");
                if (File.Exists(manifest)) File.Move(manifest, manifest + $".{Guid.NewGuid():N}.removed");
            }
        }
        var removed = _models.TryRemove(id, out _);
        
        var matchingProviders = _providers.Values.Where(p => string.Equals(p.ModelId, id, StringComparison.OrdinalIgnoreCase)).ToList();
        foreach (var p in matchingProviders)
        {
            _providers.TryRemove(p.ProviderId, out _);
        }

        return removed;
    }

    private static void ValidateCustom(ModelDescriptor model)
    {
        model.Validate();
        if (model.CustomTraining == null || model.KeypointCount != 0 || model.ClassCount != model.CustomTraining.Classes.Count
            || model.Capabilities.Count != 1 || model.Capabilities[0] != InferenceCapability.ObjectDetection
            || model.CustomTraining.Classes.Select(c => c.Id).Distinct().Count() != model.ClassCount
            || model.CustomTraining.Classes.Any(c => c.Id == Guid.Empty || string.IsNullOrWhiteSpace(c.Name))
            || model.Id.Any(c => !char.IsAsciiLetterOrDigit(c) && c != '-'))
            throw new ArgumentException("Metadata de detector personalizado inválida.");
    }
}
