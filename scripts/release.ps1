# Builds a GitHub release: tests, publishes the self-contained app, compiles the Inno Setup installer,
# and zips the installer with install notes. Output: artifacts\release\md-viewer-<version>-windows-x64.zip
param([switch]$SkipTests)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$version = ([xml](Get-Content -LiteralPath (Join-Path $root 'Directory.Build.props') -Raw)).Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1
if (-not $version) { throw 'Directory.Build.props has no <Version>.' }
$release = Join-Path $root 'artifacts\release'
$app = Join-Path $release 'app'
$iscc = @((Get-Command iscc -ErrorAction SilentlyContinue).Source, "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe", "$env:ProgramFiles\Inno Setup 6\ISCC.exe", "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe") |
    Where-Object { $_ -and (Test-Path -LiteralPath $_) } | Select-Object -First 1
if (-not $iscc) { throw 'Inno Setup 6 is required to build the installer (https://jrsoftware.org/isinfo.php, or: winget install JRSoftware.InnoSetup).' }

& "$PSScriptRoot\build.ps1" -Test:(-not $SkipTests)

# Start from a clean release folder.
if (Test-Path -LiteralPath $release) {
    $resolved = (Resolve-Path -LiteralPath $release).Path
    if ($resolved -ne [IO.Path]::GetFullPath($release) -or (Get-Item -LiteralPath $release).Attributes -band [IO.FileAttributes]::ReparsePoint) { throw 'Unsafe release path.' }
    Remove-Item -LiteralPath $resolved -Recurse -Force
}
& dotnet publish (Join-Path $root 'src\MdViewer\MdViewer.csproj') -c Release -r win-x64 --self-contained true --nologo -o $app
if ($LASTEXITCODE -ne 0) { throw 'Self-contained publish failed.' }
foreach ($required in @('MdViewer.exe', 'MdViewer.pri', 'App.xbf', 'MainWindow.xbf', 'ToolsWindow.xbf', 'coreclr.dll', 'Microsoft.UI.Xaml.dll', 'mdv_native.dll', 'pdfium.dll', 'Assets\AppIcon.ico', 'licenses\md4c.txt', 'licenses\pdfium.txt')) {
    if (-not (Test-Path -LiteralPath (Join-Path $app $required))) { throw "The release is missing $required" }
}

& $iscc /Qp "/DAppVersion=$version" "/DSourceDir=$app" "/DOutputDir=$release" (Join-Path $root 'installer\md-viewer.iss')
if ($LASTEXITCODE -ne 0) { throw 'Inno Setup could not build the installer.' }
$setup = Join-Path $release "md-viewer-$version-setup.exe"

$notes = @"
md-viewer $version for Windows 10 (version 2004) and Windows 11, 64-bit

Install
  1. Run md-viewer-$version-setup.exe. It installs for your account only and does not need administrator rights.
  2. The installer is not code-signed, so Windows SmartScreen may say "Windows protected your PC".
     Choose "More info", then "Run anyway".
  3. Open md-viewer from the Start menu. It also appears under "Open with" for .md files.

Everything md-viewer needs is included. Two optional programs add features; install or update them
from md-viewer's Tools window (in the ... menu):
  - Pandoc: importing Word, HTML, and EPUB, exporting, crawling, and Format.
  - Tesseract OCR: better reading of scanned PDF pages (Windows OCR is used otherwise).

Updating: run the newer installer; it replaces the old version and keeps your settings.
Removing: Settings > Apps > Installed apps > md-viewer > Uninstall.

https://github.com/ianrastall/md-viewer
"@
$staging = Join-Path $release 'zip'
New-Item -ItemType Directory -Force $staging | Out-Null
Copy-Item -LiteralPath $setup -Destination $staging
Set-Content -LiteralPath (Join-Path $staging 'INSTALL.txt') -Value $notes -Encoding utf8
Copy-Item -LiteralPath (Join-Path $root 'LICENSE') -Destination (Join-Path $staging 'LICENSE.txt')
Copy-Item -LiteralPath (Join-Path $root 'THIRD_PARTY_NOTICES.md') -Destination $staging
$zip = Join-Path $release "md-viewer-$version-windows-x64.zip"
Compress-Archive -Path (Join-Path $staging '*') -DestinationPath $zip -CompressionLevel Optimal
Remove-Item -LiteralPath $staging -Recurse -Force

Write-Host "Installer: $setup"
Write-Host "Release zip: $zip"
foreach ($file in $setup, $zip) { Write-Host ("SHA-256 {0}  {1}" -f (Get-FileHash -LiteralPath $file -Algorithm SHA256).Hash.ToLowerInvariant(), (Split-Path $file -Leaf)) }
