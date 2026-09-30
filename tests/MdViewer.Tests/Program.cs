using MdViewer.Interop;
using MdViewer.Services;
using MdViewer.ViewModels;

// Drives the real view-model commands with simulated dialogs and isolated settings. Exits nonzero on failure.
var failures = 0;
var checks = 0;
void Check(string name, bool condition, string detail = "")
{
    checks++;
    if (condition) return;
    failures++;
    Console.WriteLine($"FAIL  {name}{(detail.Length > 0 ? " — " + detail : "")}");
}

var scratch = Directory.CreateTempSubdirectory("md-viewer-vm-");
try
{
    var settingsPath = Path.Combine(scratch.FullName, "settings.json");
    Core.Configure(Path.Combine(scratch.FullName, "data"));
    var dialogs = new FakeDialogs();
    var vm = new MainViewModel(dialogs, ViewerSettings.Load(settingsPath));

    await vm.ShowWelcomeAsync();
    Check("welcome", vm is { Origin: DocumentOrigin.Welcome, IsDirty: false, DocumentTitle: "Welcome" } && vm.Headings.Count == 1 && vm.Document is not null);
    Check("welcome window title", vm.WindowTitle == "Welcome — md-viewer");

    // Open, outline, and statistics.
    var file = Path.Combine(scratch.FullName, "notes.md");
    await File.WriteAllTextAsync(file, "# Notes\n\n### Skipped\n\ntext\n\n## Child\n");
    await vm.OpenFileAsync(file);
    Check("open", vm is { Origin: DocumentOrigin.Markdown, IsDirty: false, DocumentTitle: "notes.md" } && vm.FilePath == file);
    Check("outline tree", vm.Headings.Count == 1 && vm.Headings[0].Children.Count == 2 && vm.Headings[0].Children[1].Title == "Child");
    Check("statistics", vm.StatisticsText.StartsWith("8 lines", StringComparison.Ordinal) && vm.StatisticsText.EndsWith("UTF-8", StringComparison.Ordinal), vm.StatisticsText);
    Check("base directory", vm.BaseDirectory == scratch.FullName);

    // Reflow edits in memory and marks the document dirty.
    await vm.ReflowCommand.ExecuteAsync(null);
    Check("reflow", vm.MarkdownText == "# Notes\n\n## Skipped\n\ntext\n\n## Child\n" && vm.IsDirty && vm.Status.StartsWith("Reflow changed 1", StringComparison.Ordinal), vm.Status);
    Check("dirty title", vm.WindowTitle == "● notes.md — md-viewer" && vm.DocumentState == "● Unsaved changes");
    Check("disk untouched before save", File.ReadAllText(file).Contains("### Skipped"));
    await vm.ReflowCommand.ExecuteAsync(null);
    Check("reflow idempotent", vm.Status.StartsWith("Reflow made no heading-level changes", StringComparison.Ordinal), vm.Status);

    // Closing asks first; Cancel keeps everything.
    dialogs.Unsaved = UnsavedChoice.Cancel;
    await vm.CloseDocumentCommand.ExecuteAsync(null);
    Check("cancel close", vm is { Origin: DocumentOrigin.Markdown, IsDirty: true } && dialogs.UnsavedPrompts == 1);

    // Save writes in place.
    await vm.SaveCommand.ExecuteAsync(null);
    Check("save", !vm.IsDirty && File.ReadAllText(file) == vm.MarkdownText && dialogs.SavePrompts == 0);

    // Opening another file while dirty: Discard proceeds without saving.
    vm.ZoomInCommand.Execute(null);
    var other = Path.Combine(scratch.FullName, "other.markdown");
    await File.WriteAllTextAsync(other, "Plain paragraph.");
    await vm.ReflowCommand.ExecuteAsync(null); // no headings: no change, stays clean
    Check("no-heading reflow on clean doc", !vm.IsDirty);
    await vm.OpenFileAsync(other);
    Check("open other", vm.DocumentTitle == "other.markdown" && vm.HasNoOutline && dialogs.UnsavedPrompts == 1);

    // Unsupported files report an error and keep the current document.
    var unsupported = Path.Combine(scratch.FullName, "image.png");
    await File.WriteAllBytesAsync(unsupported, [1, 2, 3]);
    await vm.OpenFileAsync(unsupported);
    Check("unsupported file", dialogs.Errors.Count == 1 && dialogs.Errors[0].Contains(".png") && vm.DocumentTitle == "other.markdown", string.Join(" | ", dialogs.Errors));

    // Missing files likewise.
    await vm.OpenFileAsync(Path.Combine(scratch.FullName, "missing.md"));
    Check("missing file", dialogs.Errors.Count == 2 && !vm.IsBusy);

    // Legacy encodings are preserved on save.
    var legacy = Path.Combine(scratch.FullName, "legacy.md");
    await File.WriteAllBytesAsync(legacy, [(byte)'#', (byte)'#', (byte)' ', (byte)'c', (byte)'a', (byte)'f', 0xE9]);
    await vm.OpenFileAsync(legacy);
    Check("legacy encoding", vm.EncodingLabel == "Windows-1252" && vm.Headings[0].Title == "café");
    await vm.ReflowCommand.ExecuteAsync(null);
    await vm.SaveCommand.ExecuteAsync(null);
    Check("legacy save", File.ReadAllBytes(legacy).SequenceEqual(new byte[] { (byte)'#', (byte)' ', (byte)'c', (byte)'a', (byte)'f', 0xE9 }));

    // Save As goes to a new path and becomes the current file.
    var copy = Path.Combine(scratch.FullName, "copy.md");
    dialogs.SavePath = copy;
    await vm.SaveAsCommand.ExecuteAsync(null);
    Check("save as", vm.FilePath == copy && File.Exists(copy) && dialogs.SavePrompts == 1 && !vm.IsDirty);

    // Raw view, zoom limits, and persisted settings.
    vm.ToggleRawCommand.Execute(null);
    Check("raw toggle", vm is { IsRawView: true, IsRichView: false });
    for (var i = 0; i < 40; i++) vm.ZoomInCommand.Execute(null);
    Check("zoom max", vm.ZoomPercent == MainViewModel.MaxZoom && vm.ZoomText == "250%");
    for (var i = 0; i < 40; i++) vm.ZoomOutCommand.Execute(null);
    Check("zoom min", vm.ZoomPercent == MainViewModel.MinZoom);
    vm.ResetZoomCommand.Execute(null);
    vm.ZoomInCommand.Execute(null);
    Check("zoom persisted", ViewerSettings.Load(settingsPath).ZoomPercent == 110 && new MainViewModel(dialogs, ViewerSettings.Load(settingsPath)).ZoomPercent == 110);
    await vm.OpenFileAsync(file);
    Check("opening resets raw view", vm.IsRichView);

    // Crawl input validation never touches the network for bad URLs.
    dialogs.Url = "not a url";
    await vm.CrawlCommand.ExecuteAsync(null);
    Check("crawl rejects bad url", dialogs.Errors.Count == 3 && vm.Origin == DocumentOrigin.Markdown && !vm.IsCrawling);

    // Pandoc-backed commands, when Pandoc is available.
    if (HasPandoc())
    {
        var html = Path.Combine(scratch.FullName, "page.html");
        await File.WriteAllTextAsync(html, "<h1>Imported</h1><p>From <b>HTML</b>.</p>");
        await vm.OpenFileAsync(html);
        Check("import html", vm is { Origin: DocumentOrigin.Imported, IsDirty: true, FilePath: null } && vm.SourcePath == html && vm.MarkdownText.Contains("# Imported") && vm.DocumentState == "● Not saved yet", vm.MarkdownText);

        var export = Path.Combine(scratch.FullName, "export.docx");
        dialogs.ExportPath = export;
        await vm.ExportCommand.ExecuteAsync(null);
        Check("export docx", File.Exists(export) && new FileInfo(export).Length > 1000);

        dialogs.Unsaved = UnsavedChoice.Discard;
        await vm.OpenFileAsync(file);
        await vm.FormatCommand.ExecuteAsync(null);
        Check("format", vm.Status is "Formatted with Pandoc." or "Pandoc made no changes.", vm.Status);

        // Saving an import asks for a path and writes UTF-8 Markdown.
        await vm.OpenFileAsync(html);
        var saved = Path.Combine(scratch.FullName, "imported.md");
        dialogs.SavePath = saved;
        dialogs.Unsaved = UnsavedChoice.Save;
        await vm.CloseDocumentCommand.ExecuteAsync(null);
        Check("save on close", File.ReadAllText(saved).Contains("# Imported") && vm.Origin == DocumentOrigin.Welcome, vm.Status);
    }
    else Console.WriteLine("      pandoc not found; skipped Pandoc workflows");

    // A cancelled save prompt keeps the document open.
    await vm.OpenFileAsync(file);
    await vm.ReflowCommand.ExecuteAsync(null);
    await File.WriteAllTextAsync(file, "# A\n\n### B\n");
    await vm.OpenFileAsync(file);
    dialogs.Unsaved = UnsavedChoice.Save;
    dialogs.SavePath = null;
    await vm.ReflowCommand.ExecuteAsync(null);
    await vm.CloseDocumentCommand.ExecuteAsync(null);
    Check("save prompt writes in place", vm.Origin == DocumentOrigin.Welcome && File.ReadAllText(file) == "# A\n\n## B\n");
    Check("no stray errors", dialogs.Errors.Count == 3, string.Join(" | ", dialogs.Errors));
}
finally { scratch.Delete(recursive: true); }

