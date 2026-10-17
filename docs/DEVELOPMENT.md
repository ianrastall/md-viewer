# Development status

This report is updated at development milestones. The README describes the user-facing application; this file records how the implementation fits together, what was verified, and the next work. Historical remaining-work lists describe the state at that milestone; later milestones supersede them.

## Architecture

The application is a Windows x64 desktop program with a WinUI 3 window. `App.xaml.cs` initializes the native core and handles launch/file activation. `MainWindow.xaml` supplies the toolbar, heading outline, reading surface, source view, status bar, and operation overlays. `MainWindow.xaml.cs` implements the dialogs, file pickers, links, window lifetime, and platform interactions.

`MainViewModel` owns the current Markdown text, parsed document, source and save paths, encoding, dirty state, history, zoom, and operation state. Commands coordinate these fields and call the core through `MdViewer.Interop`. `IViewerDialogs` isolates user choices from document workflows, so the same view model runs in the console verification harness without loading WinUI.

`MdViewer.Interop` marshals UTF-8 strings and paths through the versioned C ABI in `native/include/mdv_native.h`. Native result buffers belong to C++ and are always freed by the interop layer. Long native calls execute on worker threads with progress callbacks and cancellation tokens; exceptions become managed errors at the boundary. The C++ modules implement Markdown parsing/reflow, file decoding and atomic writes, Pandoc subprocesses, PDFium extraction and OCR, HTTP/crawling, and external-tool discovery/downloads.

Markdown parsing produces a binary tree/event model plus heading metadata and statistics. C# reconstructs that model. `MarkdownView` virtualizes top-level blocks through `ItemsRepeater`; `MarkdownRenderer` builds native WinUI paragraphs, lists, tables, links, code, and images as blocks appear. Heading metadata drives the outline and navigation. The source view uses a separate text control.

`ViewerSettings` persists reading and OCR preferences. `ToolsViewModel` coordinates native status checks, downloads, and OCR configuration. Pandoc and Tesseract remain optional; basic Markdown reading and writing work without them. PDFs use built-in text extraction and can fall back to Windows OCR.

The build script compiles the C++ DLL and native harness, then the managed application and optional view-model harness. Release packaging publishes the runtimes with the app, compiles a per-user Inno Setup installer, and bundles installation notes and license notices. MSIX packaging is a separate local-development path. Signing uses an existing suitable certificate when one is available.

## Milestone 1 — document-operation safety

### Behavior

Document commands now reserve the busy state before their first asynchronous dialog or file operation and keep it until completion, cancellation, or error. A pending picker no longer allows Open, Close Document, Save, or Undo to change the document underneath the pending action. Undo and Redo are disabled while another action owns the document and reserve the same busy state while restoring and parsing history.

Both window-close paths wait for the current action. They request native cancellation when the action supports it, but wait for non-cancellable saves to finish. Only then do they check unsaved work. Failed saves refuse to close. The Tools window closes only when the main window actually closes.

The build script detects a CMake cache created by a different Visual Studio generator or installation and uses CMake's `--fresh` configuration. This repaired the existing local cache after the machine's available build tools changed from Visual Studio 2026 to 2022.

### Coordination

`RunAsync` owns command serialization, error reporting, and an idle-completion signal. Public document commands enter through it; internal save/open/leave helpers compose within the already-reserved action. `CanLeaveAsync`, used by window shutdown, waits for idle before entering a final unsaved-work check. This prevents a nested save from being rejected merely because its parent Open or Close command already owns the busy state.

### Verification

The native harness passes all 90 checks, including real PDF/OCR and Pandoc paths available on this machine. The view-model harness passes 52 checks, including 10 additional checks for pending Save As, rejected overlapping commands, close waiting, cancelled pickers, invalid paths, picker errors, failed save on close, and preservation/cleanup after a failed atomic write. `scripts/build.ps1 -Test` also builds the full WinUI application with zero warnings and zero errors.

### Remaining work

