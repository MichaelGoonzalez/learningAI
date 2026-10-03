using System.Security.Cryptography;
using System.Text.Json;
using HandRaise.Infrastructure.Windows.Inference;

namespace HandRaise.Infrastructure.Windows.Tests;

public sealed class ModelIntegrityVerifierTests
{
    [Fact]
    public async Task AcceptsMatchingModelHash()
    {
        var paths = await CreateFilesAsync([1, 2, 3, 4]);
        try
        {
            var result = await ModelIntegrityVerifier.VerifyAsync(paths.Model, paths.Manifest);
            Assert.True(result.Success, result.Message);
        }
        finally
        {
            Directory.Delete(paths.Directory, true);
        }
    }

    [Fact]
    public async Task RejectsChangedModel()
    {
        var paths = await CreateFilesAsync([1, 2, 3, 4]);
        try
        {
            await File.WriteAllBytesAsync(paths.Model, [4, 3, 2, 1]);
            var result = await ModelIntegrityVerifier.VerifyAsync(paths.Model, paths.Manifest);
            Assert.False(result.Success);
            Assert.Contains("SHA-256", result.Message);
        }
        finally
        {
            Directory.Delete(paths.Directory, true);
        }
    }

    private static async Task<(string Directory, string Model, string Manifest)> CreateFilesAsync(byte[] content)
    {
        var directory = Path.Combine(Path.GetTempPath(), $"handraise-model-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        var model = Path.Combine(directory, "model.onnx");
        var manifest = Path.Combine(directory, "model.manifest.json");
        await File.WriteAllBytesAsync(model, content);
        var hash = Convert.ToHexString(SHA256.HashData(content)).ToLowerInvariant();
        await File.WriteAllTextAsync(manifest, JsonSerializer.Serialize(new { artifact = new { sha256 = hash } }));
        return (directory, model, manifest);
    }
}
