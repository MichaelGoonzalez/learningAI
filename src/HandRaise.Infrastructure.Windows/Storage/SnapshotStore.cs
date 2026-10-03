using System.Security.Cryptography;
using System.Text;

namespace HandRaise.Infrastructure.Windows.Storage;

public sealed class SnapshotStore
{
    private readonly string _root;

    public SnapshotStore(string root)
    {
        _root = Path.GetFullPath(Environment.ExpandEnvironmentVariables(root));
        Directory.CreateDirectory(_root);
    }

    public string Root => _root;

    public async Task<string> SaveAsync(
        string eventId,
        DateTimeOffset timestamp,
        byte[] jpeg,
        CancellationToken cancellationToken = default)
    {
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(eventId))).ToLowerInvariant();
        var relative = Path.Combine(
            timestamp.UtcDateTime.ToString("yyyy"),
            timestamp.UtcDateTime.ToString("MM"),
            timestamp.UtcDateTime.ToString("dd"),
            $"{hash}.jpg");
        var target = Resolve(relative);
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        var temporary = $"{target}.{Guid.NewGuid():N}.tmp";
        try
        {
            await File.WriteAllBytesAsync(temporary, jpeg, cancellationToken);
            File.Move(temporary, target, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary))
            {
                File.Delete(temporary);
            }
        }

        return relative.Replace(Path.DirectorySeparatorChar, '/');
    }

    public bool Delete(string relativePath)
    {
        var path = Resolve(relativePath);
        if (!File.Exists(path))
        {
            return false;
        }

        File.Delete(path);
        return true;
    }

    public long GetLength(string relativePath)
    {
        var path = Resolve(relativePath);
        return File.Exists(path) ? new FileInfo(path).Length : 0;
    }

    public IReadOnlyList<string> EnumerateRelativeJpegs() => Directory.Exists(_root)
        ? Directory.EnumerateFiles(_root, "*.jpg", SearchOption.AllDirectories)
            .Select(path => Path.GetRelativePath(_root, path).Replace(Path.DirectorySeparatorChar, '/'))
            .ToArray()
        : [];

    private string Resolve(string relativePath)
    {
        var normalized = relativePath.Replace('/', Path.DirectorySeparatorChar);
        var resolved = Path.GetFullPath(Path.Combine(_root, normalized));
        var prefix = _root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!resolved.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("La ruta del snapshot sale de la carpeta configurada.");
        }

        return resolved;
    }
}
