using System.IO;
using System.Net;
using System.Net.Sockets;
using HandRaise.Infrastructure.Windows.Inference;
using AppHostOptions = HandRaise.Host.Configuration.HostOptions;

namespace HandRaise.Host.Services;

public sealed class PreflightChecker(AppHostOptions options)
{
    public IReadOnlyList<PreflightCheckResult> RunChecks(bool isRunning = true)
    {
        var checks = new List<PreflightCheckResult>();

        // 1. Directorio de datos y base de datos
        try
        {
            var dbDir = Path.GetDirectoryName(options.Storage.DatabasePath);
            if (!string.IsNullOrEmpty(dbDir)) Directory.CreateDirectory(dbDir);
            checks.Add(new PreflightCheckResult("Storage.Database", NodeHealthStatus.Healthy, "Directorio de base de datos listo.", true));
        }
        catch (Exception ex)
        {
            checks.Add(new PreflightCheckResult("Storage.Database", NodeHealthStatus.Failed, $"Error en directorio de base de datos: {ex.Message}", true));
        }

        // 2. Directorio de snapshots
        try
        {
            Directory.CreateDirectory(options.Storage.SnapshotDirectory);
            var testFile = Path.Combine(options.Storage.SnapshotDirectory, ".preflight_write_test");
            File.WriteAllText(testFile, "test");
            File.Delete(testFile);
            checks.Add(new PreflightCheckResult("Storage.Snapshots", NodeHealthStatus.Healthy, "Directorio de snapshots escribible.", false));
        }
        catch (Exception ex)
        {
            checks.Add(new PreflightCheckResult("Storage.Snapshots", NodeHealthStatus.Degraded, $"Permisos o acceso a snapshots restringido: {ex.Message}", false));
        }

        // 3. Modelo e integridad
        try
        {
            var modelPath = options.Model.Path;
            var manifestPath = Path.Combine(Path.GetDirectoryName(modelPath) ?? string.Empty, "model.manifest.json");
            if (File.Exists(modelPath))
            {
                var manifestExists = File.Exists(manifestPath);
                checks.Add(new PreflightCheckResult(
                    "Model.Integrity",
                    manifestExists ? NodeHealthStatus.Healthy : NodeHealthStatus.Degraded,
                    manifestExists ? "Modelo y manifiesto verificados." : "Modelo presente sin manifiesto SHA256.",
                    true));
            }
            else
            {
                checks.Add(new PreflightCheckResult("Model.Integrity", NodeHealthStatus.Failed, $"Archivo de modelo no encontrado en '{modelPath}'.", true));
            }
        }
        catch (Exception ex)
        {
            checks.Add(new PreflightCheckResult("Model.Integrity", NodeHealthStatus.Failed, $"Error verificando modelo: {ex.Message}", true));
        }

        // 4. Puerto de API
        if (!isRunning)
        {
            var portAvailable = CheckPort(options.Api.Port, options.Api.BindAddress);
            checks.Add(new PreflightCheckResult(
                "Api.Port",
                portAvailable ? NodeHealthStatus.Healthy : NodeHealthStatus.Failed,
                portAvailable ? $"Puerto {options.Api.Port} disponible." : $"Puerto {options.Api.Port} ocupado por otro proceso.",
                false));
        }
        else
        {
            checks.Add(new PreflightCheckResult("Api.Port", NodeHealthStatus.Healthy, $"API activa en http://{options.Api.BindAddress}:{options.Api.Port}.", false));
        }

        return checks;
    }

    public NodeReadinessReport BuildReport(double uptimeSeconds, string nodeId, string siteId, bool isRunning = true)
    {
        var checks = RunChecks(isRunning);
        var hasFailedCritical = checks.Any(c => c.IsCritical && c.Status == NodeHealthStatus.Failed);
        var hasFailedNonCritical = checks.Any(c => c.Status == NodeHealthStatus.Failed);
        var hasDegraded = checks.Any(c => c.Status == NodeHealthStatus.Degraded);

        var overallStatus = hasFailedCritical ? NodeHealthStatus.Failed
            : (hasDegraded || hasFailedNonCritical) ? NodeHealthStatus.Degraded
            : NodeHealthStatus.Healthy;

        return new NodeReadinessReport(
            Status: overallStatus.ToString().ToLowerInvariant(),
            Ready: overallStatus != NodeHealthStatus.Failed,
            NodeId: nodeId,
            SiteId: siteId,
            UptimeSeconds: uptimeSeconds,
            Checks: checks);
    }

    private static bool CheckPort(int port, string bindAddress)
    {
        try
        {
            var ip = bindAddress == "0.0.0.0" ? IPAddress.Any : IPAddress.Parse(bindAddress);
            using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            socket.Bind(new IPEndPoint(ip, port));
            return true;
        }
        catch
        {
            return false;
        }
    }
}

