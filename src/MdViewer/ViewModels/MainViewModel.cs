using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MdViewer.Interop;
using MdViewer.Interop.Markdown;
using MdViewer.Services;

namespace MdViewer.ViewModels;

public enum DocumentOrigin { Welcome, Markdown, Imported, Crawled }

public partial class MainViewModel : ObservableObject
{
    public const int MinZoom = 50, MaxZoom = 250, ZoomStep = 10, MaxCrawlPages = 250;
    private readonly IViewerDialogs dialogs;
    private readonly ViewerSettings settings;
    private const int UndoLimit = 100;
    private readonly List<string> undoHistory = [], redoHistory = [];
    private CancellationTokenSource? operation;
    private string? savedText;
    private int renderGeneration;

    public MainViewModel(IViewerDialogs dialogs, ViewerSettings? settings = null)
    {
        this.dialogs = dialogs;
        this.settings = settings ?? ViewerSettings.Load();
        ZoomPercent = Math.Clamp(this.settings.ZoomPercent, MinZoom, MaxZoom);
    }

    [ObservableProperty] public partial string MarkdownText { get; private set; } = "";
    [ObservableProperty] public partial MarkdownDocument? Document { get; private set; }
    [ObservableProperty] public partial string? FilePath { get; private set; }
    [ObservableProperty] public partial string? SourcePath { get; private set; }
    [ObservableProperty] public partial DocumentOrigin Origin { get; private set; }
    [ObservableProperty] public partial bool IsDirty { get; private set; }
    [ObservableProperty] public partial bool IsRawView { get; set; }
    [ObservableProperty] public partial int ZoomPercent { get; set; } = 100;
    [ObservableProperty] public partial string Status { get; set; } = "Ready.";
    [ObservableProperty] public partial string EncodingLabel { get; private set; } = "UTF-8";
    [ObservableProperty] public partial bool IsBusy { get; private set; }
    [ObservableProperty] public partial bool IsCrawling { get; private set; }
    [ObservableProperty] public partial bool IsImporting { get; private set; }
    [ObservableProperty] public partial string CrawlStatus { get; private set; } = "";
    public ObservableCollection<HeadingNode> Headings { get; } = [];

    public string DocumentTitle => Origin switch
    {
        DocumentOrigin.Welcome => "Welcome",
        DocumentOrigin.Crawled => "Crawled documentation",
        _ => Path.GetFileName(FilePath ?? SourcePath) ?? "Untitled"
    };
    public string WindowTitle => $"{(IsDirty ? "● " : "")}{DocumentTitle} — md-viewer";
    public string DocumentState => Origin switch
    {
        DocumentOrigin.Welcome => "",
        _ when IsDirty && FilePath is null => "● Not saved yet",
        _ when IsDirty => "● Unsaved changes",
        _ => "Saved"
    };
    public string LocationText => FilePath ?? SourcePath ?? "";
    public string StatisticsText => Document is { } d ? $"{d.LineCount:N0} lines  ·  {d.WordCount:N0} words  ·  {d.CharacterCount:N0} chars  ·  {EncodingLabel}" : EncodingLabel;
    public string ZoomText => $"{ZoomPercent}%";
    public double ZoomFactor => ZoomPercent / 100.0;
    public string OutlineSummary => Document is { Headings.Count: > 0 } d ? $"{d.Headings.Count:N0}" : "";
    public bool HasOutline => Document is { Headings.Count: > 0 };
    public bool HasNoOutline => !HasOutline;
    public bool IsRichView => !IsRawView;
    public double RawFontSize => Math.Round(13.5 * ZoomFactor, 1);
    public double OutlineWidth
    {
        get => settings.OutlineWidth;
        set { settings.OutlineWidth = value; settings.Save(); }
    }
    public bool IsIdle => !IsBusy;
    public bool CanUndo => undoHistory.Count > 0;
    public bool CanRedo => redoHistory.Count > 0;
    /// <summary>Relative links and images resolve against the document's folder.</summary>
    public string? BaseDirectory => Path.GetDirectoryName(FilePath ?? SourcePath) is { Length: > 0 } directory ? directory : null;

