# Third-party notices

- **md4c 0.5.2:** Martin Mitáš and contributors, [mity/md4c](https://github.com/mity/md4c), tag `release-0.5.2` (revision `729e6b8b320caa96328968ab27d7db2235e4fb47`). Vendored unchanged in `native/vendor/md4c` (`md4c.c`, `md4c.h`, `entity.c`, `entity.h`); MIT license in `native/vendor/md4c/LICENSE.md`, shipped as `licenses/md4c.txt`. Used for CommonMark and GitHub-flavored Markdown parsing and HTML entity decoding.
- **Windows App SDK 2.4.0:** Microsoft, distributed through NuGet under the package's license and third-party notices.
- **CommunityToolkit.Mvvm 8.4.2:** .NET Foundation and contributors, MIT, distributed through NuGet.
- **Gumbo 0.14.1:** Google and the [gumbo-parser](https://codeberg.org/gumbo-parser/gumbo-parser) maintainers, Apache-2.0. Vendored unchanged in `native/vendor/gumbo` (revision in `REVISION`, license in `COPYING`, shipped as `licenses/gumbo.txt`); `msvc/strings.h` is the project's own Visual C++ shim. Used to parse crawled HTML.
- **nlohmann/json 3.12.0:** Niels Lohmann, MIT. Vendored unchanged in `native/vendor/nlohmann`, shipped as `licenses/nlohmann-json.txt`. Used for the MediaWiki and GitHub APIs.
- **miniz 3.1.2:** Rich Geldreich and contributors, MIT. Vendored unchanged in `native/vendor/miniz`, shipped as `licenses/miniz.txt`. Used to extract a fetched Pandoc.
- **PDFium (pdfium-binaries chromium/8076):** Google and the PDFium authors, BSD-3-Clause, with the third-party licenses listed in the release (FreeType, libjpeg-turbo, OpenJPEG, lcms, zlib, and others). The prebuilt `pdfium.dll` from [bblanchon/pdfium-binaries](https://github.com/bblanchon/pdfium-binaries) is downloaded at build time and verified against SHA-256 `808d36da9bc5a3104315fb307c80998121f565ee53953633bf33e80d7429e5ac`; its licenses ship in `licenses/pdfium.txt` and `licenses/pdfium/`. Used for PDF text extraction and page rendering for OCR.
- **Tesseract** is not bundled. md-viewer runs an installed `tesseract.exe`, or the official Windows installer (by UB Mannheim, published in the [tesseract-ocr/tesseract](https://github.com/tesseract-ocr/tesseract/releases) releases, Apache-2.0) that the Tools window downloads and starts.
- **Inno Setup** (Jordan Russell and Martijn Laan) builds the release installer; its setup and uninstaller code is distributed under the Inno Setup license.
- **Windows OCR, WinHTTP, and CNG** are Windows components, used through C++/WinRT and Win32.
- **Pandoc** is not bundled. md-viewer runs a `pandoc.exe` that is already installed, or one that **Fetch Pandoc** downloads from the official [jgm/pandoc](https://github.com/jgm/pandoc/releases) releases (GPL-2.0-or-later) into md-viewer's data folder from the Tools window.

The icon recolors Minerva's master icon, from the same author. Fonts use Windows-installed Segoe UI Variable, Segoe UI Symbol, and Cascadia Mono with system fallbacks. No font files are bundled.
