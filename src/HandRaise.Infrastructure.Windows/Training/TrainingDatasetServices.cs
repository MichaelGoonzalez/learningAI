using System.Globalization;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using HandRaise.Application.Inference;
using HandRaise.Application.Training;
using HandRaise.Domain.Training;
using OpenCvSharp;

namespace HandRaise.Infrastructure.Windows.Training;

public sealed record TrainingImportFailure(string FileName, string Message);
public sealed record TrainingFolderImportResult(TrainingProject Project, IReadOnlyList<TrainingImportFailure> Failures);

public sealed class TrainingAssetService(TrainingPaths paths) : ITrainingAssets
{
    private const long MaxBytes = 40 * 1024 * 1024;
    public async Task<TrainingImage> ImportAsync(TrainingProject project, string path, TrainingSourceType source,
        string? sourceId, double? sourceTimeSeconds, CancellationToken ct)
    {
        var extension = Path.GetExtension(path).ToLowerInvariant();
        if (extension is not (".jpg" or ".jpeg" or ".png")) throw new ArgumentException("Seleccione una imagen JPG o PNG.");
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (stream.Length is <= 0 or > MaxBytes) throw new ArgumentException("La imagen está vacía o supera 40 MB.");
        var bytes = new byte[checked((int)stream.Length)];
        await stream.ReadExactlyAsync(bytes, ct);
        var jpeg = bytes.Length >= 3 && bytes[0] == 0xff && bytes[1] == 0xd8 && bytes[2] == 0xff;
        var png = bytes.Length >= 8 && bytes.AsSpan(0, 8).SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 });
        if (extension == ".png" ? !png : !jpeg) throw new ArgumentException("El contenido no corresponde al formato de imagen indicado.");
        return await StoreAsync(project, bytes, Path.GetFileName(path), extension, source, sourceId, sourceTimeSeconds, ct);
    }

    public async Task<TrainingImage> ImportFrameAsync(TrainingProject project, ImageFrame frame, TrainingSourceType source,
        string sourceId, double? sourceTimeSeconds, CancellationToken ct)
    {
        if (source == TrainingSourceType.Image) throw new ArgumentException("Indique VideoFrame o CameraCapture para un frame.");
        using var mat = new Mat(frame.Height, frame.Width, MatType.CV_8UC3);
        var pixels = frame.BgrPixels.ToArray();
        Marshal.Copy(pixels, 0, mat.Data, pixels.Length);
        if (!Cv2.ImEncode(".png", mat, out var bytes)) throw new InvalidDataException("No se pudo convertir el frame a PNG.");
        return await StoreAsync(project, bytes, "frame.png", ".png", source, sourceId, sourceTimeSeconds, ct);
    }

    private async Task<TrainingImage> StoreAsync(TrainingProject project, byte[] bytes, string name, string extension,
        TrainingSourceType source, string? sourceId, double? time, CancellationToken ct)
    {
        if (!Enum.IsDefined(source) || (source != TrainingSourceType.Image && string.IsNullOrWhiteSpace(sourceId)))
            throw new ArgumentException("Los frames necesitan un identificador de origen estable.");
        if (time.HasValue && (!double.IsFinite(time.Value) || time < 0)) throw new ArgumentException("El tiempo del frame no es válido.");
        var hash = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        var duplicate = project.Images.FirstOrDefault(i => i.Sha256 == hash);
        if (duplicate != null) return duplicate;
        using var decoded = Cv2.ImDecode(bytes, ImreadModes.Color);
        if (decoded.Empty() || decoded.Width <= 0 || decoded.Height <= 0 || (long)decoded.Width * decoded.Height > 40_000_000)
            throw new InvalidDataException("La imagen está dañada o supera 40 megapíxeles.");
        var id = Guid.NewGuid();
        var relative = $"assets/{id:N}{extension}";
        var destination = TrainingPaths.Within(paths.Project(project.Id), relative);
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        await using (var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            await output.WriteAsync(bytes, ct);
        return new(id, project.Id, name, relative, decoded.Width, decoded.Height, DateTimeOffset.UtcNow, source,
            hash, sourceId, time, []);
    }

    /// <summary>Non-recursive by design: no traversal through folder junctions. Errors are per asset.</summary>
    public static async Task<TrainingFolderImportResult> ImportFolderAsync(TrainingApplicationService service, Guid projectId,
        string folder, CancellationToken ct = default)
    {
        var failures = new List<TrainingImportFailure>();
        var project = await service.GetProjectAsync(projectId, ct);
        foreach (var path in Directory.EnumerateFiles(folder).Order(StringComparer.OrdinalIgnoreCase))
        {
            ct.ThrowIfCancellationRequested();
            if (Path.GetExtension(path).ToLowerInvariant() is not (".jpg" or ".jpeg" or ".png")) continue;
            try { project = await service.ImportAsync(projectId, path, ct: ct); }
            catch (Exception ex) when (ex is IOException or InvalidDataException or ArgumentException or OpenCVException)
            { failures.Add(new(Path.GetFileName(path), ex.Message)); }
        }
        return new(project, failures);
    }
}

