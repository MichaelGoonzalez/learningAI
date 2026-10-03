using System.IO;
using System.Threading;
using System.Windows;
using System.Windows.Threading;
using HandRaise.Desktop.Configuration;
using HandRaise.Desktop.Services;
using HandRaise.Desktop.ViewModels;
using HandRaise.Infrastructure.Windows.Diagnostics;

namespace HandRaise.Desktop;

public partial class App : System.Windows.Application
{
    private static Mutex? _singleInstanceMutex;
    private readonly RollingErrorLog _errorLog = new();
    private MainViewModel? _viewModel;
    private NodeHostController? _hostController;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        const string mutexName = @"Global\HandRaiseDetection_Desktop_Appliance";
        _singleInstanceMutex = new Mutex(true, mutexName, out var isOnlyInstance);
        if (!isOnlyInstance)
        {
            MessageBox.Show(
                "Hand Raise Detection ya se encuentra en ejecución en este equipo.",
                "Instancia en ejecución",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            Shutdown(0);
            return;
        }

        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += OnAppDomainUnhandledException;
        TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;

        try
        {
            var configuration = DesktopConfiguration.Load();
            _hostController = new NodeHostController();
            _viewModel = new MainViewModel(
                configuration,
                _hostController,
                Dispatcher,
                ApplyTheme);

            var window = new MainWindow(_viewModel);
            MainWindow = window;
            window.Show();

            await _viewModel.InitializeAsync();
        }
        catch (Exception exception)
        {
            var logPath = WriteError(exception, "inicio");
            MessageBox.Show(
                $"{RollingErrorLog.Sanitize(exception.Message)}\n\nDetalles: {logPath}",
                "No fue posible iniciar Hand Raise Detection",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            Shutdown(1);
        }
    }

    protected override async void OnExit(ExitEventArgs e)
    {
        DispatcherUnhandledException -= OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException -= OnAppDomainUnhandledException;
        TaskScheduler.UnobservedTaskException -= OnUnobservedTaskException;

        if (_viewModel is not null)
        {
            await _viewModel.DisposeAsync();
        }

        if (_hostController is not null)
        {
            await _hostController.DisposeAsync();
        }

        if (_singleInstanceMutex is not null)
        {
            try
            {
                _singleInstanceMutex.ReleaseMutex();
            }
            catch
            {
                // Mutex might not have been acquired
            }
            _singleInstanceMutex.Dispose();
            _singleInstanceMutex = null;
        }

        base.OnExit(e);
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        e.Handled = true;
        var path = WriteError(e.Exception, "UI");
        MessageBox.Show($"Ocurrió un error no controlado.\n\nRegistro: {path}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
        Shutdown(1);
    }

    private void OnAppDomainUnhandledException(object? sender, UnhandledExceptionEventArgs e)
    {
        var exception = e.ExceptionObject as Exception ?? new Exception("Error no controlado sin detalle.");
        WriteError(exception, "proceso");
    }

    private void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        var path = WriteError(e.Exception, "tarea");
        e.SetObserved();
        Dispatcher.BeginInvoke(() => MessageBox.Show(
            $"Una tarea en segundo plano produjo un error.\n\nRegistro: {path}",
            "Error en segundo plano", MessageBoxButton.OK, MessageBoxImage.Warning));
    }

    private string WriteError(Exception exception, string context)
    {
        try
        {
            return _errorLog.Write(exception, context);
        }
        catch
        {
            return _errorLog.DirectoryPath;
        }
    }

    private void ApplyTheme(bool dark)
    {
        var colors = dark
            ? new Dictionary<string, string>
            {
                ["WindowBackground"] = "#101317",
                ["Surface"] = "#191E24",
                ["SurfaceAlt"] = "#222932",
                ["PrimaryText"] = "#F2F5F7",
                ["SecondaryText"] = "#AAB4BE",
                ["BorderBrush"] = "#303945"
            }
            : new Dictionary<string, string>
            {
                ["WindowBackground"] = "#F3F5F7",
                ["Surface"] = "#FFFFFF",
                ["SurfaceAlt"] = "#E8EDF2",
                ["PrimaryText"] = "#1B232B",
                ["SecondaryText"] = "#5B6772",
                ["BorderBrush"] = "#D3DAE1"
            };
        foreach (var (key, value) in colors)
        {
            Resources[key] = new System.Windows.Media.SolidColorBrush(
                (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(value));
        }
    }
}

