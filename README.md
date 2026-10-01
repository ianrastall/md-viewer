# md-viewer

A native dark-mode Markdown reader and converter for Windows. It renders Markdown with WinUI controls, builds a heading outline for navigation, imports Word, HTML, EPUB, and PDF documents, exports through Pandoc, and can collect a documentation site into one Markdown file. A green accent palette runs through the interface; theme colors are centralized in `src/MdViewer/App.xaml`.

Built with **WinUI 3, C# 14, .NET 10, CommunityToolkit.Mvvm, and C++20**. The program itself is C++ behind a versioned C ABI (`native/include/mdv_native.h`): Markdown parsing and reflow, reading and saving documents, Pandoc import/export/format, PDF import with Tesseract or Windows OCR, the documentation crawler, and finding, checking, and installing Pandoc and Tesseract. C# draws the window and handles the outside world: rendering, the view model, dialogs, settings, and packaging. No browser or WebView is used.

> md-viewer replaces **MDViewer**, the earlier unpackaged version of this app. Its last source is tagged [`v1-legacy`](https://github.com/ianrastall/md-viewer/tree/v1-legacy).

## Install

Download `md-viewer-<version>-windows-x64.zip` from [Releases](https://github.com/ianrastall/md-viewer/releases), unzip it, and run `md-viewer-<version>-setup.exe`. It installs for your account only, without administrator rights, and includes everything md-viewer needs (.NET, the Windows App SDK, and the C++ core). Windows 10 version 2004 or later, 64-bit, is required. SmartScreen may show **Windows protected your PC** for an unsigned or newly released build; choose **More info → Run anyway**. Installing a newer version replaces the old one and keeps your settings; remove md-viewer through **Settings → Apps → Installed apps**.

Pandoc and Tesseract are optional and installed separately; md-viewer's **Tools** window installs and updates them.

## Run

The installed **md-viewer** entry has a green **M** icon matching Minerva, Lore, Board Meeting, and CTML Workspace. Search for it in Start, right-click it, and choose **Pin to taskbar** (or **More → Pin to taskbar**). md-viewer also appears in File Explorer's **Open with** menu for `.md`, `.markdown`, `.mdown`, and `.mkd` files; it does not change your default app.

For development, build and install/update the local MSIX package (this is separate from the installer above, and needs Windows Developer Mode):

```powershell
.\scripts\package.ps1 -Install
```

This publishes a self-contained x64 application, validates and creates `artifacts/MdViewer_<version>_x64.msix`, and registers a separate copy under `%LOCALAPPDATA%\Programs\md-viewer`. The installed app includes .NET and Windows App SDK runtimes and does not depend on the build folder. Local registration requires Windows Developer Mode; the script does not change security settings or install certificates. The MSIX is unsigned and must be signed before distribution to other machines. Close md-viewer before updating. `-Test` runs the verification harnesses before packaging. `-Launch` on `scripts\install.ps1` starts the app afterwards.

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
- **Import:** Open also converts `.docx`, `.html`, `.htm`, `.epub`, `.odt`, and `.rtf` through Pandoc, and `.pdf` with built-in text extraction plus OCR for scanned pages (Tesseract when it is installed, otherwise Windows OCR; see Tools). Imported Markdown includes a hidden diagnostics comment with page, OCR, and warning details.
- **Export** (Ctrl+E): Word, HTML, EPUB, RTF, ODT, LaTeX, Typst, reStructuredText, or Org through Pandoc. Relative images resolve from the document's folder.
- **Crawl:** collects a documentation site into one Markdown document. It stays within the start page's folder, fetches one page at a time at least two seconds apart, honors `robots.txt` and `Crawl-delay`, stops at 250 pages, and fetches Wikipedia and other MediaWiki articles as a single article.
- **Format:** normalizes the Markdown through Pandoc: ATX headings, pipe tables, no hard wrapping, and no Pandoc-only attribute, div, or span syntax. A leading YAML metadata block is kept verbatim. Files with old Mac (CR-only) or doubled (CR CR LF) line endings keep their paragraphs. Text md-viewer produces (Format, imports, and crawls) uses the platform's line endings, CRLF on Windows; Reflow edits headings in place and leaves line endings alone.
- **Reflow:** repairs skipped heading levels (`#`, `###`, `#####` becomes `#`, `##`, `###`), editing only the heading markers. The status bar reports changes and anything worth reviewing, such as hand-written tables of contents or anchor links.
- **Undo** (Ctrl+Z) and **Redo** (Ctrl+Y or Ctrl+Shift+Z) step through Format and Reflow changes, up to 100 steps; opening or closing a document clears the history.
- **Save** (Ctrl+S) and **Save as** (Ctrl+Shift+S) write Markdown. Saves go through a temporary file, so a failed save never truncates the original. Closing, opening, or crawling over unsaved work (including a fresh import) asks first.

- **Tools** (in the ⋯ menu) shows each external program md-viewer uses: whether it is installed, where, its version and languages, and whether a newer official release is out. md-viewer also checks once a day (this can be turned off there) and shows a notice when an update is available.
  - **Pandoc** is needed for Word, HTML, EPUB, ODT, and RTF import, export, crawl, and Format. md-viewer uses its own copy if you downloaded one in Tools, then one beside the app, on `PATH`, or in Pandoc's standard install folders. **Download** fetches the latest official Windows release from GitHub into md-viewer's data folder, without changing `PATH` or any other copy.
  - **Tesseract OCR** reads scanned PDF pages, in any of the languages its installer offers. md-viewer finds it on `PATH` or where its installer puts it. **Install** / **Run latest installer** downloads the official Windows installer from the [Tesseract project's releases](https://github.com/tesseract-ocr/tesseract/releases) and runs it; Windows asks for administrator permission, and the installer is where you choose languages.
  - **OCR for scanned PDFs**: Automatic (Tesseract when installed, otherwise Windows OCR), Windows OCR, or Tesseract, and the Tesseract languages to use, such as `eng+deu`.
  - Downloads are checked against the SHA-256 digests GitHub publishes for each release.
| Shortcut | Action |
| --- | --- |
| Ctrl+O | Open or import |
| Ctrl+S / Ctrl+Shift+S | Save / Save as |
| Ctrl+W | Close the document |
| Ctrl+Z / Ctrl+Y | Undo / redo Format and Reflow |
| Ctrl+E | Export |
| Ctrl+R | Toggle raw Markdown |
| Ctrl+Plus / Ctrl+Minus / Ctrl+0 | Zoom in / out / reset |

md-viewer's data folder holds `settings.json`, a fetched Pandoc, and the `app.log` and `crash.log` error logs. For the installed app it is the package's `LocalState` folder (`%LOCALAPPDATA%\Packages\IanRastall.MdViewer_5hb8chnqdhgxw\LocalState`), which Windows removes with the app; development builds use `%LOCALAPPDATA%\md-viewer`.

## Build and verify

Windows 10 build 19041 or later, the .NET 10 SDK, [Inno Setup 6](https://jrsoftware.org/isinfo.php) (for release installers only), Visual Studio 2026 (or 2022) with the Desktop development with C++ tools, Windows SDK 10.0.26100 (including its C++/WinRT headers), and CMake on PATH are required. Visual Studio 2026 needs CMake 4.2 or later. The first build needs network access for NuGet and for PDFium, which `scripts\build.ps1` downloads from a pinned [pdfium-binaries](https://github.com/bblanchon/pdfium-binaries) release and checks against its SHA-256. The other C/C++ libraries are vendored and pinned in `native/vendor`.

```powershell
.\scripts\build.ps1 -Test
```

Open `MdViewer.slnx` in Visual Studio to work on the managed projects. Build the native core with the script first; the application and tests copy `build/native/Release/mdv_native.dll` and `pdfium.dll` into their output folders. The native project also builds directly with CMake (`native/CMakeLists.txt`).

`build/native/Release/mdv_tests.exe` (built with the core, run by `-Test`) exercises the C++ core through its C ABI and internal modules: block and inline structure, outline lines and slugs, statistics, entities, and front matter; heading reflow in lists, quotes, and setext form; a 20,000-heading document; file encodings, Unicode paths, and atomic saves; PDF import, including Windows OCR and Tesseract on a text-less scanned page; tool detection and version comparison; robots.txt, URL resolution, HTML selection, and Markdown cleanup for the crawler; cancellation; and Pandoc import, format, and export when Pandoc is installed. Set `MDV_NETWORK_TESTS=1` to add one live crawl of a Wikipedia article and a live check of the latest Pandoc and Tesseract releases. `tests/MdViewer.Tests` drives the real view-model commands through the C ABI with simulated dialogs and isolated settings: open, reflow, save, save as, undo and redo, unsaved-change prompts, encodings, Tools status and OCR preferences, zoom persistence, and Pandoc import and export. Both exit nonzero on failure.

Rebuild the icon sizes from `src/MdViewer/Assets/MdViewerIconMaster.png` with `.\scripts\build-icons.ps1` (requires ImageMagick); see `src/MdViewer/Assets/ICON.md`.

### Releases

```powershell
.\scripts\release.ps1
```

This runs the tests, publishes the self-contained app to `artifacts/release/app`, builds `md-viewer-<version>-setup.exe` from `installer/md-viewer.iss` with Inno Setup, and zips it with install notes, the license, and third-party notices as `artifacts/release/md-viewer-<version>-windows-x64.zip`, printing SHA-256 hashes of both. Upload the zip to a GitHub release.

#### Signing

`release.ps1` signs md-viewer's own binaries (`MdViewer.exe`, its managed assemblies, and `mdv_native.dll`), the installer, and the uninstaller when a code-signing certificate is available, and `package.ps1` signs the MSIX the same way. It uses `-CertificateThumbprint`, or else the newest valid code-signing certificate in your user store whose subject is the package publisher, `CN=Ian Rastall`. The private key stays in the certificate store. Signatures are timestamped (`-TimestampUrl`, DigiCert's by default) so they stay valid after the certificate expires. `-Unsigned` builds without signing; with no certificate the build is unsigned and says so.

A self-signed certificate only satisfies machines that trust it, so it does not remove SmartScreen warnings for people downloading from GitHub. That needs a publicly trusted certificate, for example [Azure Artifact Signing](https://azure.microsoft.com/en-us/products/artifact-signing) or [SignPath Foundation](https://signpath.org/terms.html) (free for open-source projects); new releases can still show a milder SmartScreen prompt until they build reputation. MSIX signing requires the certificate subject to equal the manifest's `Publisher`. The version comes from `<Version>` in `Directory.Build.props`; keep `packaging/AppxManifest.xml` in step. The installer is a per-user, unpackaged install, so its data lives in `%LOCALAPPDATA%\md-viewer`.

## Project layout

| Path | Responsibility |
| --- | --- |
| `native/include/mdv_native.h` | The C ABI between the C# frame and the C++ program |
| `native/src` | The program: Markdown engine (`markdown`), documents and encodings (`files`), Pandoc (`pandoc`, `subprocess`), PDF import and OCR (`pdf`), crawler (`crawl`, `html`, `http`, `url`), finding, checking, and installing Pandoc and Tesseract (`tools`), and the exported ABI (`abi`) |
| `native/vendor` | md4c, Gumbo, nlohmann/json, and miniz, vendored unchanged |
| `native/tests` | Native integration checks |
| `src/MdViewer.Interop` | C# declarations and marshaling for the C ABI, and the document model the renderer draws |
| `src/MdViewer` | WinUI windows (main and Tools), view models and commands, native Markdown rendering, dialogs, settings |
| `packaging` | MSIX manifest for the local development install (Start Menu entry and Open with) |
| `installer` | Inno Setup script for the release installer |
| `tests/MdViewer.Tests` | View-model workflows with simulated dialogs |

C# passes UTF-8 text and paths across the ABI; the core returns a result buffer (a binary document model for rendering, or text) that C# releases with `mdv_result_free`. Long operations report progress and poll for cancellation through a callback, and run on a worker thread so the window stays responsive. The renderer builds native WinUI text, lists, tables, code blocks, and images for each top-level block as it scrolls into view, so documents of tens of thousands of blocks stay responsive.

## Notes

- Rendering follows CommonMark with GitHub tables, task lists, strikethrough, and autolinks. Footnotes, math, and Pandoc-specific syntax appear as written. Raw HTML blocks are shown as source; HTML comments are hidden.
- Text selection works within a paragraph or block rather than across the whole page.
- JavaScript-heavy sites may not crawl cleanly if their content is rendered only in the browser. Review imported and crawled Markdown when sources contain complex tables or unusual HTML.

## License

MIT License. See [LICENSE](LICENSE). Third-party components are listed in [THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md).
