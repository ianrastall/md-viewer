using MdViewer.Core.Markdown;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using VirtualKeyModifiers = Windows.System.VirtualKeyModifiers;

namespace MdViewer.Controls;

/// <summary>
/// The reading surface. Top-level blocks are realized on demand by an ItemsRepeater,
/// so very long documents (crawled sites, large PDFs) stay responsive.
/// </summary>
public sealed partial class MarkdownView : UserControl
{
    private const double ReadingWidth = 1000;
    private readonly ScrollViewer scroller;
    private readonly ItemsRepeater repeater;
    private readonly BlockFactory factory = new();
    private MarkdownDocument? document;

    public event EventHandler<string>? LinkInvoked;
    /// <summary>Ctrl+wheel: +1 to zoom in, -1 to zoom out.</summary>
    public event EventHandler<int>? ZoomRequested;

    public MarkdownView()
    {
        repeater = new ItemsRepeater { Layout = new StackLayout(), HorizontalAlignment = HorizontalAlignment.Stretch, ItemTemplate = factory };
        scroller = new ScrollViewer
        {
            Content = repeater,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            ZoomMode = ZoomMode.Disabled,
            IsTabStop = true
        };
        Content = scroller;
        AddHandler(PointerWheelChangedEvent, new PointerEventHandler(OnPointerWheelChanged), handledEventsToo: true);
    }

    public void Show(MarkdownDocument? value, double zoom, string? baseDirectory, bool keepPosition)
    {
        var ratio = keepPosition && scroller.ScrollableHeight > 0 ? scroller.VerticalOffset / scroller.ScrollableHeight : 0;
        document = value;
        // Clearing the source recycles every host, so the same document re-renders at a new zoom.
        repeater.ItemsSource = null;
        factory.Renderer = new MarkdownRenderer(zoom, baseDirectory, url => LinkInvoked?.Invoke(this, url));
        factory.First = value?.Blocks.FirstOrDefault();
        repeater.MaxWidth = ReadingWidth * zoom;
        repeater.Margin = new Thickness(56 * zoom, 36 * zoom, 56 * zoom, 120);
        repeater.ItemsSource = value?.Blocks;
        scroller.UpdateLayout();
        scroller.ChangeView(null, ratio * scroller.ScrollableHeight, null, disableAnimation: true);
    }

    public void ScrollToBlock(int index)
    {
        if (document is null || index < 0 || index >= document.Blocks.Count) return;
        var element = repeater.GetOrCreateElement(index);
        element.UpdateLayout();
        element.StartBringIntoView(new BringIntoViewOptions { VerticalAlignmentRatio = 0, AnimationDesired = false });
        // Virtualized layout estimates positions of unrealized blocks; settle on the measured position.
        DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, () => Settle(element, 3));
    }

    private void Settle(UIElement element, int attempts)
    {
        if (!ReferenceEquals(VisualParent(element), repeater)) return;
        var top = element.TransformToVisual(repeater).TransformPoint(default).Y + repeater.Margin.Top - 16;
        if (Math.Abs(scroller.VerticalOffset - top) < 1) return;
        scroller.ChangeView(null, Math.Max(0, top), null, disableAnimation: true);
        if (attempts > 1) DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, () => Settle(element, attempts - 1));
    }

    private static DependencyObject? VisualParent(UIElement element) => Microsoft.UI.Xaml.Media.VisualTreeHelper.GetParent(element);

    public void FocusReader() => scroller.Focus(FocusState.Programmatic);

    private void OnPointerWheelChanged(object sender, PointerRoutedEventArgs e)
    {
        if (!e.KeyModifiers.HasFlag(VirtualKeyModifiers.Control)) return;
        var delta = e.GetCurrentPoint(this).Properties.MouseWheelDelta;
        if (delta != 0) ZoomRequested?.Invoke(this, Math.Sign(delta));
        e.Handled = true;
    }

    /// <summary>
    /// Reuses lightweight host borders (they stay children of the repeater while recycled, as WinUI's own
    /// pools do); each block's content is rebuilt when it scrolls back into view.
    /// </summary>
    private sealed partial class BlockFactory : IElementFactory
    {
        private readonly Stack<Border> pool = new();
        public MarkdownRenderer? Renderer { get; set; }
        public MarkdownBlock? First { get; set; }

        public UIElement GetElement(ElementFactoryGetArgs args)
        {
            var host = pool.Count > 0 ? pool.Pop() : new Border();
            if (args.Data is MarkdownBlock block && Renderer is not null) host.Child = Renderer.Build(block, ReferenceEquals(block, First));
            return host;
        }

        public void RecycleElement(ElementFactoryRecycleArgs args)
        {
            if (args.Element is not Border host) return;
            host.Child = null;
            pool.Push(host);
        }
    }
}