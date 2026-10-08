using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using HandRaise.Domain.Training;

namespace HandRaise.Desktop.Controls;

public static class TrainingBoxCoordinates
{
    public static Rect ImageRect(Size surface, double width, double height)
    {
        if (width <= 0 || height <= 0 || surface.Width <= 0 || surface.Height <= 0) return Rect.Empty;
        var scale = Math.Min(surface.Width / width, surface.Height / height);
        return new((surface.Width - width * scale) / 2, (surface.Height - height * scale) / 2, width * scale, height * scale);
    }
    public static Point Normalize(Point point, Rect image) => new(Math.Clamp((point.X - image.X) / image.Width, 0, 1), Math.Clamp((point.Y - image.Y) / image.Height, 0, 1));
    public static BoundingBoxAnnotation? FromCorners(Guid cls, Point a, Point b)
    {
        var left = Math.Clamp(Math.Min(a.X, b.X), 0, 1); var top = Math.Clamp(Math.Min(a.Y, b.Y), 0, 1);
        var right = Math.Clamp(Math.Max(a.X, b.X), 0, 1); var bottom = Math.Clamp(Math.Max(a.Y, b.Y), 0, 1);
        if (right - left < .001 || bottom - top < .001 || cls == Guid.Empty) return null;
        return new(cls, (left + right) / 2, (top + bottom) / 2, right - left, bottom - top);
    }
    public static Rect ToRect(BoundingBoxAnnotation box, Rect image) => new(image.X + (box.XCenter - box.Width / 2) * image.Width,
        image.Y + (box.YCenter - box.Height / 2) * image.Height, box.Width * image.Width, box.Height * image.Height);
}

