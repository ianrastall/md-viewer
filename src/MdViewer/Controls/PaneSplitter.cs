using Microsoft.UI;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;

namespace MdViewer.Controls;

/// <summary>A thin vertical grip that resizes the grid column to its left.</summary>
public sealed partial class PaneSplitter : Grid
{
    private double? dragStart;
    private double startWidth;

    public ColumnDefinition? Target { get; set; }
    public event EventHandler<double>? Resized;

    public PaneSplitter()
    {
        Width = 6;
        Background = new SolidColorBrush(Colors.Transparent);
        ProtectedCursor = InputSystemCursor.Create(InputSystemCursorShape.SizeWestEast);
        Children.Add(new Border { Width = 1, HorizontalAlignment = HorizontalAlignment.Left, Background = (Brush)Application.Current.Resources["LineBrush"] });
        PointerPressed += OnPressed;
        PointerMoved += OnMoved;
        PointerReleased += (_, e) => End(e.Pointer);
        PointerCaptureLost += (_, e) => End(e.Pointer);
    }

    private void OnPressed(object sender, PointerRoutedEventArgs e)
    {
        if (Target is null) return;
        dragStart = e.GetCurrentPoint(null).Position.X;
        startWidth = Target.ActualWidth;
        CapturePointer(e.Pointer);
        e.Handled = true;
    }

    private void OnMoved(object sender, PointerRoutedEventArgs e)
    {
        if (dragStart is not { } start || Target is null) return;
        var width = Math.Clamp(startWidth + e.GetCurrentPoint(null).Position.X - start, Target.MinWidth, Target.MaxWidth);
        Target.Width = new GridLength(width);
        e.Handled = true;
    }

    private void End(Pointer pointer)
    {
        if (dragStart is null) return;
        dragStart = null;
        ReleasePointerCapture(pointer);
        if (Target is not null) Resized?.Invoke(this, Target.ActualWidth);
    }
}
