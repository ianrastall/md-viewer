using System.ComponentModel;
using MdViewer.Interop;
using MdViewer.Services;
using MdViewer.ViewModels;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;
using Windows.Storage.Pickers;
using Windows.System;

namespace MdViewer;

public sealed partial class MainWindow : Window, IViewerDialogs
{
    public MainViewModel ViewModel { get; }
    private bool allowClose, closing, sized;

    public MainWindow()
    {
        InitializeComponent();
        AppWindow.SetIcon(Path.Combine(AppContext.BaseDirectory, "Assets", "AppIcon.ico"));
        ViewModel = new MainViewModel(this);
        try { Core.ConfigureOcr(ViewModel.Settings.OcrEngine, ViewModel.Settings.TesseractLanguages); }
        catch (NativeCoreException ex) { AppLog.Write("OCR settings", ex); }
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);
        var titleBar = AppWindow.TitleBar;
        titleBar.ButtonBackgroundColor = Colors.Transparent;
        titleBar.ButtonInactiveBackgroundColor = Colors.Transparent;
        titleBar.ButtonForegroundColor = ((SolidColorBrush)Application.Current.Resources["TextBrush"]).Color;
        titleBar.ButtonHoverBackgroundColor = ((SolidColorBrush)Application.Current.Resources["LineBrush"]).Color;
        titleBar.ButtonInactiveForegroundColor = ((SolidColorBrush)Application.Current.Resources["QuietBrush"]).Color;
        AppWindow.Closing += OnClosing;

        OutlineColumn.Width = new GridLength(Math.Clamp(ViewModel.OutlineWidth, OutlineColumn.MinWidth, OutlineColumn.MaxWidth));
        Splitter.Target = OutlineColumn;
        Splitter.Resized += (_, width) => ViewModel.OutlineWidth = width;
        Reader.LinkInvoked += Reader_LinkInvoked;
        Reader.ZoomRequested += (_, direction) => (direction > 0 ? ViewModel.ZoomInCommand : ViewModel.ZoomOutCommand).Execute(null);
        ViewModel.PropertyChanged += ViewModelChanged;
        Title = ViewModel.WindowTitle;

        AddShortcut(VirtualKey.R, VirtualKeyModifiers.Control, () => ViewModel.ToggleRawCommand.Execute(null));
        foreach (var key in new[] { VirtualKey.Add, (VirtualKey)187 })
            AddShortcut(key, VirtualKeyModifiers.Control, () => ViewModel.ZoomInCommand.Execute(null));
        foreach (var key in new[] { VirtualKey.Subtract, (VirtualKey)189 })
            AddShortcut(key, VirtualKeyModifiers.Control, () => ViewModel.ZoomOutCommand.Execute(null));
        foreach (var key in new[] { VirtualKey.Number0, VirtualKey.NumberPad0 })
            AddShortcut(key, VirtualKeyModifiers.Control, () => ViewModel.ResetZoomCommand.Execute(null));