public sealed class TrainingBoxEditor : FrameworkElement
{
    public static readonly DependencyProperty ImageProperty = DependencyProperty.Register(nameof(Image), typeof(ImageSource), typeof(TrainingBoxEditor), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty BoxesProperty = DependencyProperty.Register(nameof(Boxes), typeof(ObservableCollection<BoundingBoxAnnotation>), typeof(TrainingBoxEditor), new FrameworkPropertyMetadata(null, OnBoxesChanged));
    public static readonly DependencyProperty ClassesProperty = DependencyProperty.Register(nameof(Classes), typeof(ObservableCollection<TrainingClass>), typeof(TrainingBoxEditor), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty ActiveClassProperty = DependencyProperty.Register(nameof(ActiveClass), typeof(TrainingClass), typeof(TrainingBoxEditor));
    public static readonly DependencyProperty SelectedIndexProperty = DependencyProperty.Register(nameof(SelectedIndex), typeof(int), typeof(TrainingBoxEditor), new FrameworkPropertyMetadata(-1, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault | FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty CanEditProperty = DependencyProperty.Register(nameof(CanEdit), typeof(bool), typeof(TrainingBoxEditor), new PropertyMetadata(true));
    public ImageSource? Image { get => (ImageSource?)GetValue(ImageProperty); set => SetValue(ImageProperty, value); }
    public ObservableCollection<BoundingBoxAnnotation>? Boxes { get => (ObservableCollection<BoundingBoxAnnotation>?)GetValue(BoxesProperty); set => SetValue(BoxesProperty, value); }
    public ObservableCollection<TrainingClass>? Classes { get => (ObservableCollection<TrainingClass>?)GetValue(ClassesProperty); set => SetValue(ClassesProperty, value); }
    public TrainingClass? ActiveClass { get => (TrainingClass?)GetValue(ActiveClassProperty); set => SetValue(ActiveClassProperty, value); }
    public int SelectedIndex { get => (int)GetValue(SelectedIndexProperty); set => SetValue(SelectedIndexProperty, value); }
    public bool CanEdit { get => (bool)GetValue(CanEditProperty); set => SetValue(CanEditProperty, value); }
    private Point _start;
    private BoundingBoxAnnotation? _original, _draft;
    private bool _resizing;
    private Rect ImageBounds => Image == null ? Rect.Empty : TrainingBoxCoordinates.ImageRect(RenderSize, Image.Width, Image.Height);

    private static void OnBoxesChanged(DependencyObject sender, DependencyPropertyChangedEventArgs e)
    {
        var control = (TrainingBoxEditor)sender;
        if (e.OldValue is INotifyCollectionChanged old) old.CollectionChanged -= control.Redraw;
        if (e.NewValue is INotifyCollectionChanged current) current.CollectionChanged += control.Redraw;
        control.InvalidateVisual();
    }
    private void Redraw(object? sender, NotifyCollectionChangedEventArgs args) => InvalidateVisual();
    protected override void OnRender(DrawingContext dc)
    {
        base.OnRender(dc);
        dc.DrawRectangle(Brushes.Black, null, new Rect(RenderSize));
        var bounds = ImageBounds;
        if (Image == null || bounds.IsEmpty) return;
        dc.DrawImage(Image, bounds);
        if (Boxes != null)
            for (var i = 0; i < Boxes.Count; i++) DrawBox(dc, IsMouseCaptured && i == SelectedIndex && _draft != null ? _draft : Boxes[i], i == SelectedIndex);
        if (_draft != null && SelectedIndex == -1) DrawBox(dc, _draft, true);
    }
    private void DrawBox(DrawingContext dc, BoundingBoxAnnotation box, bool selected)
    {
        var rect = TrainingBoxCoordinates.ToRect(box, ImageBounds);
        var brush = selected ? Brushes.Gold : Brushes.DeepSkyBlue;
        dc.DrawRectangle(null, new Pen(brush, selected ? 3 : 2), rect);
        dc.DrawRectangle(brush, null, new Rect(rect.Right - 4, rect.Bottom - 4, 8, 8));
        var name = Classes?.FirstOrDefault(c => c.Id == box.ClassId)?.Name ?? "Objeto";
        var text = new FormattedText(name, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, new Typeface("Segoe UI"), 13, Brushes.White, VisualTreeHelper.GetDpi(this).PixelsPerDip);
        var location = new Point(rect.Left, Math.Max(ImageBounds.Top, rect.Top - text.Height - 3));
        dc.DrawRectangle(Brushes.Black, null, new Rect(location, new Size(text.Width + 6, text.Height + 2)));
        dc.DrawText(text, location);
    }
    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        if (!CanEdit || Boxes == null || ImageBounds.IsEmpty || !ImageBounds.Contains(e.GetPosition(this))) return;
        _start = TrainingBoxCoordinates.Normalize(e.GetPosition(this), ImageBounds);
        SelectedIndex = -1; _original = null; _draft = null;
        for (var i = Boxes.Count - 1; i >= 0; i--)
        {
            var rect = TrainingBoxCoordinates.ToRect(Boxes[i], ImageBounds);
            if (!rect.Contains(e.GetPosition(this))) continue;
            SelectedIndex = i; _original = Boxes[i];
            _resizing = (rect.BottomRight - e.GetPosition(this)).Length < 14;
            break;
        }
        CaptureMouse(); e.Handled = true; InvalidateVisual();
    }
    protected override void OnMouseMove(MouseEventArgs e)
    {
        if (!IsMouseCaptured || !CanEdit || ImageBounds.IsEmpty) return;
        var current = TrainingBoxCoordinates.Normalize(e.GetPosition(this), ImageBounds);
        if (_original == null) _draft = TrainingBoxCoordinates.FromCorners(ActiveClass?.Id ?? Guid.Empty, _start, current);
        else if (_resizing) _draft = TrainingBoxCoordinates.FromCorners(_original.ClassId,
            new(_original.XCenter - _original.Width / 2, _original.YCenter - _original.Height / 2), current);
        else _draft = _original with { XCenter = Math.Clamp(_original.XCenter + current.X - _start.X, _original.Width / 2, 1 - _original.Width / 2),
            YCenter = Math.Clamp(_original.YCenter + current.Y - _start.Y, _original.Height / 2, 1 - _original.Height / 2) };
        InvalidateVisual();
    }
    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        if (!IsMouseCaptured) return;
        if (CanEdit && _draft != null && Boxes != null)
        {
            if (SelectedIndex >= 0 && SelectedIndex < Boxes.Count) Boxes[SelectedIndex] = _draft;
            else { Boxes.Add(_draft); SelectedIndex = Boxes.Count - 1; }
        }
        _draft = null; ReleaseMouseCapture(); InvalidateVisual(); e.Handled = true;
    }
}
