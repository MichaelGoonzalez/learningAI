using System.Text.Json;
using System.Text.Json.Serialization;
using HandRaise.Application.Training;
using HandRaise.Domain.Training;

namespace HandRaise.Infrastructure.Windows.Training;

public sealed class TrainingPaths
{
    public static string DefaultDataRoot => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "HandRaiseDetection");
    public string DataRoot { get; }
    public TrainingPaths(string? dataRoot = null) => DataRoot = Path.GetFullPath(dataRoot ?? DefaultDataRoot);
    public string Project(Guid id) => Within(DataRoot, "training", "projects", Key(id));
    public string Job(Guid id) => Within(DataRoot, "training", "jobs", Key(id));
    public string CustomModels => Within(DataRoot, "models", "custom");
    private static string Key(Guid id) => id != Guid.Empty ? id.ToString("N") : throw new ArgumentException("El ID está vacío.");
    public static string Within(string root, params string[] parts)
    {
        if (parts.Any(part => Path.IsPathRooted(part) || part.Split(['/', '\\']).Any(segment =>
                segment == ".." || segment.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || segment.EndsWith(' ') || segment.EndsWith('.'))))
            throw new InvalidDataException("La ruta contiene segmentos no permitidos.");
        var fullRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var path = Path.GetFullPath(Path.Combine(new[] { fullRoot }.Concat(parts).ToArray()));
        if (!path.StartsWith(fullRoot, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("La ruta sale del almacenamiento permitido.");
        // Reject junctions/symlinks at every existing ancestor, including the controlled root.
        for (var current = path; current != null; current = Path.GetDirectoryName(current))
            if ((File.Exists(current) || Directory.Exists(current)) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("No se admiten enlaces en el almacenamiento de entrenamiento.");
        return path;
    }
}

public static class TrainingJson
{
    public static JsonSerializerOptions Options { get; } = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower, WriteIndented = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower, allowIntegerValues: false) }
    };
    public static async Task<T> ReadAsync<T>(string path, CancellationToken ct = default)
    {
        await using var stream = File.OpenRead(path);
        return await JsonSerializer.DeserializeAsync<T>(stream, Options, ct) ?? throw new InvalidDataException("El manifiesto está vacío.");
    }
    public static async Task WriteAsync<T>(string path, T value, CancellationToken ct = default)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temp = path + $".{Guid.NewGuid():N}.tmp";
        try
        {
            await using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
            { await JsonSerializer.SerializeAsync(stream, value, Options, ct); await stream.FlushAsync(ct); }
            File.Move(temp, path, true);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }
}

public sealed class JsonTrainingStore(TrainingPaths paths) : ITrainingStore
{
    public async Task<TrainingProject> LoadProjectAsync(Guid id, CancellationToken ct = default)
    {
        var project = await TrainingJson.ReadAsync<TrainingProject>(TrainingPaths.Within(paths.Project(id), "project.json"), ct);
        if (project.Id != id) throw new InvalidDataException("El ID del manifiesto no coincide con el proyecto.");
        project.Validate();
        return project;
    }
    public Task SaveProjectAsync(TrainingProject project, CancellationToken ct = default)
    { project.Validate(); return TrainingJson.WriteAsync(TrainingPaths.Within(paths.Project(project.Id), "project.json"), project, ct); }
    public Task SaveJobAsync(TrainingJob job, CancellationToken ct = default) =>
        TrainingJson.WriteAsync(TrainingPaths.Within(paths.Job(job.Id), "job.json"), job, ct);
    public async Task<TrainingJob> LoadJobAsync(Guid id, CancellationToken ct = default)
    {
        var job = await TrainingJson.ReadAsync<TrainingJob>(TrainingPaths.Within(paths.Job(id), "job.json"), ct);
        if (job.Id != id) throw new InvalidDataException("El ID del job no coincide con el manifiesto.");
        return job;
    }
    public async Task<IReadOnlyList<TrainingProject>> ListProjectsAsync(CancellationToken ct = default)
    {
        var result = new List<TrainingProject>();
        foreach (var id in Ids("projects")) result.Add(await LoadProjectAsync(id, ct));
        return result;
    }
    public async Task<IReadOnlyList<TrainingJob>> ListJobsAsync(CancellationToken ct = default)
    {
        var result = new List<TrainingJob>();
        foreach (var id in Ids("jobs")) result.Add(await LoadJobAsync(id, ct));
        return result;
    }
    private IEnumerable<Guid> Ids(string kind)
    {
        var root = TrainingPaths.Within(paths.DataRoot, "training", kind);
        return Directory.Exists(root) ? Directory.EnumerateDirectories(root).Select(Path.GetFileName)
            .Where(s => Guid.TryParseExact(s, "N", out _)).Select(s => Guid.ParseExact(s!, "N")).ToArray() : [];
    }
    public Task DeleteProjectAsync(Guid id, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var source = paths.Project(id);
        // Recoverable explicit deletion. Published models and job audit records have independent lifecycles.
        var destination = TrainingPaths.Within(paths.DataRoot, "training", "deleted", $"{id:N}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        Directory.Move(source, destination);
        return Task.CompletedTask;
    }
}
