using System.ComponentModel;
using System.Windows;
using HandRaise.Desktop.ViewModels;

namespace HandRaise.Desktop;

public partial class MainWindow : Window
{
    private readonly MainViewModel _viewModel;
    private bool _isClosing;

    public MainWindow(MainViewModel viewModel)
    {
        InitializeComponent();
        _viewModel = viewModel;
        DataContext = viewModel;
        Closing += OnClosing;
    }

    private async void OnClosing(object? sender, CancelEventArgs eventArgs)
    {
        if (_isClosing)
        {
            return;
        }

        eventArgs.Cancel = true;
        _isClosing = true;
        IsEnabled = false;

        try
        {
            Closing -= OnClosing;
            await _viewModel.DisposeAsync();
        }
        catch (Exception exception)
        {
            MessageBox.Show(
                exception.Message,
                "Error durante el cierre",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }
        finally
        {
            System.Windows.Application.Current?.Shutdown();
        }
    }
}
