using System.IO.Compression;
using System.Security.Cryptography;
using HandRaise.Application.Training;
using HandRaise.Domain.Training;

namespace HandRaise.Infrastructure.Windows.Training;

public enum TrainingRuntimeState { NotInstalled, Checking, Preparing, Ready, Error }
public sealed record TrainingRuntimeProgress(TrainingRuntimeState State, double Percent, string Message, long? TotalBytes = null,
    long? DownloadedBytes = null);
public sealed record TrainingRuntimeStatus(TrainingRuntimeState State, IReadOnlyList<TrainingDeviceCapability> Devices,
    string Message, string? Detail = null, bool CanResume = false, bool RequiresPreparation = false,
    bool CapabilitiesAreLastKnown = false, bool RequiresDeviceProbe = false,
    bool DeviceAvailabilityConfirmed = false, bool DeviceProbeFailed = false);
public sealed record TrainingRuntimeArtifact(string Name, string Url, string Sha256, long? Size, string Kind);
public sealed record TrainingRuntimeManifest(string Version, string RecipeHash, IReadOnlyList<TrainingRuntimeArtifact> Artifacts);
public sealed record TrainingRuntimeReceipt(TrainingRuntimeManifest Manifest, IReadOnlyDictionary<string, string> Files)
{
    public string? WorkerVersion { get; init; }
    public bool? InstallationCompleted { get; init; }
    public DateTimeOffset? DeepValidatedAtUtc { get; init; }
    public IReadOnlyList<TrainingDeviceCapability>? LastKnownDevices { get; init; }
    public DateTimeOffset? DevicesDetectedAtUtc { get; init; }
}

