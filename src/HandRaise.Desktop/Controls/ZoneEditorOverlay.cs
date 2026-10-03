using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using HandRaise.Application.Zones;
using HandRaise.Desktop.ViewModels;

namespace HandRaise.Desktop.Controls;

public sealed class ZoneEditorOverlay : FrameworkElement
{
    public static readonly DependencyProperty ZonesProperty = DependencyProperty.Register(
        nameof(Zones), typeof(IReadOnlyList<NormalizedZone>), typeof(ZoneEditorOverlay),
        new FrameworkPropertyMetadata(Array.Empty<NormalizedZone>(), FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty LinesProperty = DependencyProperty.Register(
        nameof(Lines), typeof(IReadOnlyList<HandRaise.Domain.Lines.LineDefinition>), typeof(ZoneEditorOverlay),
        new FrameworkPropertyMetadata(Array.Empty<HandRaise.Domain.Lines.LineDefinition>(), FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty DraftPointsProperty = DependencyProperty.Register(
        nameof(DraftPoints), typeof(IReadOnlyList<NormalizedPoint>), typeof(ZoneEditorOverlay),
        new FrameworkPropertyMetadata(Array.Empty<NormalizedPoint>(), FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty EditingProperty = DependencyProperty.Register(
        nameof(Editing), typeof(bool), typeof(ZoneEditorOverlay),
        new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty FrameWidthProperty = DependencyProperty.Register(
        nameof(FrameWidth), typeof(int), typeof(ZoneEditorOverlay),
        new FrameworkPropertyMetadata(0, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty FrameHeightProperty = DependencyProperty.Register(
        nameof(FrameHeight), typeof(int), typeof(ZoneEditorOverlay),
        new FrameworkPropertyMetadata(0, FrameworkPropertyMetadataOptions.AffectsRender));

    private int _dragPoint = -1;
    private string? _dragZone;

    public IReadOnlyList<NormalizedZone> Zones { get => (IReadOnlyList<NormalizedZone>)GetValue(ZonesProperty); set => SetValue(ZonesProperty, value); }
    public IReadOnlyList<HandRaise.Domain.Lines.LineDefinition> Lines { get => (IReadOnlyList<HandRaise.Domain.Lines.LineDefinition>)GetValue(LinesProperty); set => SetValue(LinesProperty, value); }
    public IReadOnlyList<NormalizedPoint> DraftPoints { get => (IReadOnlyList<NormalizedPoint>)GetValue(DraftPointsProperty); set => SetValue(DraftPointsProperty, value); }
    public bool Editing { get => (bool)GetValue(EditingProperty); set => SetValue(EditingProperty, value); }
    public int FrameWidth { get => (int)GetValue(FrameWidthProperty); set => SetValue(FrameWidthProperty, value); }
    public int FrameHeight { get => (int)GetValue(FrameHeightProperty); set => SetValue(FrameHeightProperty, value); }

    protected override void OnRender(DrawingContext drawingContext)
    {
        base.OnRender(drawingContext);
        if (FrameWidth <= 0 || FrameHeight <= 0) return;
        drawingContext.DrawRectangle(Brushes.Transparent, null, new Rect(RenderSize));
        var rect = VideoRect();
        var linePen = new Pen(Brushes.DeepSkyBlue, 2);
        foreach (var zone in Zones)
        {
            DrawPolygon(drawingContext, zone.Points, rect, linePen, true);
            if (Editing)
            {
                foreach (var point in zone.Points) DrawHandle(drawingContext, ToView(point, rect), Brushes.White);
            }
        }
        if (Lines is { Count: > 0 })
        {
            var tripPen = new Pen(Brushes.OrangeRed, 2);
            foreach (var line in Lines)
            {
                var ptA = ToView(new NormalizedPoint(line.PointA.X, line.PointA.Y), rect);
                var ptB = ToView(new NormalizedPoint(line.PointB.X, line.PointB.Y), rect);
                drawingContext.DrawLine(tripPen, ptA, ptB);
                DrawHandle(drawingContext, ptA, Brushes.Cyan);
                DrawHandle(drawingContext, ptB, Brushes.Magenta);
            }
        }
        if (Editing)
        {
            DrawPolygon(drawingContext, DraftPoints, rect, new Pen(Brushes.Gold, 2), false);
            foreach (var point in DraftPoints) DrawHandle(drawingContext, ToView(point, rect), Brushes.Gold);
        }
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        if (!Editing || DataContext is not CameraViewModel viewModel || !TryNormalize(e.GetPosition(this), out var point)) return;
        if (e.ClickCount >= 2)
        {
            if (viewModel.CloseZoneCommand.CanExecute(null)) viewModel.CloseZoneCommand.Execute(null);
            return;
        }
        if (viewModel.SelectedZone is { } selected && TryFindPoint(selected, e.GetPosition(this), out var index))
        {
            _dragZone = selected.Name;
            _dragPoint = index;
            viewModel.BeginZonePointDrag();
            CaptureMouse();
        }
        else
        {
            viewModel.AddZonePoint(point);
        }
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        if (_dragPoint < 0 || _dragZone is null || DataContext is not CameraViewModel viewModel ||
            !TryNormalize(e.GetPosition(this), out var point)) return;
        viewModel.MoveZonePoint(_dragZone, _dragPoint, point);
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        if (_dragPoint < 0 || DataContext is not CameraViewModel viewModel) return;
        _dragPoint = -1;
        _dragZone = null;
        ReleaseMouseCapture();
        _ = viewModel.EndZonePointDragAsync();
    }

    private bool TryFindPoint(NormalizedZone zone, Point mouse, out int index)
    {
        var rect = VideoRect();
        for (var i = 0; i < zone.Points.Count; i++)
        {
            if ((ToView(zone.Points[i], rect) - mouse).Length <= 12)
            {
                index = i;
                return true;
            }
        }
        index = -1;
        return false;
    }

    private bool TryNormalize(Point point, out NormalizedPoint normalized)
    {
        var rect = VideoRect();
        if (!rect.Contains(point))
        {
            normalized = new(0, 0);
            return false;
        }
        normalized = new(
            Math.Clamp((point.X - rect.X) / rect.Width, 0, 1),
            Math.Clamp((point.Y - rect.Y) / rect.Height, 0, 1));
        return true;
    }

    private Rect VideoRect()
    {
        var scale = Math.Min(ActualWidth / FrameWidth, ActualHeight / FrameHeight);
        var width = FrameWidth * scale;
        var height = FrameHeight * scale;
        return new((ActualWidth - width) / 2, (ActualHeight - height) / 2, width, height);
    }

    private static Point ToView(NormalizedPoint point, Rect rect) =>
        new(rect.X + (point.X * rect.Width), rect.Y + (point.Y * rect.Height));

    private static void DrawHandle(DrawingContext context, Point point, Brush brush) =>
        context.DrawEllipse(brush, new Pen(Brushes.Black, 1), point, 5, 5);

    private static void DrawPolygon(
        DrawingContext context,
        IReadOnlyList<NormalizedPoint> points,
        Rect rect,
        Pen pen,
        bool close)
    {
        if (points.Count == 0) return;
        for (var i = 1; i < points.Count; i++)
        {
            context.DrawLine(pen, ToView(points[i - 1], rect), ToView(points[i], rect));
        }
        if (close && points.Count > 2)
        {
            context.DrawLine(pen, ToView(points[^1], rect), ToView(points[0], rect));
        }
    }
}
