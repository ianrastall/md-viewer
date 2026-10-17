using MdViewer.Interop;
using MdViewer.Interop.Markdown;
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

    // History bounds cover both directions and keep the nearest reverse step.
    // Small budgets exercise eviction without allocating large test documents.
    var bounded = new DocumentHistory(maxSteps: 3, maxCharacters: 10);
    Check("history starts empty", bounded.Count == 0 && bounded.CharacterCount == 0 && !bounded.CanUndo && !bounded.CanRedo);
    bounded.Record("aa");
    bounded.Record("bbb");
    bounded.Record("cccc");
    Check("history tracks snapshot payload", bounded.Count == 3 && bounded.CharacterCount == 9);
    bounded.Record("dd");
    Check("history evicts oldest step by count", bounded.Count == 3 && bounded.CharacterCount == 9);
    Check("undo preserves nearest state under memory pressure", bounded.Undo("eeeee") == "dd" && bounded.Count == 2 && bounded.CharacterCount == 9 && bounded.CanUndo && bounded.CanRedo);
    Check("redo accounts for removed and added snapshots", bounded.Redo("dd") == "eeeee" && bounded.Count == 2 && bounded.CharacterCount == 6 && bounded.CanUndo && !bounded.CanRedo);
    bounded.Record("abcdefghijklmnop");
    Check("single oversized history step stays reversible", bounded.Count == 1 && bounded.CharacterCount == 16 && bounded.CanUndo);
    Check("oversized undo retains redo", bounded.Undo("qrstuvwxyz0123456") == "abcdefghijklmnop" && bounded.Count == 1 && bounded.CharacterCount == 17 && !bounded.CanUndo && bounded.CanRedo);
    Check("oversized redo retains undo", bounded.Redo("abcdefghijklmnop") == "qrstuvwxyz0123456" && bounded.Count == 1 && bounded.CharacterCount == 16 && bounded.CanUndo && !bounded.CanRedo);
    bounded.Record("x");
    Check("history returns below budget after oversized step", bounded.Count == 1 && bounded.CharacterCount == 1);
    bounded.Clear();
    Check("clearing history resets accounting", bounded.Count == 0 && bounded.CharacterCount == 0 && !bounded.CanUndo && !bounded.CanRedo);
    bounded.Record("a");
    Check("branch setup has redo", bounded.Undo("b") == "a" && bounded.CanRedo);
    bounded.Record("a");
    Check("new edit clears redo and its memory", !bounded.CanRedo && bounded.Count == 1 && bounded.CharacterCount == 1 && bounded.Undo("c") == "a");

    var stepsOnly = new DocumentHistory(maxSteps: 3, maxCharacters: 100);
    for (var i = 0; i < 6; i++) stepsOnly.Record(i.ToString());
    Check("history count limit keeps nearest undo steps", stepsOnly.Count == 3 && stepsOnly.Undo("6") == "5" && stepsOnly.Undo("5") == "4" && stepsOnly.Undo("4") == "3" && !stepsOnly.CanUndo && stepsOnly.Count == 3);
    Check("history count limit survives undo/redo transfers", stepsOnly.Redo("3") == "4" && stepsOnly.Redo("4") == "5" && stepsOnly.Redo("5") == "6" && !stepsOnly.CanRedo && stepsOnly.Count == 3 && stepsOnly.CharacterCount == 3);

    var sourceSearch = DocumentSearch.Source("One one\rA+B 😀 needle");
    sourceSearch.SetQuery("one", matchCase: false);
    Check("find source is case insensitive by default", sourceSearch.Count == 2 && sourceSearch.Ordinal == 1 && sourceSearch.Match is { Start: 0, Length: 3, BlockIndex: -1 });
    Check("find next advances", sourceSearch.Move(false) is { Start: 4 } && sourceSearch.Ordinal == 2);
    Check("find next wraps", sourceSearch.Move(false) is { Start: 0 } && sourceSearch.Ordinal == 1);
    Check("find previous wraps", sourceSearch.Move(true) is { Start: 4 } && sourceSearch.Ordinal == 2);
    sourceSearch.SetQuery("One", matchCase: true);
    Check("find case option", sourceSearch.Count == 1 && sourceSearch.Match is { Start: 0 } && sourceSearch.Move(true) is { Start: 0 });
    sourceSearch.SetQuery("A+B", matchCase: false);
    Check("find query is literal", sourceSearch.Count == 1 && sourceSearch.Match is { Start: 8, Length: 3 });
    sourceSearch.SetQuery("needle", matchCase: false);
    Check("find offsets are UTF16", sourceSearch.Match is { Start: 15, Length: 6 });
    sourceSearch.SetQuery("", matchCase: false);
    Check("empty find query is safe", sourceSearch.Count == 0 && sourceSearch.Match is null && sourceSearch.Move(false) is null);

    var overlapping = DocumentSearch.Source("aaaa");
    overlapping.SetQuery("aa", false);
    Check("find includes overlapping occurrences", overlapping.Count == 3 && overlapping.Move(false) is { Start: 1 } && overlapping.Move(false) is { Start: 2 } && overlapping.Move(false) is { Start: 0 } && overlapping.Move(true) is { Start: 2 });
    var abundantText = new string('a', DocumentSearch.CountLimit + 20) + " tail a";
    var abundant = DocumentSearch.Source(abundantText);
    abundant.SetQuery("a", false);
    Check("find count is bounded", abundant.Count == DocumentSearch.CountLimit && abundant.CountLimited);
    Check("find can navigate beyond the count limit", abundant.Move(true)?.Start == abundantText.Length - 1 && abundant.Move(false) is { Start: 0 });
    abundant.SetQuery("tail", false);
    Check("find resets limited count for new query", abundant.Count == 1 && !abundant.CountLimited);

    const string searchMarkdown = """
        # Title {#hidden-id}

        A **joined** phrase and [label](https://example.test) plus ![hidden alt](missing.png).

        <!-- secret -->

        > Quote **joined** phrase.
        >
        > `code needle`

        | Column |
        | --- |
        | table needle |

        ```csharp
        needle in code
        ```

        ![picture alt](missing.png)
        """;
    var searchDocument = MarkdownEngine.Parse(searchMarkdown);
    var previewSearch = DocumentSearch.Preview(searchDocument);
    previewSearch.SetQuery("joined phrase", false);
    Check("preview find crosses inline formatting", previewSearch.Count == 2 && previewSearch.Match is { BlockIndex: 1, TextIndex: 0 });
    var rawSearch = DocumentSearch.Source(searchMarkdown);
    rawSearch.SetQuery("joined phrase", false);
    Check("source find keeps Markdown syntax", rawSearch.Count == 0);
    previewSearch.SetQuery("secret", false);
    rawSearch.SetQuery("secret", false);
    Check("preview excludes hidden comments", previewSearch.Count == 0 && rawSearch.Count == 1);
    previewSearch.SetQuery("example.test", false);
    Check("preview excludes hidden link destinations", previewSearch.Count == 0);
    previewSearch.SetQuery("hidden-id", false);
    Check("preview excludes stripped heading attributes", previewSearch.Count == 0);
    previewSearch.SetQuery("hidden alt", false);
    Check("preview excludes inline image alternatives", previewSearch.Count == 0);
    previewSearch.SetQuery("picture alt", false);
    Check("preview excludes standalone image alternatives", previewSearch.Count == 0);
    previewSearch.SetQuery("csharp", false);
    Check("preview excludes generated code labels", previewSearch.Count == 0);
    previewSearch.SetQuery("needle", false);
    Check("preview indexes nested text and literal code", previewSearch.Count == 3 && previewSearch.Match is { TextIndex: 1 } q && searchDocument.Blocks[q.BlockIndex].Kind == BlockKind.Quote);
    var tableMatch = previewSearch.Move(false);
    Check("preview navigation reaches table cells", tableMatch is not null && searchDocument.Blocks[tableMatch.BlockIndex].Kind == BlockKind.Table);
    var codeMatch = previewSearch.Move(false);
    Check("preview navigation reaches code blocks", codeMatch is not null && searchDocument.Blocks[codeMatch.BlockIndex].Kind == BlockKind.Code && previewSearch.Ordinal == 3);
    Check("preview previous reverses segment navigation", previewSearch.Move(true) == tableMatch);
    previewSearch.SetQuery("needle", false, codeMatch);
    Check("find preserves a retained location when rebuilding", previewSearch.Match == codeMatch && previewSearch.Ordinal == 3);
    var emptySearch = DocumentSearch.Preview(MarkdownEngine.Parse(""));
    emptySearch.SetQuery("needle", false);
    Check("empty preview search is safe", emptySearch.Count == 0 && emptySearch.Move(true) is null);

    var imageInline = new MarkdownInline(InlineKind.Image);
    imageInline.Children.Add(new(InlineKind.Text) { Text = "alt" });
    MarkdownInline[] imageLine = [new(InlineKind.Text) { Text = "before " }, imageInline, new(InlineKind.Text) { Text = " after" }];
    var fallbackProjection = MarkdownDisplay.ProjectInlines(imageLine, null);
    var objectProjection = MarkdownDisplay.ProjectInlines(imageLine, new HashSet<MarkdownInline> { imageInline });
    Check("image projection uses a non-searchable object marker", fallbackProjection.Text == "before \uFFFC after" && objectProjection.Text == fallbackProjection.Text);
    Check("fallback image offsets map to displayed text", fallbackProjection.DisplayOffset(9) == 13 && fallbackProjection.DisplayOffset(14) == 18);
    Check("image objects occupy no highlight text characters", objectProjection.DisplayOffset(9) == 8);
    Check("HTML breaks share display projection", MarkdownDisplay.InlineText([new(InlineKind.Text) { Text = "a" }, new(InlineKind.Html) { Text = "<BR />" }, new(InlineKind.Text) { Text = "b" }]) == "a\rb");
    var deepRoot = new MarkdownBlock(BlockKind.Quote);
    var deepLeaf = deepRoot;
    for (var i = 0; i < 5000; i++) { var child = new MarkdownBlock(BlockKind.Quote); deepLeaf.Blocks.Add(child); deepLeaf = child; }
    var deepParagraph = new MarkdownBlock(BlockKind.Paragraph);
    deepParagraph.Inlines.Add(new(InlineKind.Text) { Text = "deep needle" });
    deepLeaf.Blocks.Add(deepParagraph);
    var deepSearch = DocumentSearch.Preview(new MarkdownDocument { Blocks = [deepRoot], Headings = [], LineCount = 1, WordCount = 2, CharacterCount = 11 });
    deepSearch.SetQuery("needle", false);
    Check("search indexing handles deep block trees iteratively", deepSearch.Count == 1 && deepSearch.Match is { BlockIndex: 0, TextIndex: 0, Start: 5 });

    var manyBlocks = Enumerable.Range(0, 20000).Select(i =>
    {
        var block = new MarkdownBlock(BlockKind.Paragraph);
        block.Inlines.Add(new(InlineKind.Text) { Text = $"Block {i} needle" });
        return block;
    }).ToArray();
    var searchTimer = System.Diagnostics.Stopwatch.StartNew();
    var largeSearch = DocumentSearch.Preview(new MarkdownDocument { Blocks = manyBlocks, Headings = [], LineCount = 40000, WordCount = 60000, CharacterCount = 400000 });
    largeSearch.SetQuery("needle", false);
    Check("large preview navigation reaches matches beyond bounded count", largeSearch.CountLimited && largeSearch.Count == DocumentSearch.CountLimit && largeSearch.Move(true) is { BlockIndex: 19999 } && largeSearch.Move(false) is { BlockIndex: 0 });
    Console.WriteLine($"      indexed and searched 20,000 preview blocks in {searchTimer.ElapsedMilliseconds} ms");

    // Find is a reading operation: it never changes saved text or edit history.
    var findDialogs = new FakeDialogs();
    var finder = new MainViewModel(findDialogs, ViewerSettings.Load(Path.Combine(scratch.FullName, "find-settings.json")));
    await finder.OpenFileAsync(file);
    finder.FindCommand.Execute(null);
    finder.FindQuery = "child";
    Check("find opens in the current view", finder.IsFindOpen && finder.IsRichView && finder.CanFindNavigate && finder.FindPresentation is not null && !finder.IsDirty && !finder.CanUndo);
    var findFocusCount = 0;
    finder.FindFocusRequested += (_, _) => findFocusCount++;
    finder.FindCommand.Execute(null);
    Check("repeated find refocuses without closing", finder.IsFindOpen && findFocusCount == 1);
    finder.IsRawView = true;
    Check("find switches to normalized source offsets", finder.FindMatch is { BlockIndex: -1 } && finder.FindPresentation is null && finder.FindPlaceholder == "Find in Markdown source" && finder.SourceText.Substring(finder.FindMatch.Start, finder.FindMatch.Length).Equals("Child", StringComparison.OrdinalIgnoreCase));
    finder.FindMatchCase = true;
    Check("find match case updates counts", finder.FindSummary == "No matches" && !finder.CanFindNavigate);
    finder.FindQuery = "Child";
    Check("find exact case restores navigation", finder.FindSummary == "1 of 1" && finder.CanFindNavigate);
    finder.EditSource("# One\r\rA **joined** phrase.\r\r<!-- secret -->\r\rAnother one.\r");
    finder.FindMatchCase = false;
    finder.FindQuery = "one";
    Check("find sees edits before preview debounce", finder.FindSummary == "1 of 2" && finder.FindMatch is { Start: 2 });
    finder.FindNextCommand.Execute(null);
    Check("find view model advances", finder.FindSummary == "2 of 2");
    finder.FindNextCommand.Execute(null);
    Check("find view model wraps", finder.FindSummary == "1 of 2");
    finder.FindPreviousCommand.Execute(null);
    Check("find view model previous wraps", finder.FindSummary == "2 of 2");
    var navigations = new List<bool>();
    finder.FindChanged += (_, navigate) => navigations.Add(navigate);
    await finder.FlushSourceAsync();
    Check("background find refresh leaves the editor caret alone", navigations.Count > 0 && !navigations[^1]);
    finder.FindQuery = "joined phrase";
    Check("source query respects markup", finder.FindSummary == "No matches");
    finder.IsRawView = false;
    Check("same query finds visible formatted text", finder.FindSummary == "1 of 1" && finder.FindMatch is { BlockIndex: >= 0 });
    finder.FindQuery = "secret";
    Check("preview view model excludes hidden content", finder.FindSummary == "No matches");
    finder.IsRawView = true;
    Check("source view model includes hidden content", finder.FindSummary == "1 of 1");
    finder.FindQuery = "one";
    finder.EditSource("# Removed\r");
    finder.FindNextCommand.Execute(null);
    Check("find next revalidates pending source changes", finder.FindSummary == "No matches" && finder.FindMatch is null);
    finder.EditSource("# One\r");
    finder.FindQuery = "One";
    var pendingFindSave = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
    findDialogs.PendingSave = pendingFindSave.Task;
    var findSaving = finder.SaveAsCommand.ExecuteAsync(null);
    Check("find navigation disables during document commands", finder.IsBusy && !finder.CanFindNavigate && !finder.FindNextCommand.CanExecute(null));
    pendingFindSave.SetResult(null);
    await findSaving;
    Check("find navigation returns after cancelled save", finder.IsFindOpen && finder.CanFindNavigate && finder.IsDirty && finder.CanUndo);
    finder.CloseFindCommand.Execute(null);
    Check("close find preserves document and history", !finder.IsFindOpen && finder.FindMatch is null && finder.IsDirty && finder.CanUndo && finder.MarkdownText == "# One\n");
    finder.FindCommand.Execute(null);
    findDialogs.Unsaved = UnsavedChoice.Discard;
    await finder.NewCommand.ExecuteAsync(null);
    Check("new document clears find state", !finder.IsFindOpen && !finder.CanFindNavigate && finder.FindMatch is null && !finder.IsDirty);
    Check("find reports no unexpected errors", findDialogs.Errors.Count == 0, string.Join(" | ", findDialogs.Errors));
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
