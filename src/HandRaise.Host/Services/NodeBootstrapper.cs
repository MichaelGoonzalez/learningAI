using System.IO;
using System.Text.Json;
using AppHostOptions = HandRaise.Host.Configuration.HostOptions;

namespace HandRaise.Host.Services;

public static class NodeBootstrapper
{
    private sealed record NodeIdentity(string NodeId, DateTime CreatedAtUtc);

    public static string Bootstrap(AppHostOptions options)
    {
        // 1. Ensure required directories exist
        var dbDir = Path.GetDirectoryName(options.Storage.DatabasePath);
        if (!string.IsNullOrEmpty(dbDir)) Directory.CreateDirectory(dbDir);

        Directory.CreateDirectory(options.Storage.SnapshotDirectory);

        var cameraDir = Path.GetDirectoryName(options.Storage.CameraStorePath);
        if (!string.IsNullOrEmpty(cameraDir)) Directory.CreateDirectory(cameraDir);

        var credDir = Path.GetDirectoryName(options.Storage.CameraCredentialStorePath);
        if (!string.IsNullOrEmpty(credDir)) Directory.CreateDirectory(credDir);

        var nodeCredDir = Path.GetDirectoryName(options.Storage.NodeCredentialStorePath);
        if (!string.IsNullOrEmpty(nodeCredDir)) Directory.CreateDirectory(nodeCredDir);

        var analyticDir = Path.GetDirectoryName(options.Storage.AnalyticStorePath);
        if (!string.IsNullOrEmpty(analyticDir)) Directory.CreateDirectory(analyticDir);

        // 2. Persistent Node Identity if needed
        var dataDir = dbDir ?? AppDomain.CurrentDomain.BaseDirectory;
        var identityPath = Path.Combine(dataDir, "node-identity.json");

        if (File.Exists(identityPath))
        {
            try
            {
                var content = File.ReadAllText(identityPath);
                var identity = JsonSerializer.Deserialize<NodeIdentity>(content);
                if (identity is not null && !string.IsNullOrWhiteSpace(identity.NodeId))
                {
                    return identity.NodeId;
                }
            }
            catch
            {
                // Fallback to configured
            }
        }
        else
        {
            try
            {
                var newId = string.IsNullOrWhiteSpace(options.NodeId) ? $"node-{Guid.NewGuid():N}" : options.NodeId;
                var identity = new NodeIdentity(newId, DateTime.UtcNow);
                var json = JsonSerializer.Serialize(identity, new JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(identityPath, json);
                return newId;
            }
            catch
            {
                // Non-fatal if directory is read-only
            }
        }

        return options.NodeId;
    }
}

