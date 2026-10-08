using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace HandRaise.Infrastructure.Windows.Training;

/// <summary>Resolves only fixed releases from official HTTPS indexes, never a latest version or an sdist.</summary>
public sealed class OfficialTrainingRuntimeSource(string assets, HttpClient? client = null) : ITrainingRuntimeSource
{
    private sealed record DownloadState(string Url, string Sha256, long? Size, string? ETag, DateTimeOffset? LastModified);
    public const string PythonUrl = "https://www.python.org/ftp/python/3.11.9/python-3.11.9-embeddable-amd64.zip";
    public const string PythonSha256 = "33b448f95fecb7c6f802157dbd5e6b40a2ad9bfc8b95ca634a06ba4073ad1ac0";
    private static readonly HttpClient Shared = new() { Timeout = TimeSpan.FromHours(2) };
    private readonly HttpClient _http = client ?? Shared;
    public async Task<TrainingRuntimeManifest> ResolveAsync(CancellationToken ct)
    {
        var artifacts = new List<TrainingRuntimeArtifact>
        {
            // Python.org's versioned Windows release manifest; embedded distribution, no global installation.
            new("python.zip", PythonUrl, PythonSha256, 11243893, "python")
        };
        foreach (var line in await File.ReadAllLinesAsync(Path.Combine(assets, "requirements.txt"), ct))
        {
            if (string.IsNullOrWhiteSpace(line) || line.StartsWith('#')) continue;
            var pin = line.Split("==");
            if (pin.Length != 2 || !Regex.IsMatch(pin[0], "^[A-Za-z0-9_-]+$") || !IsExactVersion(pin[1]))
                throw new InvalidDataException("La lista de componentes contiene una versión no fijada.");
            artifacts.Add(pin[0] is "torch" or "torchvision"
                ? await TorchAsync(pin[0], pin[1], ct) : await PyPiAsync(pin[0], pin[1], ct));
        }
        using var release = await JsonAsync("https://api.github.com/repos/ultralytics/assets/releases/tags/v8.4.0", ct);
        var model = release.RootElement.GetProperty("assets").EnumerateArray().Single(a => a.GetProperty("name").GetString() == "yolo26n.pt");
        var digest = model.GetProperty("digest").GetString();
        if (digest == null || !digest.StartsWith("sha256:")) throw new InvalidDataException("El proveedor no publicó un hash verificable del modelo base.");
        artifacts.Add(new("yolo26n.pt", model.GetProperty("browser_download_url").GetString()!, digest[7..], model.GetProperty("size").GetInt64(), "model"));
        var manifest = new TrainingRuntimeManifest(TrainingRuntimeProvisioner.Version,
            await TrainingRuntimeProvisioner.ComputeRecipeHashAsync(assets, ct), artifacts);
        ValidateOfficialManifest(manifest);
        return manifest;
    }
    public static bool IsExactVersion(string version) => Regex.IsMatch(version,
        @"^\d+(?:\.\d+)+(?:a\d+|b\d+|rc\d+|\.post\d+|\.dev\d+)?$");
    public static void ValidateOfficialManifest(TrainingRuntimeManifest manifest)
    {
        TrainingRuntimeManifestContracts.ValidateBasic(manifest);
        var files = manifest.Artifacts.Select(a => a.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var required in new[]
        {
            "python.zip", "torch-2.14.1+cu130-cp311-cp311-win_amd64.whl",
            "torchvision-0.29.1+cu130-cp311-cp311-win_amd64.whl",
            "ultralytics-8.4.170-py3-none-any.whl", "onnx-1.23.1-cp311-cp311-win_amd64.whl", "yolo26n.pt"
        })
            if (!files.Contains(required)) throw new InvalidDataException($"Falta el componente fijado '{required}'.");
        var python = manifest.Artifacts.Single(a => a.Kind == "python");
        if (python.Url != PythonUrl || !python.Sha256.Equals(PythonSha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("El paquete privado de Python no coincide con el release Windows x64 fijado.");
    }
    private async Task<TrainingRuntimeArtifact> PyPiAsync(string name, string version, CancellationToken ct)
    {
        using var json = await JsonAsync($"https://pypi.org/pypi/{name}/{version}/json", ct);
        var matches = json.RootElement.GetProperty("urls").EnumerateArray()
            .Where(f => !f.GetProperty("yanked").GetBoolean() && CompatibleWheel(f.GetProperty("filename").GetString()!))
            .OrderBy(f => f.GetProperty("filename").GetString(), StringComparer.Ordinal).ToArray();
        if (matches.Length == 0) throw new InvalidDataException($"No hay un componente compatible para {name} {version}.");
        var file = matches[0];
        return new(file.GetProperty("filename").GetString()!, file.GetProperty("url").GetString()!,
            file.GetProperty("digests").GetProperty("sha256").GetString()!, file.GetProperty("size").GetInt64(), "wheel");
    }
    public static bool CompatibleWheel(string name)
    {
        var parts = name.Split('-');
        if (parts.Length < 5 || !name.EndsWith(".whl", StringComparison.Ordinal)) return false;
        var python = parts[^3]; var abi = parts[^2]; var platform = parts[^1][..^4];
        if (platform is not ("any" or "win_amd64")) return false;
        return (python.Split('.').Contains("py3") && abi == "none")
            || (python == "cp311" && abi is "cp311" or "abi3" or "none")
            || (abi == "abi3" && python.StartsWith("cp3") && int.TryParse(python[3..], out var minor) && minor <= 11);
    }
    private async Task<TrainingRuntimeArtifact> TorchAsync(string name, string version, CancellationToken ct)
    {
        var index = new Uri($"https://download.pytorch.org/whl/cu130/{name}/");
        using var indexRequest = new HttpRequestMessage(HttpMethod.Get, index);
        using var indexResponse = await SendAsync(indexRequest, HttpCompletionOption.ResponseHeadersRead, "consultar índice de procesamiento", ct);
        var html = await indexResponse.Content.ReadAsStringAsync(ct);
        var filename = $"{name}-{version}+cu130-cp311-cp311-win_amd64.whl";
        foreach (Match match in Regex.Matches(html, "href=\"([^\"]+)\""))
        {
            var uri = new Uri(index, WebUtility.HtmlDecode(match.Groups[1].Value));
            if (Uri.UnescapeDataString(Path.GetFileName(uri.AbsolutePath)) != filename) continue;
            if (!uri.Fragment.StartsWith("#sha256=")) throw new InvalidDataException("Falta el hash del componente de procesamiento.");
            using var head = new HttpRequestMessage(HttpMethod.Head, uri.GetLeftPart(UriPartial.Path));
            using var response = await SendAsync(head, HttpCompletionOption.ResponseHeadersRead, "comprobar componente", ct);
            return new(filename, uri.GetLeftPart(UriPartial.Path), uri.Fragment[8..], response.Content.Headers.ContentLength, "wheel");
        }
        throw new InvalidDataException("La versión fijada de procesamiento no está disponible en su fuente oficial.");
    }
    private async Task<JsonDocument> JsonAsync(string url, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.UserAgent.ParseAdd("VisionControl-Edge/2.0");
        using var response = await SendAsync(request, HttpCompletionOption.ResponseHeadersRead, "consultar metadata", ct);
        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        return await JsonDocument.ParseAsync(stream, cancellationToken: ct);
    }
    public async Task DownloadAsync(TrainingRuntimeArtifact artifact, string destination, Action<long> bytes, CancellationToken ct)
    {
        var uri = new Uri(artifact.Url);
        if (uri.Scheme != "https" || uri.Host is not ("www.python.org" or "files.pythonhosted.org" or "download.pytorch.org" or "download-r2.pytorch.org" or "github.com"))
            throw new InvalidDataException("Origen de componente no permitido.");
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        if (File.Exists(destination)) { bytes(new FileInfo(destination).Length); return; }
        var partial = destination + ".partial";
        var statePath = destination + ".download.json";
        var state = await ReadStateAsync(statePath, ct);
        if (state != null && (state.Url != artifact.Url || !state.Sha256.Equals(artifact.Sha256, StringComparison.OrdinalIgnoreCase)
            || state.Size != artifact.Size))
        {
            if (File.Exists(partial)) File.Delete(partial);
            File.Delete(statePath);
            state = null;
        }
        if (File.Exists(partial) && state == null) File.Delete(partial);
        Exception? last = null;
        for (var attempt = 1; attempt <= 3; attempt++)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                await DownloadAttemptAsync(artifact, uri, destination, partial, statePath, state, bytes, ct);
                return;
            }
            catch (Exception ex) when (ex is HttpRequestException or IOException)
            {
                last = ex;
                if (attempt == 3 || ex is HttpRequestException { StatusCode: >= HttpStatusCode.BadRequest and < HttpStatusCode.InternalServerError
                    and not HttpStatusCode.RequestTimeout and not HttpStatusCode.TooManyRequests }) break;
                await Task.Delay(TimeSpan.FromSeconds(attempt), ct);
                state = await ReadStateAsync(statePath, ct);
            }
        }
        throw new HttpRequestException($"No se pudo descargar el componente tras 3 intentos: {artifact.Url}. {last?.Message}", last);
    }

