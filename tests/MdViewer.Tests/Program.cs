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
    AppLog.Directory = Path.Combine(scratch.FullName, "logs");
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

    // Undo and redo step through Format/Reflow changes; dirty state follows the saved text.
    var reflowed = vm.MarkdownText;
    Check("undo available", vm.CanUndo && !vm.CanRedo && vm.UndoCommand.CanExecute(null));
    await vm.UndoCommand.ExecuteAsync(null);
    Check("undo", vm.MarkdownText == "# Notes\n\n### Skipped\n\ntext\n\n## Child\n" && !vm.IsDirty && !vm.CanUndo && vm.CanRedo && vm.Headings[0].Children[0].Title == "Skipped");
    await vm.RedoCommand.ExecuteAsync(null);
    Check("redo", vm.MarkdownText == reflowed && vm.IsDirty && vm.CanUndo && !vm.CanRedo);

    // Closing asks first; Cancel keeps everything.
    dialogs.Unsaved = UnsavedChoice.Cancel;
    await vm.CloseDocumentCommand.ExecuteAsync(null);
    Check("cancel close", vm is { Origin: DocumentOrigin.Markdown, IsDirty: true } && dialogs.UnsavedPrompts == 1);

    // Save writes in place.
    await vm.SaveCommand.ExecuteAsync(null);
    Check("save", !vm.IsDirty && File.ReadAllText(file) == vm.MarkdownText && dialogs.SavePrompts == 0);
    await vm.UndoCommand.ExecuteAsync(null);
    Check("undo after save marks the document dirty again", vm.IsDirty && vm.CanRedo);
    await vm.RedoCommand.ExecuteAsync(null);
    Check("redo back to the saved text", !vm.IsDirty);

    // Opening another file while dirty: Discard proceeds without saving.
    vm.ZoomInCommand.Execute(null);
    var other = Path.Combine(scratch.FullName, "other.markdown");
    await File.WriteAllTextAsync(other, "Plain paragraph.");
    await vm.ReflowCommand.ExecuteAsync(null); // no headings: no change, stays clean
    Check("no-heading reflow on clean doc", !vm.IsDirty);
    await vm.OpenFileAsync(other);
    Check("opening clears history", !vm.CanUndo && !vm.CanRedo);
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

    // Tools: installed status without network, OCR preferences persisted, and the Tools command.
    var tools = new ToolsViewModel(vm.Settings);
    await tools.RefreshAsync(checkLatest: false);
    Check("tools status", tools.Status == "" && tools.Pandoc.Name == "Pandoc" && tools.Tesseract.Name == "Tesseract OCR" && tools.WindowsOcr.Name == "Windows OCR" &&
                          tools.Pandoc.LatestText == "Not checked for updates yet." && tools.Pandoc.HasAction && !tools.WindowsOcr.HasAction, tools.Status);
    Check("pandoc card", tools.Pandoc.Installed == HasPandoc() && (tools.Pandoc.Installed ? tools.Pandoc.StatusText.StartsWith("Installed", StringComparison.Ordinal) : tools.Pandoc.NeedsAttention));
    tools.EngineIndex = 1;
    tools.TesseractLanguages = "eng deu";
    var reloaded = ViewerSettings.Load(settingsPath);
    Check("ocr preferences saved", reloaded is { OcrEngine: "windows", TesseractLanguages: "eng+deu" } && tools.ActiveEngineText == "Scanned pages are read with Windows OCR.");
    tools.EngineIndex = 0;
    tools.TesseractLanguages = "eng";
    vm.OpenToolsCommand.Execute(null);
    Check("tools command", dialogs.ToolsShown == 1);
    vm.Settings.CheckToolsAutomatically = false;
    await vm.CheckToolsInBackgroundAsync();
    Check("background check can be turned off", vm.Settings.LastToolCheck is null && !vm.HasUpdateNotice);

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

    // Hold a real Save As workflow at its picker: other document commands must
    // not change the source that will be saved, and closing must wait for it.
    var gatedDialogs = new FakeDialogs();
    var gated = new MainViewModel(gatedDialogs, ViewerSettings.Load(Path.Combine(scratch.FullName, "gated-settings.json")));
    await gated.OpenFileAsync(file);
    await File.WriteAllTextAsync(file, "# Start\n\n### Child\n");
    await gated.OpenFileAsync(file);
    await gated.ReflowCommand.ExecuteAsync(null);
    var pendingSave = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
    gatedDialogs.PendingSave = pendingSave.Task;
    var saving = gated.SaveAsCommand.ExecuteAsync(null);
    Check("picker holds busy state", gated.IsBusy && !gated.IsIdle && !gated.CanUndo && !gated.UndoCommand.CanExecute(null));
    var textBefore = gated.MarkdownText;
    await gated.OpenFileAsync(other);
    await gated.CloseDocumentCommand.ExecuteAsync(null);
    await gated.UndoCommand.ExecuteAsync(null);
    Check("document commands cannot overlap a picker", gated.MarkdownText == textBefore && gated.FilePath == file && gatedDialogs.UnsavedPrompts == 0);
    var leaving = gated.CanLeaveAsync();
    Check("window close waits for pending save", !leaving.IsCompleted && gatedDialogs.UnsavedPrompts == 0);
    var gatedCopy = Path.Combine(scratch.FullName, "gated-copy.md");
    pendingSave.SetResult(gatedCopy);
    await saving;
    Check("close after save sees saved state", await leaving && !gated.IsBusy && !gated.IsDirty && gated.FilePath == gatedCopy && File.ReadAllText(gatedCopy) == textBefore && gatedDialogs.UnsavedPrompts == 0);
    Check("history commands reenabled after save", gated.CanUndo && gated.UndoCommand.CanExecute(null));

    // Cancelled Save As leaves the dirty document intact; closing asks afterwards.
    await gated.UndoCommand.ExecuteAsync(null);
    pendingSave = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
    gatedDialogs.PendingSave = pendingSave.Task;
    saving = gated.SaveAsCommand.ExecuteAsync(null);
    leaving = gated.CanLeaveAsync();
    pendingSave.SetResult(null);
    await saving;
    Check("cancelled picker still checks unsaved work", !await leaving && gated.IsDirty && !gated.IsBusy && gatedDialogs.UnsavedPrompts == 1);

    // Errors from pickers and path normalization are handled like native errors.
    gatedDialogs.Unsaved = UnsavedChoice.Discard;
    await gated.OpenFileAsync("\0");
    Check("invalid open path is handled", gatedDialogs.Errors.Count == 1 && !gated.IsBusy && gated.FilePath == gatedCopy);
    gatedDialogs.PendingSave = Task.FromException<string?>(new IOException("Picker unavailable"));
    await gated.SaveAsCommand.ExecuteAsync(null);
    Check("picker error releases operation", gatedDialogs.Errors.Count == 2 && gatedDialogs.Errors[^1].Contains("Picker unavailable") && !gated.IsBusy && gated.IsDirty);

    // A failed save during close must never authorize discarding the document.
    gatedDialogs.Unsaved = UnsavedChoice.Save;
    using (var locked = new FileStream(gatedCopy, FileMode.Open, FileAccess.Read, FileShare.None))
        Check("failed save keeps window open", !await gated.CanLeaveAsync() && gated.IsDirty && !gated.IsBusy && gatedDialogs.Errors.Count == 3);
    Check("failed save leaves original intact", File.ReadAllText(gatedCopy) == textBefore && !Directory.EnumerateFiles(scratch.FullName, ".*.tmp").Any());

    // New documents start with a clean, editable source and acquire a save path later.
    var editorDialogs = new FakeDialogs();
    var editor = new MainViewModel(editorDialogs, ViewerSettings.Load(Path.Combine(scratch.FullName, "editor-settings.json")));
    await editor.NewCommand.ExecuteAsync(null);
    Check("new blank document", editor is { Origin: DocumentOrigin.Untitled, DocumentTitle: "Untitled", MarkdownText: "", FilePath: null, SourcePath: null, IsDirty: false, IsRawView: true, IsSourceReadOnly: false, EncodingLabel: "UTF-8" } && editor.Headings.Count == 0 && !editor.CanUndo && editor.DocumentState == "Not saved yet");
    editor.EditSource("# Draft\r\r### Child\r");
    editor.EditSource("# Draft\r\r### Child\r\rWords here.\r");
    Check("typing updates dirty state immediately", editor.IsDirty && editor.CanUndo && editor.WindowTitle == "● Untitled — md-viewer");
    await editor.FlushSourceAsync();
    Check("typing refreshes outline and statistics", editor.Headings.Count == 1 && editor.Headings[0].Title == "Draft" && editor.Headings[0].Children[0].Title == "Child" && editor.Document?.WordCount == 4, editor.StatisticsText);
    var typed = editor.MarkdownText;
    Check("new text uses platform line endings", typed == "# Draft" + Environment.NewLine + Environment.NewLine + "### Child" + Environment.NewLine + Environment.NewLine + "Words here." + Environment.NewLine);
    await editor.UndoCommand.ExecuteAsync(null);
    Check("typing coalesces into one undo group", editor.MarkdownText == "" && !editor.IsDirty && editor.CanRedo && !editor.CanUndo);
    await editor.RedoCommand.ExecuteAsync(null);
    Check("redo restores typed source and outline", editor.MarkdownText == typed && editor.IsDirty && editor.Headings[0].Title == "Draft");

    await editor.NewCommand.ExecuteAsync(null);
    Check("new respects cancel unsaved", editor.MarkdownText == typed && editor.IsDirty && editorDialogs.UnsavedPrompts == 1);
    editorDialogs.Unsaved = UnsavedChoice.Save;
    await editor.NewCommand.ExecuteAsync(null);
    Check("new respects cancelled save picker", editor.MarkdownText == typed && editor.IsDirty && editorDialogs.SavePrompts == 1);
    var draft = Path.Combine(scratch.FullName, "draft.md");
    editorDialogs.SavePath = draft;
    await editor.NewCommand.ExecuteAsync(null);
    Check("new saves previous document when requested", File.ReadAllText(draft) == typed && editor is { Origin: DocumentOrigin.Untitled, IsDirty: false, MarkdownText: "", IsRawView: true } && !editor.CanUndo && !editor.CanRedo);
    var prompts = editorDialogs.UnsavedPrompts;
    await editor.NewCommand.ExecuteAsync(null);
    Check("empty new document needs no discard prompt", editorDialogs.UnsavedPrompts == prompts);

    editor.EditSource("# Saved draft\r");
    await editor.SaveCommand.ExecuteAsync(null);
    Check("save new document chooses Markdown path", editor is { Origin: DocumentOrigin.Markdown, IsDirty: false } && editor.FilePath == draft && File.ReadAllText(draft) == editor.MarkdownText);
    await editor.UndoCommand.ExecuteAsync(null);
    Check("undo typing after save marks dirty", editor.MarkdownText == "" && editor.IsDirty);
    await editor.RedoCommand.ExecuteAsync(null);
    Check("redo typing to saved text becomes clean", !editor.IsDirty);

    // Viewing source is not an edit. Actual editing preserves the file's newline convention.
    foreach (var newline in new[] { "\n", "\r\n", "\r" })
    {
        var lineFile = Path.Combine(scratch.FullName, "line-endings.md");
        var original = "# Lines" + newline + newline + "Text" + newline;
        await File.WriteAllTextAsync(lineFile, original);
        await editor.OpenFileAsync(lineFile);
        editor.IsRawView = true;
        editor.EditSource(editor.SourceText);
        Check("source display preserves clean state " + newline.Length + " " + (int)newline[0], !editor.IsDirty && editor.MarkdownText == original && !editor.CanUndo);
        editor.EditSource(editor.SourceText + "More\r");
        await editor.SaveCommand.ExecuteAsync(null);
        Check("source editing preserves line endings " + newline.Length + " " + (int)newline[0], File.ReadAllText(lineFile) == original + "More" + newline);
    }

    // Debounced old edits cannot replace a newly opened document's model.
    for (var i = 0; i < 25; i++) editor.EditSource("# Revision " + i);
    await editor.FlushSourceAsync();
    Check("preview shows latest rapid edit", editor.Headings.Count == 1 && editor.Headings[0].Title == "Revision 24");
    editorDialogs.Unsaved = UnsavedChoice.Discard;
    editor.EditSource("# Obsolete preview");
    await editor.OpenFileAsync(other);
    await Task.Delay(300);
    Check("old preview cannot overwrite opened document", editor.FilePath == other && editor.MarkdownText == "Plain paragraph." && editor.Document?.Headings.Count == 0);
    await editor.ShowWelcomeAsync();
    editor.EditSource("Replace welcome");
    Check("welcome remains read only", editor.IsSourceReadOnly && editor.MarkdownText == MainViewModel.WelcomeText && !editor.IsDirty);
    Check("editing has no unexpected errors", editorDialogs.Errors.Count == 0, string.Join(" | ", editorDialogs.Errors));
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
    public Task<string?>? PendingSave { get; set; }
    public List<string> Errors { get; } = [];

    public Task<string?> PickOpenPathAsync() => Task.FromResult<string?>(null);
    public Task<string?> PickSavePathAsync(string suggestedName) { SavePrompts++; return PendingSave ?? Task.FromResult(SavePath); }
    public Task<string?> PickExportPathAsync(string suggestedName) => Task.FromResult(ExportPath);
    public Task<string?> PromptUrlAsync() => Task.FromResult(Url);
    public Task<UnsavedChoice> ConfirmUnsavedAsync(string documentName) { UnsavedPrompts++; return Task.FromResult(Unsaved); }
    public Task ShowErrorAsync(string title, string message) { Errors.Add($"{title}: {message}"); return Task.CompletedTask; }
    public void CloseWindow() { }
    public int ToolsShown { get; private set; }
    public void ShowTools() => ToolsShown++;
}