public static class TrainingRuntimeManifestContracts
{
    private static readonly HashSet<string> AllowedHosts = new(StringComparer.OrdinalIgnoreCase)
    { "www.python.org", "files.pythonhosted.org", "download.pytorch.org", "download-r2.pytorch.org", "github.com" };
    public static void ValidateBasic(TrainingRuntimeManifest manifest)
    {
        if (string.IsNullOrWhiteSpace(manifest.Version) || manifest.RecipeHash.Length != 64 || manifest.Artifacts.Count == 0)
            throw new InvalidDataException("Manifest de preparación incompleto.");
        if (manifest.Artifacts.Select(a => a.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count() != manifest.Artifacts.Count
            || manifest.Artifacts.Count(a => a.Kind == "python") != 1 || manifest.Artifacts.Count(a => a.Kind == "model") != 1
            || !manifest.Artifacts.Any(a => a.Kind == "wheel"))
            throw new InvalidDataException("Manifest de preparación incoherente.");
        foreach (var artifact in manifest.Artifacts)
        {
            if (Path.GetFileName(artifact.Name) != artifact.Name || string.IsNullOrWhiteSpace(artifact.Name)
                || !Uri.TryCreate(artifact.Url, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps
                || !AllowedHosts.Contains(uri.Host) || artifact.Size is null or <= 0
                || artifact.Kind is not ("python" or "wheel" or "model")
                || artifact.Sha256.Length != 64 || !artifact.Sha256.All(Uri.IsHexDigit)
                || artifact.Sha256.Select(char.ToUpperInvariant).Distinct().Count() < 5)
                throw new InvalidDataException($"Artefacto inválido en el manifest: '{artifact.Name}'.");
        }
    }
}

public interface ITrainingRuntimeSource
{
    Task<TrainingRuntimeManifest> ResolveAsync(CancellationToken ct);
    Task DownloadAsync(TrainingRuntimeArtifact artifact, string destination, Action<long> bytes, CancellationToken ct);
}
public interface ITrainingRuntimeProbe
{
    Task<IReadOnlyList<TrainingDeviceCapability>> ProbeAsync(TrainingRuntimeOptions runtime, CancellationToken ct);
}
public interface ITrainingRuntimeDeviceProbe
{
    Task<IReadOnlyList<TrainingDeviceCapability>> ProbeDevicesAsync(TrainingRuntimeOptions runtime, CancellationToken ct);
}
public interface ITrainingRuntimeManager
{
    string PreparationPath { get; }
    Task<TrainingRuntimeStatus> CheckAsync(CancellationToken ct = default, IProgress<TrainingRuntimeProgress>? progress = null);
    Task<TrainingRuntimeStatus> ProbeDevicesAsync(CancellationToken ct = default, IProgress<TrainingRuntimeProgress>? progress = null);
    Task<TrainingRuntimeStatus> DeepCheckAsync(CancellationToken ct = default, IProgress<TrainingRuntimeProgress>? progress = null);
    Task<TrainingRuntimeStatus> PrepareAsync(IProgress<TrainingRuntimeProgress>? progress = null, CancellationToken ct = default);
}

/// <summary>All mutations are private, staged and explicit. Never launches global Python or an installer.</summary>
public sealed class TrainingRuntimeProvisioner : ITrainingRuntimeManager
{
    public const string Version = "2.0.0-win-x64-cu130";
    public const string WorkerVersion = "2.0.0";
    private static readonly string[] EssentialFiles = ["python/python.exe", "python/python311._pth", "worker.py", "requirements.txt", "yolo26n.pt"];
    private readonly TrainingPaths _paths;
    private readonly ITrainingRuntimeSource _source;
    private readonly ITrainingRuntimeProbe _probe;
    private readonly ITrainingRuntimeDeviceProbe _deviceProbe;
    private readonly string _assets;
    public TrainingRuntimeProvisioner(TrainingPaths paths, ITrainingRuntimeSource? source = null,
        ITrainingRuntimeProbe? probe = null, string? assets = null, ITrainingRuntimeDeviceProbe? deviceProbe = null)
    {
        _paths = paths; _assets = assets ?? Path.Combine(AppContext.BaseDirectory, "training-worker");
        _source = source ?? new OfficialTrainingRuntimeSource(_assets);
        _probe = probe ?? new ManagedTrainingRuntimeProbe();
        _deviceProbe = deviceProbe ?? new ManagedTrainingRuntimeDeviceProbe();
    }
    public string Root => TrainingPaths.Within(_paths.DataRoot, "training", "runtime");
    public string Current => TrainingPaths.Within(Root, Version);
    public string LogPath => TrainingPaths.Within(Root, "provisioning.log");
    public string PreparationPath => TrainingPaths.Within(Root, "preparation.json");
    public string Cache => TrainingPaths.Within(Root, "cache");
    public static TrainingRuntimeOptions Options(string directory) => new(
        TrainingPaths.Within(directory, "python", "python.exe"), TrainingPaths.Within(directory, "worker.py"),
        TrainingPaths.Within(directory, "yolo26n.pt"));
    public FileStream Acquire(bool exclusive)
    {
        Directory.CreateDirectory(Root);
        return new FileStream(TrainingPaths.Within(Root, "runtime.lock"), FileMode.OpenOrCreate,
            exclusive ? FileAccess.ReadWrite : FileAccess.Read, exclusive ? FileShare.None : FileShare.Read);
    }
    public Task<string> RecipeHashAsync(CancellationToken ct) => ComputeRecipeHashAsync(_assets, ct);
    public static async Task<string> ComputeRecipeHashAsync(string assets, CancellationToken ct)
    {
        var hashes = new List<string> { Version };
        foreach (var name in new[] { "worker.py", "requirements.txt" }) hashes.Add(await HashAsync(Path.Combine(assets, name), ct));
        return Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(string.Join("\n", hashes))));
    }
    public async Task<TrainingRuntimeStatus> CheckAsync(CancellationToken ct = default, IProgress<TrainingRuntimeProgress>? progress = null)
    {
        progress?.Report(new(TrainingRuntimeState.Checking, 0, "Comprobando entorno de entrenamiento..."));
        if (!Directory.Exists(Current))
        {
            var resumable = File.Exists(PreparationPath) || Directory.Exists(Cache)
                && Directory.EnumerateFiles(Cache, "*.partial", SearchOption.AllDirectories).Any();
            return new(TrainingRuntimeState.NotInstalled, [], resumable
                ? "Hay una preparación pendiente. Puede continuarla."
                : "Prepare el entorno de entrenamiento.", CanResume: resumable, RequiresPreparation: true);
        }
        try
        {
            using var lease = Acquire(false);
            var receipt = await VerifyQuickAsync(Current, ct);
            var devices = receipt.LastKnownDevices is { Count: > 0 }
                ? receipt.LastKnownDevices
                : [new TrainingDeviceCapability(new(TrainingDeviceKind.Cpu), true)];
            var requiresDeviceProbe = receipt.LastKnownDevices is not { Count: > 0 };
            progress?.Report(new(TrainingRuntimeState.Ready, 100, "Entorno de entrenamiento listo"));
            return new(TrainingRuntimeState.Ready, devices,
                requiresDeviceProbe ? "Entorno listo. Comprobando dispositivos disponibles..."
                    : "Entorno de entrenamiento listo. Dispositivos según la última detección conocida.",
                CapabilitiesAreLastKnown: true, RequiresDeviceProbe: requiresDeviceProbe);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { return new(TrainingRuntimeState.Error, [],
            "El entorno requiere una verificación profunda. Puede seguir trabajando con sus proyectos.", ex.Message,
            RequiresPreparation: true); }
    }
    public async Task<TrainingRuntimeStatus> ProbeDevicesAsync(CancellationToken ct = default,
        IProgress<TrainingRuntimeProgress>? progress = null)
    {
        progress?.Report(new(TrainingRuntimeState.Checking, 0, "Comprobando dispositivos disponibles..."));
        TrainingRuntimeReceipt? receipt = null;
        try
        {
            using var lease = Acquire(true);
            receipt = await VerifyQuickAsync(Current, ct);
            var devices = await _deviceProbe.ProbeDevicesAsync(Options(Current), ct);
            if (!devices.Any(device => device.Device.Kind == TrainingDeviceKind.Cpu && device.Available))
                throw new InvalidDataException("El worker no confirmó disponibilidad de CPU.");
            receipt = receipt with
            {
                WorkerVersion = WorkerVersion,
                InstallationCompleted = true,
                LastKnownDevices = devices,
                DevicesDetectedAtUtc = DateTimeOffset.UtcNow
            };
            await TrainingJson.WriteAsync(TrainingPaths.Within(Current, "installed.json"), receipt, ct);
            var gpu = devices.FirstOrDefault(device => device.Device.Kind == TrainingDeviceKind.Gpu && device.Available);
            var unavailableReason = devices.FirstOrDefault(device => device.Device.Kind == TrainingDeviceKind.Gpu && !device.Available)
                ?.UnavailableReason;
            var message = gpu == null ? "GPU no disponible para entrenamiento. CPU disponible."
                : $"GPU disponible — {gpu.Device.DisplayName ?? $"GPU {gpu.Device.DeviceId + 1}"}";
            progress?.Report(new(TrainingRuntimeState.Ready, 100, message));
            return new(TrainingRuntimeState.Ready, devices, message, unavailableReason,
                DeviceAvailabilityConfirmed: true);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            var fallback = (receipt?.LastKnownDevices ?? [new TrainingDeviceCapability(new(TrainingDeviceKind.Cpu), true)])
                .Select(device => device.Device.Kind == TrainingDeviceKind.Gpu
                    ? device with { Available = false, UnavailableReason = "No se pudo confirmar la GPU en esta sesión." }
                    : device)
                .ToArray();
            return new(TrainingRuntimeState.Ready, fallback,
                "Error al comprobar GPU. Puede reintentar; CPU continúa disponible.", ex.Message,
                CapabilitiesAreLastKnown: true, DeviceProbeFailed: true);
        }
    }
    public async Task<TrainingRuntimeStatus> DeepCheckAsync(CancellationToken ct = default,
        IProgress<TrainingRuntimeProgress>? progress = null)
    {
        progress?.Report(new(TrainingRuntimeState.Checking, 0, "Iniciando verificación profunda del entorno..."));
        if (!Directory.Exists(Current)) return await CheckAsync(ct, progress);
        try
        {
            using var lease = Acquire(true);
            await VerifyAsync(Current, ct, progress);
            progress?.Report(new(TrainingRuntimeState.Checking, 75, "Comprobando capacidad de procesamiento..."));
            var devices = await _probe.ProbeAsync(Options(Current), ct);
            var receiptPath = TrainingPaths.Within(Current, "installed.json");
            var receipt = await TrainingJson.ReadAsync<TrainingRuntimeReceipt>(receiptPath, ct);
            receipt = receipt with
            {
                WorkerVersion = WorkerVersion,
                InstallationCompleted = true,
                DeepValidatedAtUtc = DateTimeOffset.UtcNow,
                LastKnownDevices = devices,
                DevicesDetectedAtUtc = DateTimeOffset.UtcNow
            };
            await TrainingJson.WriteAsync(receiptPath, receipt, ct);
            progress?.Report(new(TrainingRuntimeState.Ready, 100, "Entorno de entrenamiento listo"));
            return new(TrainingRuntimeState.Ready, devices, "Entorno de entrenamiento listo",
                DeviceAvailabilityConfirmed: true);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            return new(TrainingRuntimeState.Error, [],
                "No se pudo completar la verificación profunda. El runtime instalado se conservó.", ex.Message);
        }
    }
    public async Task<TrainingRuntimeReceipt> VerifyQuickAsync(string directory, CancellationToken ct)
    {
        var receipt = await TrainingJson.ReadAsync<TrainingRuntimeReceipt>(TrainingPaths.Within(directory, "installed.json"), ct);
        TrainingRuntimeManifestContracts.ValidateBasic(receipt.Manifest);
        if (receipt.Manifest.Version != Version || receipt.Manifest.RecipeHash != await RecipeHashAsync(ct)
            || receipt.Files.Count == 0 || receipt.InstallationCompleted == false
            || receipt.WorkerVersion is not null && receipt.WorkerVersion != WorkerVersion)
            throw new InvalidDataException("El receipt instalado no corresponde a esta versión.");
        foreach (var name in EssentialFiles)
        {
            if (!receipt.Files.TryGetValue(name, out var expected)) throw new InvalidDataException("El receipt está incompleto.");
            var path = TrainingPaths.Within(directory, name);
            if (!File.Exists(path) || !string.Equals(expected, await HashAsync(path, ct), StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Falta un componente esencial o no supera la comprobación rápida.");
        }
        if (!Directory.Exists(TrainingPaths.Within(directory, "python", "Lib", "site-packages")))
            throw new InvalidDataException("La estructura del runtime privado está incompleta.");
        Options(directory).Validate();
        return receipt;
    }
    public async Task VerifyAsync(string directory, CancellationToken ct, IProgress<TrainingRuntimeProgress>? progress = null)
    {
        progress?.Report(new(TrainingRuntimeState.Checking, 1, "Leyendo información del entorno instalado..."));
        var receipt = await TrainingJson.ReadAsync<TrainingRuntimeReceipt>(TrainingPaths.Within(directory, "installed.json"), ct);
        if (receipt.Manifest.Version != Version || receipt.Manifest.RecipeHash != await RecipeHashAsync(ct)
            || receipt.Files.Count == 0) throw new InvalidDataException("El manifest no corresponde a esta versión.");
        long checkedBytes = 0;
        var files = receipt.Files.Select(file => (file.Key, file.Value, Path: TrainingPaths.Within(directory, file.Key))).ToArray();
        var totalBytes = files.Sum(file => File.Exists(file.Path) ? new FileInfo(file.Path).Length : 0L);
        var report = System.Diagnostics.Stopwatch.StartNew();
        foreach (var file in files)
        {
            if (!string.Equals(file.Value, await HashAsync(file.Path, ct), StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Un componente del entorno no supera la verificación de integridad.");
            checkedBytes += new FileInfo(file.Path).Length;
            if (report.ElapsedMilliseconds >= 250)
            {
                var percent = totalBytes == 0 ? 5 : 5 + 65.0 * checkedBytes / totalBytes;
                progress?.Report(new(TrainingRuntimeState.Checking, percent, $"Verificando entorno instalado... {percent:F0}%"));
                report.Restart();
            }
        }
        foreach (var name in EssentialFiles)
            if (!receipt.Files.ContainsKey(name)) throw new InvalidDataException("El manifest está incompleto.");
        Options(directory).Validate();
    }
    public async Task<TrainingRuntimeStatus> PrepareAsync(IProgress<TrainingRuntimeProgress>? progress = null, CancellationToken ct = default)
    {
        using var lease = Acquire(true); // cannot replace components while a worker holds a read lease
        var stage = TrainingPaths.Within(Root, "stage-" + Guid.NewGuid().ToString("N"));
        var backup = TrainingPaths.Within(Root, "rollback-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(stage);
        var currentStage = "Comprobando componentes";
        try
        {
            progress?.Report(new(TrainingRuntimeState.Preparing, 0, "Comprobando componentes de descarga..."));
            var manifest = await ResolveManifestAsync(ct);
            TrainingRuntimeManifestContracts.ValidateBasic(manifest);
            if (manifest.Version != Version || manifest.RecipeHash != await RecipeHashAsync(ct)
                || manifest.Artifacts.Count == 0) throw new InvalidDataException("Manifest de preparación incompatible.");
            await TrainingJson.WriteAsync(TrainingPaths.Within(stage, "download-manifest.json"), manifest, ct);
            await TrainingJson.WriteAsync(PreparationPath, manifest, ct);
            var total = manifest.Artifacts.All(a => a.Size > 0) ? manifest.Artifacts.Sum(a => a.Size!.Value) : (long?)null;
            long completedBytes = 0;
            progress?.Report(new(TrainingRuntimeState.Preparing, 3, "Componentes comprobados. Iniciando descarga...", total));
            for (var i = 0; i < manifest.Artifacts.Count; i++)
            {
                var artifact = manifest.Artifacts[i];
                var artifactCache = TrainingPaths.Within(Cache, artifact.Sha256.ToLowerInvariant());
                Directory.CreateDirectory(artifactCache);
                var destination = TrainingPaths.Within(artifactCache, artifact.Name);
                currentStage = artifact.Kind switch { "python" => "Descargando runtime privado", "model" => "Preparando modelo base", _ => "Descargando dependencias" };
                await LogAsync($"{currentStage}: {artifact.Name} ({artifact.Url})", null);
                var cached = File.Exists(destination) && new FileInfo(destination).Length == artifact.Size
                    && string.Equals(await HashAsync(destination, ct), artifact.Sha256, StringComparison.OrdinalIgnoreCase);
                if (!cached && File.Exists(destination)) File.Delete(destination);
                if (!cached) await _source.DownloadAsync(artifact, destination, bytes =>
                {
                    var percent = total.HasValue ? 3 + 72.0 * (completedBytes + bytes) / total.Value : 3;
                    progress?.Report(new(TrainingRuntimeState.Preparing, percent, currentStage + "...", total, completedBytes + bytes));
                }, ct);
                completedBytes += artifact.Size!.Value;
                currentStage = "Verificando componente";
                progress?.Report(new(TrainingRuntimeState.Preparing, total.HasValue ? 3 + 72.0 * completedBytes / total.Value : 3,
                    $"Verificando {artifact.Name}...", total));
                if (!string.Equals(await HashAsync(destination, ct), artifact.Sha256, StringComparison.OrdinalIgnoreCase))
                {
                    File.Delete(destination);
                    throw new InvalidDataException("La descarga no supera la verificación SHA-256. Reintente la preparación.");
                }
                if (artifact.Kind == "python") { currentStage = "Preparando runtime privado"; await ExtractAsync(destination, TrainingPaths.Within(stage, "python"), false, ct); }
                else if (artifact.Kind == "wheel") { currentStage = "Preparando dependencias"; await ExtractAsync(destination, TrainingPaths.Within(stage, "python", "Lib", "site-packages"), true, ct); }
                else if (artifact.Kind == "model") File.Copy(destination, TrainingPaths.Within(stage, "yolo26n.pt"));
                else throw new InvalidDataException("Tipo de componente no permitido.");
            }
            foreach (var name in new[] { "worker.py", "requirements.txt" }) File.Copy(Path.Combine(_assets, name), TrainingPaths.Within(stage, name));
            // Isolated embedded runtime: no registry, user site-packages or global PATH lookup.
            await File.WriteAllTextAsync(TrainingPaths.Within(stage, "python", "python311._pth"),
                "python311.zip\n.\nLib/site-packages\nimport site\n", ct);
            currentStage = "Validando entorno";
            progress?.Report(new(TrainingRuntimeState.Preparing, 90, "Validando el entorno de entrenamiento...", total));
            var devices = await _probe.ProbeAsync(Options(stage), ct);
            if (!devices.Any(d => d.Available && d.Device.Kind == TrainingDeviceKind.Cpu)) throw new InvalidDataException("No se pudo validar el procesamiento del equipo.");
            var files = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var file in Directory.EnumerateFiles(stage, "*", SearchOption.AllDirectories))
            {
                var relative = Path.GetRelativePath(stage, file).Replace('\\', '/');
                if (relative.StartsWith("settings/", StringComparison.OrdinalIgnoreCase)) continue;
                files.Add(relative, await HashAsync(file, ct));
            }
            await TrainingJson.WriteAsync(TrainingPaths.Within(stage, "installed.json"), new TrainingRuntimeReceipt(manifest, files)
            {
                WorkerVersion = WorkerVersion,
                InstallationCompleted = true,
                DeepValidatedAtUtc = DateTimeOffset.UtcNow,
                LastKnownDevices = devices,
                DevicesDetectedAtUtc = DateTimeOffset.UtcNow
            }, ct);
            await VerifyAsync(stage, ct);
            ct.ThrowIfCancellationRequested();
            if (Directory.Exists(Current)) Directory.Move(Current, backup);
            try { Directory.Move(stage, Current); }
            catch { if (Directory.Exists(backup)) Directory.Move(backup, Current); throw; }
            progress?.Report(new(TrainingRuntimeState.Ready, 100, "Entorno de entrenamiento listo", total));
            await LogAsync("Preparación completada.", null);
            if (File.Exists(PreparationPath)) File.Delete(PreparationPath);
            return new(TrainingRuntimeState.Ready, devices, "Entorno de entrenamiento listo",
                DeviceAvailabilityConfirmed: true);
        }
        catch (OperationCanceledException) { progress?.Report(new(TrainingRuntimeState.NotInstalled, 0, "Preparación cancelada.")); throw; }
        catch (Exception ex)
        {
            var message = ex is HttpRequestException ? "No se pudo descargar un componente de entrenamiento."
                : ex is InvalidDataException && (ex.Message.Contains("SHA-256") || ex.Message.Contains("integridad"))
                    ? "No se pudo verificar un componente descargado."
                    : "No se pudo preparar el runtime privado.";
            await LogAsync($"Fallo en etapa '{currentStage}'.", ex);
            progress?.Report(new(TrainingRuntimeState.Error, 0, message + " Puede reintentar."));
            return new(TrainingRuntimeState.Error, [], message + " Puede reintentar.",
                $"Etapa: {currentStage}. {ex.Message} Registro: {LogPath}", RequiresPreparation: true);
        }
        finally
        {
            // Computed paths are revalidated inside the managed runtime root before recursive cleanup.
            foreach (var path in Directory.Exists(Current) ? new[] { stage, backup } : new[] { stage })
            {
                var safe = TrainingPaths.Within(Root, Path.GetFileName(path));
                if (Directory.Exists(safe)) try { Directory.Delete(safe, true); } catch (IOException) { }
            }
        }
    }
    private async Task<TrainingRuntimeManifest> ResolveManifestAsync(CancellationToken ct)
    {
        try { return await _source.ResolveAsync(ct); }
        catch (HttpRequestException) when (File.Exists(PreparationPath))
        {
            var saved = await TrainingJson.ReadAsync<TrainingRuntimeManifest>(PreparationPath, ct);
            TrainingRuntimeManifestContracts.ValidateBasic(saved);
            if (saved.Version != Version || saved.RecipeHash != await RecipeHashAsync(ct)) throw;
            await LogAsync("Sin conexión para consultar componentes; se continúa con el manifest guardado.", null);
            return saved;
        }
    }
    public static async Task<string> HashAsync(string path, CancellationToken ct)
    { await using var input = File.OpenRead(path); return Convert.ToHexString(await SHA256.HashDataAsync(input, ct)); }

    internal static async Task ExtractAsync(string archive, string root, bool wheel, CancellationToken ct)
    {
        using var zip = ZipFile.OpenRead(archive);
        foreach (var entry in zip.Entries)
        {
            ct.ThrowIfCancellationRequested();
            if (entry.FullName.EndsWith('/')) continue;
            var relative = entry.FullName;
            if (wheel && relative.Split('/')[0].EndsWith(".data", StringComparison.Ordinal))
            {
                var segments = relative.Split('/', 3);
                if (segments.Length != 3 || segments[1] is not ("purelib" or "platlib"))
                    continue; // CLI launchers, headers and data outside site-packages are not used by this worker
                relative = segments[2];
            }
            if (((entry.ExternalAttributes >> 16) & 0xF000) == 0xA000) throw new InvalidDataException("Enlace no permitido en componente.");
            var path = TrainingPaths.Within(root, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            await using var source = entry.Open();
            await using var destination = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            await source.CopyToAsync(destination, ct);
        }
    }
    private async Task LogAsync(string message, Exception? error)
    {
        try
        {
            Directory.CreateDirectory(Root);
            var detail = error == null ? message : $"{message} {error}";
            if (detail.Length > 16_384) detail = detail[..16_384];
            await File.AppendAllTextAsync(LogPath, $"{DateTimeOffset.UtcNow:O} {detail}{Environment.NewLine}");
        }
        catch (Exception) { /* Diagnostics must never hide the provisioning result. */ }
    }
}