public sealed class YoloDatasetExporter(TrainingPaths paths, DatasetSplitService split) : ITrainingDatasetExporter
{
    public async Task<TrainingDataset> ExportAsync(TrainingJob job, CancellationToken ct)
    {
        var project = job.Snapshot;
        var parts = split.Split(project, job.Configuration.Seed);
        var root = TrainingPaths.Within(paths.Job(job.Id), "dataset-export");
        if (Directory.Exists(root)) throw new IOException("El snapshot exportado ya existe; cree otra ejecución.");
        Directory.CreateDirectory(root);
        var classes = project.Classes.Select((c, index) => (c.Id, index)).ToDictionary(c => c.Id, c => c.index);
        foreach (var (name, images) in new[] { ("train", parts.Train), ("val", parts.Validation) })
        {
            Directory.CreateDirectory(TrainingPaths.Within(root, "images", name));
            Directory.CreateDirectory(TrainingPaths.Within(root, "labels", name));
            foreach (var image in images)
            {
                ct.ThrowIfCancellationRequested();
                var source = TrainingPaths.Within(paths.Project(project.Id), image.StoredPath);
                await using var input = File.OpenRead(source);
                var hash = Convert.ToHexString(await SHA256.HashDataAsync(input, ct)).ToLowerInvariant();
                if (hash != image.Sha256) throw new InvalidDataException($"La imagen '{image.OriginalFileName}' cambió desde su importación.");
                input.Position = 0;
                var imagePath = TrainingPaths.Within(root, "images", name, $"{image.Id:N}{Path.GetExtension(source)}");
                await using (var output = new FileStream(imagePath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                    await input.CopyToAsync(output, ct);
                var lines = image.Annotations.Select(a => string.Join(" ", classes[a.ClassId].ToString(CultureInfo.InvariantCulture),
                    a.XCenter.ToString("R", CultureInfo.InvariantCulture), a.YCenter.ToString("R", CultureInfo.InvariantCulture),
                    a.Width.ToString("R", CultureInfo.InvariantCulture), a.Height.ToString("R", CultureInfo.InvariantCulture)));
                await File.WriteAllLinesAsync(TrainingPaths.Within(root, "labels", name, $"{image.Id:N}.txt"), lines, ct);
            }
        }
        // JSON scalars/arrays are valid YAML, so names cannot inject YAML directives or paths.
        var yaml = $"path: {JsonSerializer.Serialize(root.Replace('\\', '/'))}\ntrain: images/train\nval: images/val\nnames: {JsonSerializer.Serialize(project.Classes.Select(c => c.Name))}\n";
        var yamlPath = TrainingPaths.Within(root, "dataset.yaml");
        await File.WriteAllTextAsync(yamlPath, yaml, ct);
        var snapshotBytes = JsonSerializer.SerializeToUtf8Bytes(new { project, seed = job.Configuration.Seed,
            train = parts.Train.Select(i => i.Id), validation = parts.Validation.Select(i => i.Id) }, TrainingJson.Options);
        var snapshot = new TrainingDataset(Convert.ToHexString(SHA256.HashData(snapshotBytes)).ToLowerInvariant(), yamlPath,
            parts.Train.Select(i => i.Id).ToArray(), parts.Validation.Select(i => i.Id).ToArray(), parts.Warnings);
        await TrainingJson.WriteAsync(TrainingPaths.Within(root, "snapshot.json"), new { project, dataset = snapshot }, ct);
        return snapshot;
    }
}

public sealed class TrainingResourceMonitor(Func<int>? activeCameraCount = null) : ITrainingResourceMonitor
{
    public Task<IReadOnlyList<TrainingResourceWarning>> AssessAsync(TrainingResourcePolicy policy, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        IReadOnlyList<TrainingResourceWarning> warnings = [new("shared_resources",
            $"El entrenamiento puede reducir temporalmente el rendimiento de las cámaras activas. Cámaras activas: {activeCameraCount?.Invoke().ToString() ?? "sin información"}.")];
        return Task.FromResult(warnings);
    }
}
