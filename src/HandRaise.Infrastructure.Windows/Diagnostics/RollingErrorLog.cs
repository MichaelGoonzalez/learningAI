using System.Text.RegularExpressions;

namespace HandRaise.Infrastructure.Windows.Diagnostics;

public sealed partial class RollingErrorLog
{
    private readonly object _sync = new();
    private readonly long _maximumBytes;
    private readonly int _retainedFiles;

    public RollingErrorLog(string? directory = null, long maximumBytes = 2 * 1024 * 1024, int retainedFiles = 4)
    {
        DirectoryPath = directory ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "HandRaiseDetection", "logs");
        LogPath = Path.Combine(DirectoryPath, "handraise.log");
        _maximumBytes = maximumBytes;
        _retainedFiles = retainedFiles;
    }

    public string DirectoryPath { get; }
    public string LogPath { get; }

    public string Write(Exception exception, string context)
    {
        lock (_sync)
        {
            Directory.CreateDirectory(DirectoryPath);
            RotateIfNeeded();
            var entry = $"{DateTimeOffset.UtcNow:O} [{Sanitize(context)}]{Environment.NewLine}" +
                        $"{Sanitize(exception.ToString())}{Environment.NewLine}";
            File.AppendAllText(LogPath, entry);
            return LogPath;
        }
    }

    public static string Sanitize(string value)
    {
        var sanitized = RtspCredentials().Replace(value, "rtsp://***@");
        return SecretQueryValues().Replace(sanitized, "$1=***");
    }

    private void RotateIfNeeded()
    {
        if (!File.Exists(LogPath) || new FileInfo(LogPath).Length < _maximumBytes) return;
        for (var index = _retainedFiles; index >= 1; index--)
        {
            var source = index == 1 ? LogPath : $"{LogPath}.{index - 1}";
            var destination = $"{LogPath}.{index}";
            if (File.Exists(source)) File.Move(source, destination, true);
        }
    }

    [GeneratedRegex(@"rtsp://[^/\s]*@", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex RtspCredentials();

    [GeneratedRegex(@"\b(password|passwd|pwd|token)=([^&\s]+)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex SecretQueryValues();
}