        RootGrid.Loaded += (_, _) =>
        {
            if (sized) return;
            sized = true;
            // AppWindow sizes are physical pixels; XAML measures in effective pixels.
            var scale = RootGrid.XamlRoot.RasterizationScale;
            var workArea = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Primary).WorkArea;
            var width = Math.Min((int)(1440 * scale), workArea.Width - 48);
            var height = Math.Min((int)(940 * scale), workArea.Height - 48);
            AppWindow.MoveAndResize(new(workArea.X + (workArea.Width - width) / 2, workArea.Y + (workArea.Height - height) / 2, width, height));
            if (AppWindow.Presenter is OverlappedPresenter presenter)
            {
                presenter.PreferredMinimumWidth = Math.Min((int)(720 * scale), workArea.Width);
                presenter.PreferredMinimumHeight = Math.Min((int)(480 * scale), workArea.Height);
            }
        };
    }

    /// <summary>Opens the launch file, or the welcome page.</summary>
    public async Task StartAsync(string? path)
    {
        await ViewModel.ShowWelcomeAsync();
        if (path is not null) await ViewModel.OpenFileAsync(path);
        await ViewModel.CheckToolsInBackgroundAsync();
    }

    private void AddShortcut(VirtualKey key, VirtualKeyModifiers modifiers, Action action)
    {
        var shortcut = new KeyboardAccelerator { Key = key, Modifiers = modifiers };
        shortcut.Invoked += (_, e) => { action(); e.Handled = true; };
        RootGrid.KeyboardAccelerators.Add(shortcut);
    }

    private void ViewModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(MainViewModel.Document):
                Reader.Show(ViewModel.Document, ViewModel.ZoomFactor, ViewModel.BaseDirectory, keepPosition: false);
                RebuildOutline();
                break;
            case nameof(MainViewModel.ZoomFactor):
                Reader.Show(ViewModel.Document, ViewModel.ZoomFactor, ViewModel.BaseDirectory, keepPosition: true);
                break;
            case nameof(MainViewModel.WindowTitle):
                Title = ViewModel.WindowTitle;
                break;
        }
    }

    // Nodes are built directly so every level starts expanded.
    private void RebuildOutline()
    {
        OutlineTree.RootNodes.Clear();
        foreach (var heading in ViewModel.Headings) OutlineTree.RootNodes.Add(Node(heading));
        static TreeViewNode Node(HeadingNode heading)
        {
            var node = new TreeViewNode { Content = heading, IsExpanded = true };
            foreach (var child in heading.Children) node.Children.Add(Node(child));
            return node;
        }
    }

    // Clicking navigates (even when the heading is already selected); so does moving through the outline with the keyboard.
    private void Outline_ItemInvoked(TreeView sender, TreeViewItemInvokedEventArgs args) => NavigateTo(args.InvokedItem as TreeViewNode);
    private void Outline_SelectionChanged(TreeView sender, TreeViewSelectionChangedEventArgs args) => NavigateTo(sender.SelectedNode);

    private void NavigateTo(TreeViewNode? selected)
    {
        if (selected?.Content is not HeadingNode node) return;
        if (ViewModel.IsRawView) ScrollRawToLine(node.Heading.Line);
        else Reader.ScrollToBlock(node.Heading.BlockIndex);
    }

    private void ScrollRawToLine(int line)
    {
        if (line <= 0) return;
        // TextBox stores line breaks as '\r'.
        var text = RawEditor.Text;
        var offset = 0;
        for (var current = 1; current < line && offset < text.Length; current++)
        {
            var next = text.IndexOf('\r', offset);
            offset = next < 0 ? text.Length : next + 1;
        }
        RawEditor.Focus(FocusState.Programmatic);
        RawEditor.Select(text.Length, 0);
        RawEditor.Select(offset, 0);
    }

    private async void Reader_LinkInvoked(object? sender, string url)
    {
        try
        {
            if (url.StartsWith('#'))
            {
                ScrollToAnchor(url[1..]);
                return;
            }
            if (Uri.TryCreate(url, UriKind.Absolute, out var uri) && !uri.IsFile)
            {
                if (uri.Scheme is "http" or "https" or "mailto") await Launcher.LaunchUriAsync(uri);
                else ViewModel.Status = $"Links with the {uri.Scheme}: scheme are not opened.";
                return;
            }
            var (path, anchor) = ResolveLocalLink(url);
            if (path is not null && File.Exists(path) && Core.KindOf(path) == FileKind.Markdown)
            {
                if (!path.Equals(ViewModel.FilePath, StringComparison.OrdinalIgnoreCase)) await ViewModel.OpenFileAsync(path);
                if (anchor is not null && path.Equals(ViewModel.FilePath, StringComparison.OrdinalIgnoreCase)) ScrollToAnchor(anchor);
            }
            else ViewModel.Status = path is null ? $"Could not resolve {url}." : $"Not opened: {path}";
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            AppLog.Write("Link failed", ex);
            ViewModel.Status = $"Could not open {url}: {ex.Message}";
        }
    }

    private void ScrollToAnchor(string anchor)
    {
        var slug = Uri.UnescapeDataString(anchor).ToLowerInvariant();
        if (ViewModel.Document?.Headings.FirstOrDefault(h => h.Slug == slug) is { } heading) Reader.ScrollToBlock(heading.BlockIndex);
        else ViewModel.Status = $"No heading matches #{anchor}.";
    }

    private (string? Path, string? Anchor) ResolveLocalLink(string url)
    {
        var hash = url.IndexOf('#');
        var anchor = hash >= 0 ? url[(hash + 1)..] : null;
        var target = hash >= 0 ? url[..hash] : url;
        try
        {
            if (Uri.TryCreate(target, UriKind.Absolute, out var uri) && uri.IsFile) return (uri.LocalPath, anchor);
            if (ViewModel.BaseDirectory is not { } directory) return (null, anchor);
            return (Path.GetFullPath(Path.Combine(directory, Uri.UnescapeDataString(target).Replace('/', Path.DirectorySeparatorChar))), anchor);
        }
        catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException or UriFormatException) { return (null, anchor); }
    }

    private void Root_DragOver(object sender, DragEventArgs e)
    {
        if (!e.DataView.Contains(StandardDataFormats.StorageItems) || ViewModel.IsBusy) return;
        e.AcceptedOperation = DataPackageOperation.Copy;
        e.DragUIOverride.Caption = "Open in md-viewer";
    }

    private async void Root_Drop(object sender, DragEventArgs e)
    {
        if (!e.DataView.Contains(StandardDataFormats.StorageItems)) return;
        var items = await e.DataView.GetStorageItemsAsync();
        if (items.OfType<StorageFile>().FirstOrDefault() is { Path.Length: > 0 } file) await ViewModel.OpenFileAsync(file.Path);
    }

    private async void OnClosing(AppWindow sender, AppWindowClosingEventArgs args)
    {
        if (allowClose) return;
        args.Cancel = true;
        if (closing) return;
        closing = true;
        try
        {
            if (ViewModel.IsBusy) ViewModel.CancelOperationCommand.Execute(null);
            tools?.Close();
            if (await ViewModel.CanLeaveAsync()) { allowClose = true; Close(); }
        }
        finally { closing = false; }
    }

    private void UpdateBar_Close(InfoBar sender, object args) => ViewModel.DismissUpdateNotice();

    // ---- IViewerDialogs ----------------------------------------------------

    private ToolsWindow? tools;

    public void ShowTools()
    {
        ViewModel.DismissUpdateNotice();
        if (tools is null)
        {
            tools = new ToolsWindow(new ToolsViewModel(ViewModel.Settings));
            tools.Closed += (_, _) => tools = null;
        }
        tools.Activate();
    }

    // Window.Close() does not raise AppWindow.Closing, so ask about unsaved work here.
    public async void CloseWindow()
    {
        if (closing) return;
        closing = true;
        try { if (await ViewModel.CanLeaveAsync()) { allowClose = true; Close(); } }
        finally { closing = false; }
    }

    private ContentDialog Dialog(string title, object content, string? primary = null, string close = "Close") => new()
    {
        XamlRoot = RootGrid.XamlRoot, RequestedTheme = ElementTheme.Dark, Title = title, Content = content,
        PrimaryButtonText = primary ?? "", CloseButtonText = close,
        DefaultButton = primary is null ? ContentDialogButton.Close : ContentDialogButton.Primary
    };

    private T Picker<T>(T picker) where T : class
    {
        WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(this));
        return picker;
    }

    public async Task<string?> PickOpenPathAsync()
    {
        var picker = Picker(new FileOpenPicker { SuggestedStartLocation = PickerLocationId.DocumentsLibrary, ViewMode = PickerViewMode.List });
        foreach (var type in Core.FileTypes.Where(t => t.Kind != FileKind.Export)) picker.FileTypeFilter.Add(type.Extension);
        var file = await picker.PickSingleFileAsync();
        if (file is null) return null;
        if (string.IsNullOrEmpty(file.Path)) throw new IOException("The selected file has no local path. Copy it to a local folder and try again.");
        return file.Path;
    }

    public async Task<string?> PickSavePathAsync(string suggestedName)
    {
        var picker = Picker(new FileSavePicker { SuggestedStartLocation = PickerLocationId.DocumentsLibrary, SuggestedFileName = suggestedName, DefaultFileExtension = ".md" });
        picker.FileTypeChoices.Add("Markdown", new List<string> { ".md" });
        picker.FileTypeChoices.Add("Plain text", new List<string> { ".txt" });
        return (await picker.PickSaveFileAsync())?.Path;
    }

    public async Task<string?> PickExportPathAsync(string suggestedName)
    {
        var picker = Picker(new FileSavePicker { SuggestedStartLocation = PickerLocationId.DocumentsLibrary, SuggestedFileName = suggestedName, DefaultFileExtension = ".docx" });
        foreach (var type in Core.FileTypes.Where(t => t.Kind == FileKind.Export)) picker.FileTypeChoices.Add(type.Label, new List<string> { type.Extension });
        return (await picker.PickSaveFileAsync())?.Path;
    }

    public async Task<string?> PromptUrlAsync()
    {
        var box = new TextBox { Header = "Start page", PlaceholderText = "https://example.com/docs/", MinWidth = 440, IsSpellCheckEnabled = false };
        var panel = new StackPanel { Spacing = 12 };
        panel.Children.Add(box);
        panel.Children.Add(new TextBlock
        {
            Text = "Pages in the same folder as the start page are collected, one at a time, honoring robots.txt. Wikipedia and other MediaWiki articles are fetched as a single page.",
            TextWrapping = TextWrapping.Wrap, MaxWidth = 440, FontSize = 12, Foreground = (Brush)Application.Current.Resources["MutedBrush"]
        });
        var dialog = Dialog("Crawl documentation", panel, "Crawl", "Cancel");
        box.Loaded += (_, _) => box.Focus(FocusState.Programmatic);
        return await dialog.ShowAsync() == ContentDialogResult.Primary ? box.Text : null;
    }

    public async Task<UnsavedChoice> ConfirmUnsavedAsync(string documentName)
    {
        var dialog = Dialog("Save your changes?", new TextBlock { Text = $"{documentName} has changes that are not saved as Markdown.", TextWrapping = TextWrapping.Wrap, MaxWidth = 440 }, "Save", "Cancel");
        dialog.SecondaryButtonText = "Don't save";
        return await dialog.ShowAsync() switch
        {
            ContentDialogResult.Primary => UnsavedChoice.Save,
            ContentDialogResult.Secondary => UnsavedChoice.Discard,
            _ => UnsavedChoice.Cancel
        };
    }

    public async Task ShowErrorAsync(string title, string message) =>
        await Dialog(title, new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap, MaxWidth = 480, IsTextSelectionEnabled = true }).ShowAsync();
}
