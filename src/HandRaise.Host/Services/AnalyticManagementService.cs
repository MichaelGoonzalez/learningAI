using HandRaise.Application.Analytics;
using HandRaise.Application.Lines;
using HandRaise.Domain.Analytics;
using Microsoft.Extensions.DependencyInjection;

namespace HandRaise.Host.Services;

public sealed class AnalyticManagementService : IAnalyticManagementService
{
    private readonly ICameraStore _cameraStore;
    private readonly IAnalyticInstanceStore _analyticStore;
    private readonly IAnalyticCatalog _catalog;
    private readonly ILineStore _lineStore;
    private readonly IServiceProvider _serviceProvider;

    public AnalyticManagementService(
        ICameraStore cameraStore,
        IAnalyticInstanceStore analyticStore,
        IAnalyticCatalog catalog,
        ILineStore lineStore,
        IServiceProvider serviceProvider)
    {
        _cameraStore = cameraStore;
        _analyticStore = analyticStore;
        _catalog = catalog;
        _lineStore = lineStore;
        _serviceProvider = serviceProvider;
    }

    public IReadOnlyList<AnalyticDefinition> GetCatalog() => _catalog.GetAll();

    public async Task<IReadOnlyList<CameraAnalyticInstance>?> ListByCameraAsync(
        string cameraId, CancellationToken token = default)
    {
        if (string.IsNullOrWhiteSpace(cameraId)) return null;
        var camera = await _cameraStore.GetAsync(cameraId, token);
        if (camera == null) return null;

        return await _analyticStore.GetByCameraIdAsync(cameraId, token);
    }

