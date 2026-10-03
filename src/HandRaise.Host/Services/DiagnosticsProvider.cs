using System.Diagnostics;
using System.IO;
using System.Reflection;
using HandRaise.Application.Pipelines;
using HandRaise.Infrastructure.Windows.Diagnostics;
using AppHostOptions = HandRaise.Host.Configuration.HostOptions;

namespace HandRaise.Host.Services;

public sealed class DiagnosticsProvider(
    AppHostOptions options,
    PipelineMetricsRegistry metrics,
    HostRuntimeState runtimeState,
    PreflightChecker preflight)
{
    public NodeDiagnosticsReport GetDiagnostics()
    {
        var snapshot = metrics.Snapshot();
        var report = preflight.BuildReport(snapshot.UptimeSeconds, options.NodeId, options.SiteId, isRunning: true);
        var activeDevice = runtimeState.Inventory.Devices.FirstOrDefault(d => d.Id == runtimeState.ActiveDeviceId);
        var recentErrors = ReadRecentErrors();

        var version = Assembly.GetEntryAssembly()?.GetName().Version?.ToString(3) ?? "1.0.0";
        var memBytes = Process.GetCurrentProcess().WorkingSet64;

        return new NodeDiagnosticsReport(
            Status: report.Status,
            Version: version,
            NodeId: options.NodeId,
            SiteId: options.SiteId,
            UptimeSeconds: snapshot.UptimeSeconds,
            ActiveDeviceId: runtimeState.ActiveDeviceId ?? "unknown",
            ActiveDeviceName: activeDevice?.Name ?? "No detectado",
            InferenceBackend: activeDevice?.Backend.ToString() ?? "Unknown",
            WorkingSetBytes: memBytes,
            CameraCount: snapshot.Cameras.Count,
            OnlineCameraCount: snapshot.Cameras.Count(c => c.Online),
            Checks: report.Checks,
            RecentErrors: recentErrors);
    }

    private static IReadOnlyList<string> ReadRecentErrors()
    {
        try
        {
            var logDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "HandRaiseDetection", "logs");
            if (!Directory.Exists(logDir)) return [];
            var files = new DirectoryInfo(logDir)
                .GetFiles("*.log")
                .OrderByDescending(f => f.LastWriteTimeUtc)
                .Take(5)
                .Select(f =>
                {
                    try
                    {
                        var firstLine = File.ReadLines(f.FullName).FirstOrDefault() ?? f.Name;
                        return $"{f.LastWriteTimeUtc:yyyy-MM-dd HH:mm:ss} UTC · {RollingErrorLog.Sanitize(firstLine)}";
                    }
                    catch
                    {
                        return f.Name;
                    }
                })
                .ToList();
            return files;
        }
        catch
        {
            return [];
        }
    }
}

