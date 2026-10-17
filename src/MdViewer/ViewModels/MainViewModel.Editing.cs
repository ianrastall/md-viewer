using CommunityToolkit.Mvvm.Input;

namespace MdViewer.ViewModels;

public partial class MainViewModel
{
    private readonly SemaphoreSlim parseGate = new(1, 1);
    private CancellationTokenSource? sourceRefresh;
    private string sourceLineEnding = Environment.NewLine;
    private bool sourceNeedsRefresh, sourceEditGroup;
    private long lastSourceEdit;

    public bool IsSourceReadOnly => IsBusy || Origin == DocumentOrigin.Welcome;

    // WinUI's TextBox normalizes its line breaks to CR. Keep that representation
    // at the control boundary so merely displaying an LF/CRLF file never edits it.
    public string SourceText => NormalizeLineEndings(MarkdownText, "\r");

    [RelayCommand]
    private Task NewAsync() => RunAsync("New document", async () =>
    {
        if (!await CanLeaveCoreAsync()) return;
        await LoadAsync("", DocumentOrigin.Untitled, null, null, "", "UTF-8");
        IsRawView = true;
        Status = "New document. Write Markdown, then Save to choose a file.";
    });

    /// <summary>Accepts source-control text; dirty state and history update immediately.</summary>
    public void EditSource(string source)
    {
        if (IsSourceReadOnly) return;
        var text = NormalizeLineEndings(source, sourceLineEnding);
        if (text == MarkdownText) return;
        var now = System.Diagnostics.Stopwatch.GetTimestamp();
        if (!sourceEditGroup || System.Diagnostics.Stopwatch.GetElapsedTime(lastSourceEdit, now) > TimeSpan.FromSeconds(1))
            PushUndo();
        else redoHistory.Clear();
        sourceEditGroup = true;
        lastSourceEdit = now;
        MarkdownText = text;
        NotifyHistory();
        Status = "Editing Markdown.";
        sourceNeedsRefresh = true;
        CancelSourceRefresh();
        var refresh = new CancellationTokenSource();
        sourceRefresh = refresh;
        _ = RefreshSourceAfterDelayAsync(refresh);
    }

    /// <summary>Settles a pending preview before a command or switching to rendered view.</summary>
    public async Task FlushSourceAsync()
    {
        if (!sourceNeedsRefresh) return;
        CancelSourceRefresh();
        await RenderAsync();
    }

    private async Task RefreshSourceAfterDelayAsync(CancellationTokenSource refresh)
    {
        try
        {
            await Task.Delay(200, refresh.Token);
            await RenderAsync(refresh.Token);
        }
        catch (OperationCanceledException) { }
        finally
        {
            if (ReferenceEquals(sourceRefresh, refresh)) sourceRefresh = null;
            refresh.Dispose();
        }
    }

    private void CancelSourceRefresh()
    {
        sourceRefresh?.Cancel();
        sourceRefresh = null;
        ++renderGeneration;
    }

    private void EndSourceEdit() => sourceEditGroup = false;

    private void PushUndo()
    {
        undoHistory.Add(MarkdownText);
        if (undoHistory.Count > UndoLimit) undoHistory.RemoveAt(0);
        redoHistory.Clear();
    }

    private static string NormalizeLineEndings(string text, string newline) =>
        text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Replace("\n", newline, StringComparison.Ordinal);

    private static string DetectLineEnding(string text)
    {
        var first = text.IndexOfAny(['\r', '\n']);
        if (first < 0) return Environment.NewLine;
        return text[first] == '\n' ? "\n" : first + 1 < text.Length && text[first + 1] == '\n' ? "\r\n" : "\r";
    }
}
