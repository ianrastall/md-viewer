# md-viewer

A native dark-mode Markdown reader and converter for Windows. It renders Markdown with WinUI controls, builds a heading outline for navigation, imports Word, HTML, EPUB, and PDF documents, exports through Pandoc, and can collect a documentation site into one Markdown file. A green accent palette runs through the interface; theme colors are centralized in `src/MdViewer/App.xaml`.

Built with **WinUI 3, C# 14, .NET 10, CommunityToolkit.Mvvm, and C++20**. The Markdown engine is C++ ([md4c](https://github.com/mity/md4c)) behind a small versioned C ABI; C# provides the window, view models, and services. No browser or WebView is used.

> md-viewer replaces **MDViewer**, the earlier unpackaged version of this app. Its last source is tagged [`v1-legacy`](https://github.com/ianrastall/md-viewer/tree/v1-legacy).

## Run

The installed **md-viewer** entry has a green **M** icon matching Minerva, Lore, Board Meeting, and CTML Workspace. Search for it in Start, right-click it, and choose **Pin to taskbar** (or **More → Pin to taskbar**). md-viewer also appears in File Explorer's **Open with** menu for `.md`, `.markdown`, `.mdown`, and `.mkd` files; it does not change your default app.

To build and install/update the local package:

```powershell
.\scripts\package.ps1 -Install
```

This publishes a self-contained x64 application, validates and creates `artifacts/MdViewer_2.0.0.0_x64.msix`, and registers a separate copy under `%LOCALAPPDATA%\Programs\md-viewer`. The installed app includes .NET and Windows App SDK runtimes and does not depend on the build folder. Local registration requires Windows Developer Mode; the script does not change security settings or install certificates. The MSIX is unsigned and must be signed before distribution to other machines. Close md-viewer before updating. `-Test` runs the verification harnesses before packaging. `-Launch` on `scripts\install.ps1` starts the app afterwards.

Remove the installed package through **Settings → Apps → Installed apps**. Local registration leaves its versioned deployment directories under `%LOCALAPPDATA%\Programs\md-viewer`.

For development, the loose build is at `artifacts/app/MdViewer.exe` (the .NET 10 x64 runtime is required):

```powershell
.\scripts\run.ps1 -File .\README.md
```

## Use

- **Open** (Ctrl+O), drag a file onto the window, or use **Open with**. Markdown and text files open directly, keeping their encoding (UTF-8, UTF-8 with BOM, UTF-16, or Windows-1252).
- **Outline:** every heading, nested by level. Select one to jump to it, in the rendered page or the raw source. Drag the divider to resize the pane.
- **Raw** (Ctrl+R) shows the Markdown source. **Zoom** with Ctrl+Plus, Ctrl+Minus, Ctrl+0, Ctrl+mouse wheel, or the status bar; the zoom level is remembered.
- **Links:** `#anchor` links scroll to the matching heading, links to other Markdown files open in md-viewer, and web and `mailto:` links open in your default apps. Images load from the web or relative to the document.
- **Import:** Open also converts `.docx`, `.html`, `.htm`, `.epub`, `.odt`, and `.rtf` through Pandoc, and `.pdf` with built-in text extraction plus Windows OCR for scanned pages. Imported Markdown includes a hidden diagnostics comment with page, OCR, and warning details.
- **Export** (Ctrl+E): Word, HTML, EPUB, RTF, ODT, LaTeX, Typst, reStructuredText, or Org through Pandoc. Relative images resolve from the document's folder.
- **Crawl:** collects a documentation site into one Markdown document. It stays within the start page's folder, fetches one page at a time at least two seconds apart, honors `robots.txt` and `Crawl-delay`, stops at 250 pages, and fetches Wikipedia and other MediaWiki articles as a single article.
- **Format:** normalizes the Markdown through Pandoc: ATX headings, pipe tables, no hard wrapping, and no Pandoc-only attribute, div, or span syntax. A leading YAML metadata block is kept verbatim.
- **Reflow:** repairs skipped heading levels (`#`, `###`, `#####` becomes `#`, `##`, `###`), editing only the heading markers. The status bar reports changes and anything worth reviewing, such as hand-written tables of contents or anchor links.
- **Save** (Ctrl+S) and **Save as** (Ctrl+Shift+S) write Markdown. Saves go through a temporary file, so a failed save never truncates the original. Closing, opening, or crawling over unsaved work (including a fresh import) asks first.

Pandoc is optional for reading. Import (other than PDF), export, crawl, and format need it. md-viewer looks for `pandoc.exe` in its own data folder, beside the app, on `PATH`, and in Pandoc's standard install folders. **Fetch Pandoc** (in the ⋯ menu) downloads the latest official Windows x64 release from GitHub, checks GitHub's published SHA-256 digest when one is available, and installs it in md-viewer's data folder without changing `PATH`.

| Shortcut | Action |
| --- | --- |
| Ctrl+O | Open or import |
| Ctrl+S / Ctrl+Shift+S | Save / Save as |
| Ctrl+W | Close the document |
| Ctrl+E | Export |
| Ctrl+R | Toggle raw Markdown |
| Ctrl+Plus / Ctrl+Minus / Ctrl+0 | Zoom in / out / reset |

md-viewer's data folder holds `settings.json`, a fetched Pandoc, and the `app.log` and `crash.log` error logs. For the installed app it is the package's `LocalState` folder (`%LOCALAPPDATA%\Packages\IanRastall.MdViewer_5hb8chnqdhgxw\LocalState`), which Windows removes with the app; development builds use `%LOCALAPPDATA%\md-viewer`.

## Build and verify

Windows 10 build 19041 or later, the .NET 10 SDK, Visual Studio 2026 (or 2022) with the Desktop development with C++ tools, Windows SDK 10.0.26100, and CMake on PATH are required. Visual Studio 2026 needs CMake 4.2 or later. NuGet access is needed on the first build; md4c is vendored and pinned.

```powershell
.\scripts\build.ps1 -Test
```

Open `MdViewer.slnx` in Visual Studio to work on the managed projects. Build the native DLL with the script first; the application and tests copy `build/native/Release/mdv_native.dll` into their output folders. The native project also builds directly with CMake.

`tests/MdViewer.Core.Tests` exercises the C++ core through its ABI (block and inline structure, outline lines and slugs, statistics, entities, front matter, heading reflow in lists, quotes, and setext form, and a 20,000-heading document), file encodings and atomic saves, PDF import, the crawler's robots.txt and cleanup rules, and Pandoc import, format, and export when Pandoc is installed. Set `MDV_NETWORK_TESTS=1` to add one live crawl of a Wikipedia article. `tests/MdViewer.Tests` drives the real view-model commands with simulated dialogs and isolated settings: open, reflow, save, save as, unsaved-change prompts, encodings, zoom persistence, and Pandoc import and export. Both exit nonzero on failure.

Rebuild the icon sizes from `src/MdViewer/Assets/MdViewerIconMaster.png` with `.\scripts\build-icons.ps1` (requires ImageMagick); see `src/MdViewer/Assets/ICON.md`.

## Project layout

| Path | Responsibility |
| --- | --- |
| `native` | C++ Markdown core: parsing to a compact document model, outline, statistics, and heading reflow, behind a versioned UTF-8 C ABI (`native/include/mdv_native.h`) |
| `native/vendor/md4c` | md4c parser, vendored unchanged |
| `src/MdViewer.Core` | Native interop and document model, encodings and atomic saves, Pandoc, PDF import, crawler, settings |
| `src/MdViewer` | WinUI window, view model and commands, native Markdown rendering, dialogs |
| `packaging` | MSIX manifest (Start Menu entry and Open with) |
| `tests/MdViewer.Core.Tests` | Core and native integration checks |
| `tests/MdViewer.Tests` | View-model workflows with simulated dialogs |

The C++ core receives UTF-8 Markdown and returns either a binary document model or rewritten text; C# releases every native buffer. The renderer builds native WinUI text, lists, tables, code blocks, and images for each top-level block as it scrolls into view, so documents of tens of thousands of blocks stay responsive.

## Notes

- Rendering follows CommonMark with GitHub tables, task lists, strikethrough, and autolinks. Footnotes, math, and Pandoc-specific syntax appear as written. Raw HTML blocks are shown as source; HTML comments are hidden.
- Text selection works within a paragraph or block rather than across the whole page.
- JavaScript-heavy sites may not crawl cleanly if their content is rendered only in the browser. Review imported and crawled Markdown when sources contain complex tables or unusual HTML.

## License

MIT License. See [LICENSE](LICENSE). Third-party components are listed in [THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md).
