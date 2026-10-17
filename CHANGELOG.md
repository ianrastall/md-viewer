# Changelog

## 2.2.0

- New (Ctrl+N) opens a blank, editable Markdown document, with Save/Discard/Cancel protection for existing work.
- Source (Ctrl+R) edits new, opened, imported, and crawled Markdown. Preview, heading outline, and statistics refresh after a 200 ms pause in typing.
- Typing, Format, and Reflow share bounded Undo/Redo history. Adjacent keystrokes are grouped; saving retains history.
- Source editing preserves existing LF, CRLF, or CR line endings and does not mark a file dirty simply by displaying it.
- Document commands remain reserved while dialogs are open. Window shutdown waits for pending operations and refuses to close after a failed save.
- Relative resources refresh after Save As, and tool-update notices occupy their own row rather than covering document text.
- Builds recover automatically from a CMake cache tied to another Visual Studio installation.

This is the first published release of the 2.x rewrite: WinUI 3 and a C++ core, a native virtualized reader, PDF extraction/OCR, documentation crawling, Pandoc imports/exports, a Tools window, and a self-contained per-user Windows installer. Previous 1.x releases used the legacy application.