    partial void OnMarkdownTextChanged(string value) => UpdateDirty();
    partial void OnOriginChanged(DocumentOrigin value) => NotifyTitle();
    partial void OnFilePathChanged(string? value) => NotifyTitle();
    partial void OnSourcePathChanged(string? value) => NotifyTitle();
    partial void OnIsDirtyChanged(bool value) => NotifyTitle();
    partial void OnEncodingLabelChanged(string value) => OnPropertyChanged(nameof(StatisticsText));
    partial void OnIsRawViewChanged(bool value) => OnPropertyChanged(nameof(IsRichView));
    partial void OnIsBusyChanged(bool value) => OnPropertyChanged(nameof(IsIdle));
    partial void OnDocumentChanged(MarkdownDocument? value)
    {
        Headings.Clear();
        foreach (var node in HeadingNode.BuildTree(value?.Headings ?? [])) Headings.Add(node);
        OnPropertyChanged(nameof(StatisticsText));
        OnPropertyChanged(nameof(OutlineSummary));
        OnPropertyChanged(nameof(HasOutline));
        OnPropertyChanged(nameof(HasNoOutline));
    }
    partial void OnZoomPercentChanged(int value)
    {
        var clamped = Math.Clamp(value, MinZoom, MaxZoom);
        if (clamped != value) { ZoomPercent = clamped; return; }
        OnPropertyChanged(nameof(ZoomText));
        OnPropertyChanged(nameof(ZoomFactor));
        OnPropertyChanged(nameof(RawFontSize));
        settings.ZoomPercent = value;
        settings.Save();
    }
    private void NotifyTitle()
    {
        OnPropertyChanged(nameof(DocumentTitle));
        OnPropertyChanged(nameof(WindowTitle));
        OnPropertyChanged(nameof(DocumentState));
        OnPropertyChanged(nameof(LocationText));
        OnPropertyChanged(nameof(BaseDirectory));
    }
    private void UpdateDirty() => IsDirty = savedText is null ? Origin is DocumentOrigin.Imported or DocumentOrigin.Crawled : MarkdownText != savedText;

    public Task ShowWelcomeAsync() => LoadAsync(WelcomeText, DocumentOrigin.Welcome, null, null, WelcomeText, "UTF-8");

    /// <summary>Opens Markdown directly; the C++ core converts Word, HTML, EPUB, ODT, RTF, and PDF to Markdown.</summary>
    public async Task OpenFileAsync(string path)
    {
        if (IsBusy) return;
        if (!await CanLeaveAsync()) return;
        var full = Path.GetFullPath(path);
        using var cancellation = new CancellationTokenSource();
        operation = cancellation;
        IsImporting = Core.KindOf(full) is FileKind.Pandoc or FileKind.Pdf;
        try
        {
            await RunAsync("Open", async () =>
            {
                var opened = await Core.OpenAsync(full, new Progress<string>(s => Status = s), cancellation.Token);
                if (opened.IsMarkdown) await LoadAsync(opened.Text, DocumentOrigin.Markdown, full, full, opened.Text, opened.Encoding);
                else await LoadAsync(opened.Text, DocumentOrigin.Imported, null, full, null, opened.Encoding);
                Status = opened.Summary;
                settings.LastFolder = Path.GetDirectoryName(full);
                settings.Save();
            });
        }
        finally { IsImporting = false; operation = null; }
    }

    [RelayCommand]
    private async Task OpenAsync()
    {
        if (IsBusy) return;
        var path = await dialogs.PickOpenPathAsync();
        if (path is not null) await OpenFileAsync(path);
    }

    [RelayCommand]
    private async Task SaveAsync() => await SaveCoreAsync(saveAs: false);

    [RelayCommand]
    private async Task SaveAsAsync() => await SaveCoreAsync(saveAs: true);

    private async Task<bool> SaveCoreAsync(bool saveAs)
    {
        if (IsBusy) return false;
        var path = !saveAs && Origin == DocumentOrigin.Markdown ? FilePath : null;
        path ??= await dialogs.PickSavePathAsync(SuggestedName);
        if (path is null) return false;
        var saved = false;
        await RunAsync("Save", async () =>
        {
            // Markdown files keep their encoding; converted documents are saved as UTF-8.
            EncodingLabel = await Core.SaveAsync(path, MarkdownText, Origin == DocumentOrigin.Markdown ? EncodingLabel : "UTF-8");
            savedText = MarkdownText;
            Origin = DocumentOrigin.Markdown;
            FilePath = SourcePath = Path.GetFullPath(path);
            UpdateDirty();
            Status = $"Saved {Path.GetFileName(path)}.";
            saved = true;
        });
        return saved;
    }

    [RelayCommand]
    private async Task CloseDocumentAsync()
    {
        if (IsBusy || !await CanLeaveAsync()) return;
        await ShowWelcomeAsync();
        Status = "Closed the document.";
    }

