using System.Text.Json;
using HandRaise.Application.Settings;

namespace HandRaise.Infrastructure.Windows.Settings;

public sealed class JsonUserSettingsStore : IUserSettingsStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        WriteIndented = true
    };

    public JsonUserSettingsStore(string? filePath = null)
    {
        FilePath = filePath ?? GetDefaultPath();
    }

    public string FilePath { get; }

    public async Task<UserSettings> LoadAsync(CancellationToken cancellationToken = default)
    {
        if (!File.Exists(FilePath))
        {
            return new UserSettings();
        }

        try
        {
            await using var stream = File.OpenRead(FilePath);
            return await JsonSerializer.DeserializeAsync<UserSettings>(stream, JsonOptions, cancellationToken)
                ?? new UserSettings();
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException(
                $"El archivo de preferencias '{FilePath}' contiene JSON inválido.", exception);
        }
    }

    public async Task SaveAsync(UserSettings settings, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var directory = Path.GetDirectoryName(FilePath)
            ?? throw new InvalidOperationException("La ruta de preferencias no tiene directorio.");
        Directory.CreateDirectory(directory);

        var temporaryPath = Path.Combine(
            directory,
            $".{Path.GetFileName(FilePath)}.{Guid.NewGuid():N}.tmp");
        try
        {
            await using (var stream = new FileStream(
                temporaryPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 4096,
                FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await JsonSerializer.SerializeAsync(stream, settings, JsonOptions, cancellationToken);
                await stream.FlushAsync(cancellationToken);
            }

            File.Move(temporaryPath, FilePath, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }

    private static string GetDefaultPath()
    {
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrWhiteSpace(localAppData))
        {
            throw new InvalidOperationException("Windows no devolvió una ruta LOCALAPPDATA válida.");
        }

        return Path.Combine(localAppData, "HandRaiseDetection", "settings.json");
    }
}
