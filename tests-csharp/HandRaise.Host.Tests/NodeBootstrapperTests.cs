using System.IO;
using HandRaise.Host.Configuration;
using HandRaise.Host.Services;

namespace HandRaise.Host.Tests;

public sealed class NodeBootstrapperTests
{
    [Fact]
    public void BootstrapCreatesDirectoriesAndGeneratesPersistentIdentity()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), $"handraise-bootstrap-test-{Guid.NewGuid():N}");
        try
        {
            var options = new HostOptions
            {
                NodeId = "local-node",
                Storage = new StorageOptions
                {
                    DatabasePath = Path.Combine(tempRoot, "db", "events.db"),
                    SnapshotDirectory = Path.Combine(tempRoot, "snapshots"),
                    CameraStorePath = Path.Combine(tempRoot, "config", "cameras.json"),
                    CameraCredentialStorePath = Path.Combine(tempRoot, "config", "credentials.bin")
                }
            };

            NodeBootstrapper.Bootstrap(options);

            Assert.True(Directory.Exists(Path.GetDirectoryName(options.Storage.DatabasePath)));
            Assert.True(Directory.Exists(options.Storage.SnapshotDirectory));
            Assert.True(Directory.Exists(Path.GetDirectoryName(options.Storage.CameraStorePath)));
            Assert.True(Directory.Exists(Path.GetDirectoryName(options.Storage.CameraCredentialStorePath)));

            var identityFile = Path.Combine(Path.GetDirectoryName(options.Storage.DatabasePath)!, "node-identity.json");
            Assert.True(File.Exists(identityFile));
            var savedId = options.NodeId;
            Assert.False(string.IsNullOrWhiteSpace(savedId));

            // Test idempotency: re-running bootstrap keeps the same NodeId
            var secondaryOptions = new HostOptions
            {
                NodeId = "local-node",
                Storage = options.Storage
            };
            NodeBootstrapper.Bootstrap(secondaryOptions);
            Assert.Equal(savedId, secondaryOptions.NodeId);
        }
        finally
        {
            if (Directory.Exists(tempRoot))
            {
                Directory.Delete(tempRoot, true);
            }
        }
    }
}