    private async Task DownloadAttemptAsync(TrainingRuntimeArtifact artifact, Uri uri, string destination, string partial,
        string statePath, DownloadState? state, Action<long> bytes, CancellationToken ct)
    {
        var offset = File.Exists(partial) ? new FileInfo(partial).Length : 0;
        if (artifact.Size.HasValue && offset > artifact.Size.Value) { File.Delete(partial); offset = 0; }
        if (artifact.Size.HasValue && offset == artifact.Size.Value)
        { File.Move(partial, destination, true); File.Delete(statePath); bytes(offset); return; }
        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        if (offset > 0)
        {
            request.Headers.Range = new RangeHeaderValue(offset, null);
            if (state?.ETag != null && EntityTagHeaderValue.TryParse(state.ETag, out var tag)) request.Headers.IfRange = new RangeConditionHeaderValue(tag);
            else if (state?.LastModified != null) request.Headers.IfRange = new RangeConditionHeaderValue(state.LastModified.Value);
        }
        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        if (response.StatusCode == HttpStatusCode.RequestedRangeNotSatisfiable && artifact.Size == offset)
        { File.Move(partial, destination, true); File.Delete(statePath); bytes(offset); return; }
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException($"No se pudo descargar componente: HTTP {(int)response.StatusCode}, {uri}.", null, response.StatusCode);
        var append = offset > 0 && response.StatusCode == HttpStatusCode.PartialContent;
        if (append && response.Content.Headers.ContentRange?.From != offset)
            throw new InvalidDataException("El servidor devolvió un rango de descarga inesperado.");
        if (!append) offset = 0;
        var etag = response.Headers.ETag?.ToString();
        var modified = response.Content.Headers.LastModified;
        if (append && state != null && ((state.ETag != null && etag != null && state.ETag != etag)
            || (state.LastModified != null && modified != null && state.LastModified != modified)))
        {
            File.Delete(partial); File.Delete(statePath);
            throw new IOException("El componente remoto cambió durante la descarga; se reiniciará este componente.");
        }
        await TrainingJson.WriteAsync(statePath, new DownloadState(artifact.Url, artifact.Sha256, artifact.Size,
            etag ?? state?.ETag, modified ?? state?.LastModified), ct);
        await using var input = await response.Content.ReadAsStreamAsync(ct);
        await using var output = new FileStream(partial, append ? FileMode.Append : FileMode.Create, FileAccess.Write, FileShare.Read,
            128 * 1024, FileOptions.Asynchronous | FileOptions.WriteThrough);
        var buffer = new byte[128 * 1024]; var count = offset; int read;
        var report = System.Diagnostics.Stopwatch.StartNew();
        while ((read = await input.ReadAsync(buffer, ct)) != 0)
        {
            await output.WriteAsync(buffer.AsMemory(0, read), ct); count += read;
            if (report.ElapsedMilliseconds >= 200) { bytes(count); report.Restart(); }
            if (artifact.Size.HasValue && count > artifact.Size.Value) throw new InvalidDataException("Tamaño de componente inesperado.");
        }
        await output.FlushAsync(ct);
        if (artifact.Size.HasValue && count != artifact.Size.Value) throw new IOException("Descarga incompleta.");
        File.Move(partial, destination, true); File.Delete(statePath); bytes(count);
    }

    private static async Task<DownloadState?> ReadStateAsync(string path, CancellationToken ct)
    {
        if (!File.Exists(path)) return null;
        try { return await TrainingJson.ReadAsync<DownloadState>(path, ct); }
        catch (Exception ex) when (ex is JsonException or InvalidDataException or IOException)
        { File.Delete(path); return null; }
    }
    private async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, HttpCompletionOption option, string operation, CancellationToken ct)
    {
        try
        {
            var response = await _http.SendAsync(request, option, ct);
            if (!response.IsSuccessStatusCode)
            {
                var statusCode = response.StatusCode;
                var status = (int)statusCode;
                response.Dispose();
                throw new HttpRequestException($"No se pudo {operation}: HTTP {status}, {request.RequestUri}.", null, statusCode);
            }
            return response;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        { throw new HttpRequestException($"Tiempo agotado al {operation}: {request.RequestUri}."); }
        catch (HttpRequestException ex) when (!ex.Message.Contains(request.RequestUri!.ToString(), StringComparison.OrdinalIgnoreCase))
        { throw new HttpRequestException($"Error de red al {operation}: {request.RequestUri}. {ex.Message}", ex, ex.StatusCode); }
    }
}
