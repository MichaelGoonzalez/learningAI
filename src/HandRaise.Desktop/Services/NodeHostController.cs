using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using HandRaise.Application.Events;
using HandRaise.Application.Pipelines;
using HandRaise.Application.Storage;
using HandRaise.Host;
using HandRaise.Host.Configuration;
using HandRaise.Host.Services;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace HandRaise.Desktop.Services;

public enum NodeHostStatus
{
    Stopped,
    Starting,
    Running,
    PortConflict,
    Error
}

public sealed record NodeHostSettings(
    string BindAddress = "127.0.0.1",
    int Port = 5080,
    string? ApiKey = null,
    bool ApiEnabled = true);

public sealed class NodeHostController : IAsyncDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private WebApplication? _app;
    private NodeHostSettings _currentSettings = new();
    private NodeHostStatus _status = NodeHostStatus.Stopped;
    private string _statusMessage = "Detenido";

    public NodeHostStatus Status => _status;
    public string StatusMessage => _statusMessage;
    public bool IsRunning => _status == NodeHostStatus.Running;
    public NodeHostSettings CurrentSettings => _currentSettings;

    public string BaseUrl => $"http://{(_currentSettings.BindAddress == "0.0.0.0" ? "127.0.0.1" : _currentSettings.BindAddress)}:{_currentSettings.Port}";

    public IServiceProvider? Services => _app?.Services;
    public PipelineMetricsRegistry? Metrics => _app?.Services.GetService<PipelineMetricsRegistry>();
    public HostRuntimeState? RuntimeState => _app?.Services.GetService<HostRuntimeState>();
    public PreflightChecker? Preflight => _app?.Services.GetService<PreflightChecker>();
    public DiagnosticsProvider? Diagnostics => _app?.Services.GetService<DiagnosticsProvider>();
    public ICameraManagementService? CameraService => _app?.Services.GetService<ICameraManagementService>();
    public IHandEventRepository? EventRepository => _app?.Services.GetService<IHandEventRepository>();
    public HandEventBus? EventBus => _app?.Services.GetService<HandEventBus>();
    public INodeCredentialStore? CredentialStore => _app?.Services.GetService<INodeCredentialStore>();

    public void UpdateApiKey(string newKey)
    {
        _currentSettings = _currentSettings with { ApiKey = newKey };
        if (_app?.Services.GetService<ApiOptions>() is { } apiOptions)
        {
            apiOptions.ApiKey = newKey;
        }
    }

    public event Action<NodeHostStatus, string>? StatusChanged;

    public async Task<bool> StartAsync(NodeHostSettings? settings = null, CancellationToken token = default)
    {
        await _gate.WaitAsync(token);
        try
        {
            if (_app is not null)
            {
                return true;
            }

            _currentSettings = settings ?? _currentSettings;
            SetStatus(NodeHostStatus.Starting, "Iniciando motor de visión y servicios...");

            // Validar si el puerto está libre
            if (!IsPortAvailable(_currentSettings.Port, _currentSettings.BindAddress))
            {
                SetStatus(NodeHostStatus.PortConflict,
                    $"Puerto {_currentSettings.Port} no disponible o en uso por otra aplicación.");
                return false;
            }

            try
            {
                var app = HostApplication.Build([], startCameras: true, builder =>
                {
                    builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
                    {
                        ["api:port"] = _currentSettings.Port.ToString(),
                        ["api:bindAddress"] = _currentSettings.BindAddress,
                        ["api:apiKey"] = _currentSettings.ApiKey
                    });
                });

                await app.StartAsync(token);
                _app = app;
                SetStatus(NodeHostStatus.Running, "Operativo");
                return true;
            }
            catch (IOException ex) when (ex.InnerException is SocketException or SocketException)
            {
                SetStatus(NodeHostStatus.PortConflict,
                    $"Puerto {_currentSettings.Port} ocupado por otra instancia o servicio.");
                return false;
            }
            catch (Exception ex)
            {
                SetStatus(NodeHostStatus.Error,
                    $"Error al iniciar nodo: {ex.Message}");
                return false;
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task StopAsync(CancellationToken token = default)
    {
        await _gate.WaitAsync(token);
        try
        {
            if (_app is null)
            {
                SetStatus(NodeHostStatus.Stopped, "Detenido");
                return;
            }

            SetStatus(NodeHostStatus.Starting, "Deteniendo servicios y liberando hardware...");
            try
            {
                await _app.StopAsync(token);
                await _app.DisposeAsync();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Error during host shutdown: {ex.Message}");
            }
            finally
            {
                _app = null;
                SetStatus(NodeHostStatus.Stopped, "Detenido");
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<bool> RestartAsync(NodeHostSettings? settings = null, CancellationToken token = default)
    {
        await StopAsync(token);
        await Task.Delay(300, token);
        return await StartAsync(settings, token);
    }

    private void SetStatus(NodeHostStatus status, string message)
    {
        _status = status;
        _statusMessage = message;
        StatusChanged?.Invoke(status, message);
    }

    public static bool IsPortAvailable(int port, string bindAddress)
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

    public async ValueTask DisposeAsync()
    {
        await StopAsync(CancellationToken.None);
        _gate.Dispose();
    }
}