1. Implement the requested New Document command (Ctrl+N), with blank editable Markdown, the existing unsaved-work choices, preview, saving, and editing history.
2. Make source editing interoperate with parsing, statistics, outline navigation, Format/Reflow, and Undo/Redo without parsing every keystroke or retaining unbounded history.
3. Validate the self-contained installer and publish the next release; GitHub's older 1.x releases predate this rewrite.
4. Inspect relative-resource refresh after Save As, source/preview position handling, and the absence of in-document search.
5. Add reproducible continuous verification so future pushes receive native and managed checks outside this workstation.

### Known limits

The harness validates the real view model and C ABI, but it does not automate native WinUI layout or dialogs. Network-dependent checks are opt-in. Code signing still depends on an available code-signing certificate. Text selection in the rendered view remains block-local, and the renderer deliberately leaves unsupported Markdown extensions as source.

## Milestone 2 — new documents and source editing

### Behavior

New (Ctrl+N) opens a blank UTF-8 document in Source mode with the title Untitled. An untouched blank document is clean, so closing it does not demand a save. Typing immediately updates the unsaved indicator. Save asks for a Markdown path, then converts the untitled document into a normal saved document. New uses the same Save/Discard/Cancel workflow as Open and Close.

Source (Ctrl+R), replacing the old read-only Raw view, edits new, opened, imported, and crawled Markdown. The welcome page stays read-only. Adjacent keystrokes form an undo group, with a new group after a one-second pause or a document command. Typing, Format, and Reflow share the existing 100-step history, including after saving. Keyboard shortcuts and the source context menu use that same history; the text control's separate undo stack is cleared.

The WinUI TextBox stores line breaks as CR. The view model exposes a normalized control representation and translates actual edits back to the document's original LF, CRLF, or CR convention. Merely displaying source does not change the document or its dirty state. Mixed conventions normalize to the first encountered convention when the user actually edits. New documents use platform line endings.

### Coordination and performance

`MainViewModel.Editing.cs` owns source-edit grouping and preview scheduling. Text and dirty state update synchronously, while parsing waits for a 200 ms typing pause. Cancellation discards superseded scheduled work. A generation counter rejects obsolete parse results, and a semaphore permits only one parser at a time. Commands flush pending parsing before operating, so Format, Reflow, outline metadata, and save prompts all see current text. No C ABI changes were needed: editing feeds the same parser and writer as existing documents.

The window synchronizes source programmatically under a guard, avoiding feedback loops and preserving the caret during ordinary typing. Switching back to preview retains the previous reading position. Changes to the base directory rebuild relative images after Save As. UI inspection also found that the Tools notice obscured the first line; the notice now has its own layout row and collapses fully when absent. Short documents exposed a separate layout problem: the reading column now uses an explicit width derived from the viewport and zoom, updated on resize, and the preview realizes content after becoming visible. Hidden previews do not build WinUI blocks on every source refresh.

### Verification

The view-model harness passes 75 checks: the preceding 52 plus 23 checks covering blank documents, typing/preview, history, New's unsaved choices, saving, newline preservation, rapid edits, stale parse rejection, and welcome-page protection. Native checks still cover the unchanged C++ core. Desktop smoke testing exercised the actual toolbar, editable TextBox, rendered headings and bold text, outline/statistics refresh, Ctrl+R, and Ctrl+Z/Ctrl+Y. The real Save As dialog opened and held the busy state; the UI automation helper could not submit its out-of-process PickerHost window, so completed disk saves are covered by the view-model/C ABI harness rather than claimed as desktop automation coverage.

### Remaining work

1. Finish installer/package verification and publish 2.2.0, with accurate signing and runtime requirements.
2. Add Windows CI for build/native/view-model checks and release artifacts.
3. Add in-document search suitable for both editable source and virtualized preview.
4. Bound editing history by memory as well as step count for very large documents, and preserve caret/scroll positions more precisely across preview and history changes.
5. Consider crash recovery for unsaved drafts and external-file-change detection before expanding into richer editing features.

### Known limits

Source uses a standard WinUI TextBox, without syntax highlighting or a split preview. Full Markdown strings are retained for undo snapshots; the step limit bounds count but not bytes. Parsing is serialized and superseded results are ignored, but a parse already running in C++ cannot be interrupted. Saving to a different folder resolves relative paths from that new folder and does not copy assets. There is no autosave or recovery yet.