    [RelayCommand]
    private async Task ExportAsync()
    {
        if (IsBusy) return;
        var path = await dialogs.PickExportPathAsync(SuggestedName);
        if (path is null) return;
        await RunAsync("Export", async () =>
        {
            Status = $"Exporting {Path.GetFileName(path)} with Pandoc...";
            await Core.ExportAsync(MarkdownText, path, BaseDirectory);
            Status = $"Exported {Path.GetFileName(path)}.";
        });
    }

    [RelayCommand]
    private async Task FormatAsync()
    {
        if (IsBusy) return;
        await RunAsync("Format", async () =>
        {
            Status = "Formatting with Pandoc...";
            var formatted = await Core.FormatAsync(MarkdownText);
            await ApplyEditAsync(formatted, "Formatted with Pandoc.", "Pandoc made no changes.");
        });
    }

    [RelayCommand]
    private async Task ReflowAsync()
    {
        if (IsBusy) return;
        await RunAsync("Reflow", async () =>
        {
            var text = MarkdownText;
            var result = await Task.Run(() => MarkdownEngine.ReflowHeadings(text));
            var summary = result.ChangedHeadingCount == 0 ? "Reflow made no heading-level changes." : $"Reflow changed {result.ChangedHeadingCount:N0} heading level(s).";
            if (result.Warnings.Count > 0) summary += " " + result.Warnings[0] + (result.Warnings.Count > 1 ? $" (+{result.Warnings.Count - 1} more)" : "");
            await ApplyEditAsync(result.Markdown, summary, summary);
        });
    }

    [RelayCommand]
    private async Task CrawlAsync()
    {
        if (IsBusy || !await CanLeaveAsync()) return;
        var text = (await dialogs.PromptUrlAsync())?.Trim();
        if (string.IsNullOrEmpty(text)) return;
        if (!Uri.TryCreate(text, UriKind.Absolute, out var start) || start.Scheme is not ("http" or "https") || text.Any(char.IsWhiteSpace))
        {
            await dialogs.ShowErrorAsync("Crawl", $"{text} is not an http or https address.");
            return;
        }
        using var cancellation = new CancellationTokenSource();
        operation = cancellation;
        IsCrawling = true;
        CrawlStatus = $"Starting {start}";
        try
        {
            await RunAsync("Crawl", async () =>
            {
                try
                {
                    var markdown = await Core.CrawlAsync(start.AbsoluteUri, MaxCrawlPages, new Progress<string>(s => CrawlStatus = s), cancellation.Token);
                    await LoadAsync(markdown, DocumentOrigin.Crawled, null, null, null, "UTF-8");
                    Status = $"Crawled {start.Host}. Save to keep the Markdown.";
                }
                catch (OperationCanceledException) { Status = "Crawl cancelled."; }
            });
        }
        finally { IsCrawling = false; operation = null; }
    }

    /// <summary>Steps back through changes made by Format and Reflow. Saving does not clear the history.</summary>
    [RelayCommand(CanExecute = nameof(CanUndo))]
    private async Task UndoAsync()
    {
        if (IsBusy || undoHistory.Count == 0) return;
        redoHistory.Add(MarkdownText);
        await RestoreAsync(Pop(undoHistory), "Undid the last change.");
    }

    [RelayCommand(CanExecute = nameof(CanRedo))]
    private async Task RedoAsync()
    {
        if (IsBusy || redoHistory.Count == 0) return;
        undoHistory.Add(MarkdownText);
        await RestoreAsync(Pop(redoHistory), "Redid the change.");
    }

    private static string Pop(List<string> history)
    {
        var text = history[^1];
        history.RemoveAt(history.Count - 1);
        return text;
    }

    private async Task RestoreAsync(string text, string status)
    {
        MarkdownText = text;
        NotifyHistory();
        await RenderAsync();
        Status = status;
    }

    private void NotifyHistory()
    {
        OnPropertyChanged(nameof(CanUndo));
        OnPropertyChanged(nameof(CanRedo));
        UndoCommand.NotifyCanExecuteChanged();
        RedoCommand.NotifyCanExecuteChanged();
    }

    [RelayCommand]
    private void CancelOperation()
    {
        operation?.Cancel();
        Status = CrawlStatus = "Cancelling...";
    }

    [RelayCommand]
    private async Task FetchPandocAsync()
    {
        if (IsBusy) return;
        await RunAsync("Fetch Pandoc", async () =>
        {
            var result = await Core.FetchPandocAsync(new Progress<string>(s => Status = s));
            Status = $"Pandoc {result.Version} is ready ({result.Path}).";
        });
    }

