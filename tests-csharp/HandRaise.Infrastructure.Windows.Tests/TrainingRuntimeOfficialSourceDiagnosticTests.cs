using HandRaise.Infrastructure.Windows.Training;

namespace HandRaise.Infrastructure.Windows.Tests;

public sealed class TrainingRuntimeOfficialSourceDiagnosticTests
{
    [Fact(Skip = "Manual lightweight metadata/HEAD diagnostic; never downloads runtime artifacts.")]
    public async Task OfficialManifestCanBeResolved()
    {
        var assets = Path.Combine(AppContext.BaseDirectory, "training-worker");
        var manifest = await new OfficialTrainingRuntimeSource(assets).ResolveAsync(default);
        Assert.NotEmpty(manifest.Artifacts);
        using var http = new HttpClient(new HttpClientHandler { AllowAutoRedirect = true }) { Timeout = TimeSpan.FromSeconds(30) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("VisionControl-Edge-MetadataCheck/1.0");
        foreach (var artifact in manifest.Artifacts)
        {
            using var request = new HttpRequestMessage(HttpMethod.Head, artifact.Url);
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
            Assert.True(response.IsSuccessStatusCode, $"{artifact.Name}: {(int)response.StatusCode} {artifact.Url}");
            Assert.Matches("^[0-9a-fA-F]{64}$", artifact.Sha256);
        }
    }
}
