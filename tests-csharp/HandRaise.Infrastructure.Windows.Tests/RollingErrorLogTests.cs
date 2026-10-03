using HandRaise.Infrastructure.Windows.Diagnostics;

namespace HandRaise.Infrastructure.Windows.Tests;

public sealed class RollingErrorLogTests
{
    [Fact]
    public void RemovesRtspCredentialsFromLog()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"handraise-log-{Guid.NewGuid():N}");
        try
        {
            var log = new RollingErrorLog(directory);
            var path = log.Write(new InvalidOperationException("rtsp://admin:secret@camera.local/live"), "capture");
            var text = File.ReadAllText(path);

            Assert.DoesNotContain("admin", text);
            Assert.DoesNotContain("secret", text);
            Assert.Contains("rtsp://***@camera.local/live", text);
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, true);
        }
    }
}