    [RelayCommand] private void ZoomIn() => ZoomPercent += ZoomStep;
    [RelayCommand] private void ZoomOut() => ZoomPercent -= ZoomStep;
    [RelayCommand] private void ResetZoom() => ZoomPercent = 100;
    [RelayCommand] private void ToggleRaw() => IsRawView = !IsRawView;
    [RelayCommand] private void Exit() => dialogs.CloseWindow();

    /// <summary>Asks about unsaved work. Returns false when the user cancels or a save fails.</summary>
    public async Task<bool> CanLeaveAsync()
    {
        if (!IsDirty) return true;
        return await dialogs.ConfirmUnsavedAsync(DocumentTitle) switch
        {
            UnsavedChoice.Save => await SaveCoreAsync(saveAs: false),
            UnsavedChoice.Discard => true,
            _ => false
        };
    }

    private string SuggestedName => Origin == DocumentOrigin.Crawled ? "crawled-documentation" : Path.GetFileNameWithoutExtension(FilePath ?? SourcePath) ?? "Untitled";

    private async Task ApplyEditAsync(string text, string changed, string unchanged)
    {
        if (text == MarkdownText) { Status = unchanged; return; }
        if (Origin == DocumentOrigin.Welcome) Origin = DocumentOrigin.Imported;
        undoHistory.Add(MarkdownText);
        if (undoHistory.Count > UndoLimit) undoHistory.RemoveAt(0);
        redoHistory.Clear();
        MarkdownText = text;
        NotifyHistory();
        await RenderAsync();
        Status = changed;
    }

    private async Task LoadAsync(string text, DocumentOrigin origin, string? filePath, string? sourcePath, string? saved, string encoding)
    {
        savedText = saved;
        EncodingLabel = encoding;
        undoHistory.Clear();
        redoHistory.Clear();
        NotifyHistory();
        Origin = origin;
        FilePath = filePath;
        SourcePath = sourcePath;
        MarkdownText = text;
        UpdateDirty();
        IsRawView = false;
        await RenderAsync();
    }

    private async Task RenderAsync()
    {
        var generation = ++renderGeneration;
        var text = MarkdownText;
        try
        {
            var document = await Task.Run(() => MarkdownEngine.Parse(text));
            if (generation == renderGeneration) Document = document;
        }
        catch (NativeCoreException ex)
        {
            if (generation != renderGeneration) return;
            Document = null;
            IsRawView = true;
            Status = ex.Message + " Showing the raw Markdown.";
        }
    }

    private async Task RunAsync(string action, Func<Task> work)
    {
        IsBusy = true;
        try { await work(); }
        catch (OperationCanceledException) { Status = $"{action} cancelled."; }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            AppLog.Write($"{action} failed", ex);
            Status = $"{action} failed.";
            await dialogs.ShowErrorAsync($"{action} failed", ex.Message);
        }
        finally { IsBusy = false; }
    }

    public const string WelcomeText = """
        # Welcome to md-viewer

        A fast, native Markdown reader for Windows. Open a file with **Open** (Ctrl+O), drop one onto the window, or use **Open with** from File Explorer.

        ## Reading

        - The **outline** on the left lists every heading. Click one to jump to it.
        - **Raw** (Ctrl+R) switches between the rendered page and the Markdown source.
        - Zoom with Ctrl+Plus, Ctrl+Minus, and Ctrl+0, or Ctrl+mouse wheel.
        - Links to other Markdown files open here; web links open in your browser.

        ## Converting

        | Command | What it does |
        |---|---|
        | Open | Also imports `.docx`, `.html`, `.epub`, `.odt`, `.rtf` (through Pandoc) and `.pdf` (built in, with Windows OCR for scanned pages) |
        | Export | Writes Word, HTML, EPUB, RTF, ODT, LaTeX, Typst, reStructuredText, or Org through Pandoc |
        | Crawl | Collects a documentation site into one Markdown document, politely and within the start page's folder |
        | Format | Normalizes Markdown through Pandoc: ATX headings, pipe tables, no hard wrapping |
        | Reflow | Repairs skipped heading levels so the outline nests cleanly |

        Pandoc is needed for Word, HTML, EPUB, export, crawl, and format. If it is not installed, choose **Fetch Pandoc** in the ⋯ menu.

        Save (Ctrl+S) keeps converted or edited documents as Markdown.
        """;
}
