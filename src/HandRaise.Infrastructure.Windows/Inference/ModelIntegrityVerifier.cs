using System.Security.Cryptography;
using System.Text.Json;

namespace HandRaise.Infrastructure.Windows.Inference;

public sealed record ModelIntegrityResult(bool Success, string Message, string? Sha256 = null);

public static class ModelIntegrityVerifier
{
    public static async Task<ModelIntegrityResult> VerifyAsync(
        string modelPath,
        string manifestPath,
        CancellationToken cancellationToken = default)
    {
        if (!File.Exists(modelPath))
        {
            return new(false, $"No se encontró el modelo: {modelPath}");
        }
        if (!File.Exists(manifestPath))
        {
            return new(false, $"No se encontró el manifiesto del modelo: {manifestPath}");
        }

        string? expected;
        try
        {
            await using var manifestStream = File.OpenRead(manifestPath);
            using var manifest = await JsonDocument.ParseAsync(manifestStream, cancellationToken: cancellationToken);
            expected = manifest.RootElement.GetProperty("artifact").GetProperty("sha256").GetString();
        }
        catch (Exception exception) when (exception is JsonException or KeyNotFoundException or InvalidOperationException)
        {
            return new(false, $"El manifiesto del modelo no es válido: {exception.Message}");
        }

        if (string.IsNullOrWhiteSpace(expected))
        {
            return new(false, "El manifiesto no contiene artifact.sha256.");
        }

        await using var modelStream = File.OpenRead(modelPath);
        var actual = Convert.ToHexString(await SHA256.HashDataAsync(modelStream, cancellationToken)).ToLowerInvariant();
        return string.Equals(actual, expected.Trim(), StringComparison.OrdinalIgnoreCase)
            ? new(true, "Modelo verificado.", actual)
            : new(false, $"El modelo no coincide con el SHA-256 del manifiesto. Esperado: {expected}; obtenido: {actual}.", actual);
    }
}
