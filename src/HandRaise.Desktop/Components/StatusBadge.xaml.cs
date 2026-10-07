using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace HandRaise.Desktop.Components;

public partial class StatusBadge : UserControl
{
    public static readonly DependencyProperty TextProperty =
        DependencyProperty.Register(nameof(Text), typeof(string), typeof(StatusBadge),
            new PropertyMetadata(string.Empty, OnTextChanged));

    public static readonly DependencyProperty StatusProperty =
        DependencyProperty.Register(nameof(Status), typeof(string), typeof(StatusBadge),
            new PropertyMetadata(string.Empty, OnStatusChanged));

    public string Text
    {
        get => (string)GetValue(TextProperty);
        set => SetValue(TextProperty, value);
    }

    public string Status
    {
        get => (string)GetValue(StatusProperty);
        set => SetValue(StatusProperty, value);
    }

    public StatusBadge()
    {
        InitializeComponent();
    }

    private static void OnTextChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is StatusBadge badge && badge.StatusText != null)
        {
            var text = e.NewValue as string ?? string.Empty;
            badge.StatusText.Text = NormalizeStatusText(text);
        }
    }

    private static string NormalizeStatusText(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return string.Empty;
        var lower = text.Trim().ToLowerInvariant();
        return lower switch
        {
            "running" or "online" or "en línea" or "en linea" => "En línea",
            "stopped" or "detenida" or "desconectada" or "desconectado" => "Detenida",
            "connecting" or "conectando" or "iniciando" => "Iniciando",
            "reconnecting" or "reconectando" => "Reconectando",
            "faulted" or "error" => "Error",
            "degraded" => "Degradada",
            "healthy" => "Saludable",
            "unhealthy" => "No saludable",
            _ => text
        };
    }

    private static void OnStatusChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is StatusBadge badge)
        {
            badge.UpdateAppearance(e.NewValue as string ?? string.Empty);
        }
    }

    private void UpdateAppearance(string status)
    {
        if (StatusDot == null || BadgeBorder == null) return;

        var normalized = status.Trim().ToLowerInvariant();
        if (normalized.Contains("en línea") || normalized.Contains("en linea") || normalized.Contains("online") || normalized.Contains("running") || normalized.Contains("saludable") || normalized.Contains("listo") || normalized.Contains("ok") || normalized.Contains("activa"))
        {
            StatusDot.Fill = FindResource("VisionSuccess") as Brush ?? Brushes.Green;
            BadgeBorder.BorderBrush = FindResource("VisionSuccess") as Brush ?? Brushes.Green;
        }
        else if (normalized.Contains("reconectando") || normalized.Contains("iniciando") || normalized.Contains("conectando") || normalized.Contains("warning") || normalized.Contains("alerta"))
        {
            StatusDot.Fill = FindResource("VisionWarning") as Brush ?? Brushes.Orange;
            BadgeBorder.BorderBrush = FindResource("VisionWarning") as Brush ?? Brushes.Orange;
        }
        else if (normalized.Contains("error") || normalized.Contains("fallo") || normalized.Contains("crítico") || normalized.Contains("faulted"))
        {
            StatusDot.Fill = FindResource("VisionDanger") as Brush ?? Brushes.Red;
            BadgeBorder.BorderBrush = FindResource("VisionDanger") as Brush ?? Brushes.Red;
        }
        else
        {
            StatusDot.Fill = FindResource("VisionTextSecondary") as Brush ?? Brushes.Gray;
            BadgeBorder.BorderBrush = FindResource("VisionBorder") as Brush ?? Brushes.Gray;
        }
    }
}