    public async Task<CameraAnalyticInstance?> GetAsync(
        string cameraId, string instanceId, CancellationToken token = default)
    {
        if (string.IsNullOrWhiteSpace(cameraId) || string.IsNullOrWhiteSpace(instanceId)) return null;
        var camera = await _cameraStore.GetAsync(cameraId, token);
        if (camera == null) return null;

        var instance = await _analyticStore.GetByIdAsync(instanceId, token);
        if (instance == null || !string.Equals(instance.CameraId, cameraId, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        return instance;
    }

    public async Task<CameraAnalyticInstance> CreateAsync(
        string cameraId, CameraAnalyticWriteRequest request, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(cameraId))
            throw new ArgumentException("CameraId no puede estar vacío.", nameof(cameraId));

        var camera = await _cameraStore.GetAsync(cameraId, token);
        if (camera == null)
            throw new KeyNotFoundException($"La cámara '{cameraId}' no existe.");

        if (string.IsNullOrWhiteSpace(request.AnalyticTypeId))
            throw new ArgumentException("El tipo de analítica (analytic_type_id) es obligatorio.");

        var definition = _catalog.GetById(request.AnalyticTypeId);
        if (definition == null)
            throw new ArgumentException($"El tipo de analítica '{request.AnalyticTypeId}' no existe en el catálogo.");

        if (request.Enabled != false) ValidateCustomModel(request.AnalyticTypeId, request.Configuration);
        var (isValid, errorMsg) = _catalog.ValidateConfiguration(request.AnalyticTypeId, request.Configuration);
        if (!isValid)
            throw new ArgumentException(errorMsg);

        if (string.Equals(request.AnalyticTypeId.Trim(), "zone_intrusion", StringComparison.OrdinalIgnoreCase))
        {
            if (request.AssignedZoneIds == null || request.AssignedZoneIds.Count == 0 || request.AssignedZoneIds.All(string.IsNullOrWhiteSpace))
            {
                throw new ArgumentException("La analítica 'zone_intrusion' requiere al menos una zona configurada en assigned_zone_ids.");
            }

            var cameraZoneNames = (camera.Zones ?? []).Select(z => z.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var zoneId in request.AssignedZoneIds)
            {
                if (!string.IsNullOrWhiteSpace(zoneId) && !cameraZoneNames.Contains(zoneId.Trim()))
                {
                    throw new ArgumentException($"La zona '{zoneId}' asignada a 'zone_intrusion' no existe en la cámara '{cameraId}'.");
                }
            }
        }

        if (string.Equals(request.AnalyticTypeId.Trim(), "line_crossing", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(request.AnalyticTypeId.Trim(), "person_counting", StringComparison.OrdinalIgnoreCase))
        {
            if (request.AssignedLineIds == null || request.AssignedLineIds.Count == 0 || request.AssignedLineIds.All(string.IsNullOrWhiteSpace))
            {
                throw new ArgumentException($"La analítica '{request.AnalyticTypeId}' requiere al menos una línea configurada en assigned_line_ids.");
            }

            var cameraLines = await _lineStore.ListByCameraAsync(cameraId, token);
            var cameraLineIds = cameraLines.Select(l => l.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var lineId in request.AssignedLineIds)
            {
                if (!string.IsNullOrWhiteSpace(lineId) && !cameraLineIds.Contains(lineId.Trim()))
                {
                    throw new ArgumentException($"La línea '{lineId}' asignada a '{request.AnalyticTypeId}' no existe en la cámara '{cameraId}'.");
                }
            }
        }

        var id = string.IsNullOrWhiteSpace(request.Id)
            ? $"an-{cameraId}-{request.AnalyticTypeId.Trim()}-{Guid.NewGuid().ToString("N")[..6]}"
            : request.Id.Trim();

        var existing = await _analyticStore.GetByIdAsync(id, token);
        if (existing != null)
            throw new InvalidOperationException($"La instancia analítica '{id}' ya existe.");

        var enabled = request.Enabled ?? true;
        var name = string.IsNullOrWhiteSpace(request.Name) ? definition.DisplayName : request.Name.Trim();
        var now = DateTimeOffset.UtcNow;

        var instance = new CameraAnalyticInstance(
            Id: id,
            CameraId: cameraId,
            AnalyticTypeId: request.AnalyticTypeId.Trim(),
            Name: name,
            Enabled: enabled,
            Status: enabled ? AnalyticStatus.Active : AnalyticStatus.Inactive,
            Configuration: request.Configuration,
            AssignedZoneIds: request.AssignedZoneIds ?? [],
            AssignedLineIds: request.AssignedLineIds ?? [],
            CreatedAt: now,
            UpdatedAt: now);

        await _analyticStore.SaveAsync(instance, token);

        await RefreshEngineAsync(cameraId, token);

        return instance;
    }

    public async Task<CameraAnalyticInstance?> UpdateAsync(
        string cameraId, string instanceId, CameraAnalyticWriteRequest request, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(cameraId) || string.IsNullOrWhiteSpace(instanceId)) return null;

        var camera = await _cameraStore.GetAsync(cameraId, token);
        if (camera == null) return null;

        var existing = await _analyticStore.GetByIdAsync(instanceId, token);
        if (existing == null || !string.Equals(existing.CameraId, cameraId, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        if (request.Enabled ?? existing.Enabled) ValidateCustomModel(existing.AnalyticTypeId, request.Configuration ?? existing.Configuration);
        if (request.Configuration != null)
        {
            var (isValid, errorMsg) = _catalog.ValidateConfiguration(existing.AnalyticTypeId, request.Configuration);
            if (!isValid)
                throw new ArgumentException(errorMsg);
        }

        if (string.Equals(existing.AnalyticTypeId, "zone_intrusion", StringComparison.OrdinalIgnoreCase))
        {
            var targetZones = request.AssignedZoneIds ?? existing.AssignedZoneIds;
            if (targetZones == null || targetZones.Count == 0 || targetZones.All(string.IsNullOrWhiteSpace))
            {
                throw new ArgumentException("La analítica 'zone_intrusion' requiere al menos una zona configurada en assigned_zone_ids.");
            }

            var cameraZoneNames = (camera.Zones ?? []).Select(z => z.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var zoneId in targetZones)
            {
                if (!string.IsNullOrWhiteSpace(zoneId) && !cameraZoneNames.Contains(zoneId.Trim()))
                {
                    throw new ArgumentException($"La zona '{zoneId}' asignada a 'zone_intrusion' no existe en la cámara '{cameraId}'.");
                }
            }
        }

        if (string.Equals(existing.AnalyticTypeId, "line_crossing", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(existing.AnalyticTypeId, "person_counting", StringComparison.OrdinalIgnoreCase))
        {
            var targetLines = request.AssignedLineIds ?? existing.AssignedLineIds;
            if (targetLines == null || targetLines.Count == 0 || targetLines.All(string.IsNullOrWhiteSpace))
            {
                throw new ArgumentException($"La analítica '{existing.AnalyticTypeId}' requiere al menos una línea configurada en assigned_line_ids.");
            }

            var cameraLines = await _lineStore.ListByCameraAsync(cameraId, token);
            var cameraLineIds = cameraLines.Select(l => l.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var lineId in targetLines)
            {
                if (!string.IsNullOrWhiteSpace(lineId) && !cameraLineIds.Contains(lineId.Trim()))
                {
                    throw new ArgumentException($"La línea '{lineId}' asignada a '{existing.AnalyticTypeId}' no existe en la cámara '{cameraId}'.");
                }
            }
        }

        var enabled = request.Enabled ?? existing.Enabled;
        var name = string.IsNullOrWhiteSpace(request.Name) ? existing.Name : request.Name.Trim();
        var config = request.Configuration ?? existing.Configuration;
        var zones = request.AssignedZoneIds ?? existing.AssignedZoneIds;
        var lines = request.AssignedLineIds ?? existing.AssignedLineIds;
        var status = enabled ? AnalyticStatus.Active : AnalyticStatus.Inactive;

        var updated = existing with
        {
            Name = name,
            Enabled = enabled,
            Status = status,
            Configuration = config,
            AssignedZoneIds = zones,
            AssignedLineIds = lines,
            UpdatedAt = DateTimeOffset.UtcNow
        };

        await _analyticStore.SaveAsync(updated, token);

        await RefreshEngineAsync(cameraId, token);

        return updated;
    }

    public async Task<bool> DeleteAsync(
        string cameraId, string instanceId, CancellationToken token = default)
    {
        if (string.IsNullOrWhiteSpace(cameraId) || string.IsNullOrWhiteSpace(instanceId)) return false;

        var camera = await _cameraStore.GetAsync(cameraId, token);
        if (camera == null) return false;

        var existing = await _analyticStore.GetByIdAsync(instanceId, token);
        if (existing == null || !string.Equals(existing.CameraId, cameraId, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var deleted = await _analyticStore.DeleteAsync(instanceId, token);
        if (deleted)
        {
            await RefreshEngineAsync(cameraId, token);
        }

        return deleted;
    }

    private async Task RefreshEngineAsync(string cameraId, CancellationToken token)
    {
        var engine = _serviceProvider.GetService<HeadlessEngineService>();
        if (engine != null)
        {
            try
            {
                await engine.RefreshCameraEvaluatorsAsync(cameraId, token);
            }
            catch
            {
                // Ignored if engine is shutting down or not available
            }
        }
    }

    private void ValidateCustomModel(string type, IReadOnlyDictionary<string, object?>? configuration)
    {
        if (!CustomObjectEvaluator.IsCustom(type)) return;
        var id = configuration?.GetValueOrDefault("model_id")?.ToString();
        var registry = _serviceProvider.GetService<HandRaise.Application.Inference.IModelRegistry>();
        if (string.IsNullOrWhiteSpace(id) || registry?.GetModel(id)?.CustomTraining == null)
            throw new ArgumentException("VisionControl no pudo cargar este modelo. Seleccione una versión disponible.");
    }
}