Console.WriteLine(failures == 0 ? $"PASS  {checks} view-model checks" : $"{failures} of {checks} view-model checks failed");
return failures == 0 ? 0 : 1;

static bool HasPandoc()
{
    try { Core.PandocPath(); return true; }
    catch (NativeCoreException) { return false; }
}

sealed class FakeDialogs : IViewerDialogs
{
    public UnsavedChoice Unsaved { get; set; } = UnsavedChoice.Cancel;
    public string? SavePath { get; set; }
    public string? ExportPath { get; set; }
    public string? Url { get; set; }
    public int UnsavedPrompts { get; private set; }
    public int SavePrompts { get; private set; }
    public List<string> Errors { get; } = [];

    public Task<string?> PickOpenPathAsync() => Task.FromResult<string?>(null);
    public Task<string?> PickSavePathAsync(string suggestedName) { SavePrompts++; return Task.FromResult(SavePath); }
    public Task<string?> PickExportPathAsync(string suggestedName) => Task.FromResult(ExportPath);
    public Task<string?> PromptUrlAsync() => Task.FromResult(Url);
    public Task<UnsavedChoice> ConfirmUnsavedAsync(string documentName) { UnsavedPrompts++; return Task.FromResult(Unsaved); }
    public Task ShowErrorAsync(string title, string message) { Errors.Add($"{title}: {message}"); return Task.CompletedTask; }
    public void CloseWindow() { }
}
