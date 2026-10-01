# Builds a GitHub release: tests, publishes the self-contained app, compiles the Inno Setup installer,
# and zips the installer with install notes. Output: artifacts\release\md-viewer-<version>-windows-x64.zip
#
# Signing: md-viewer's own binaries, the installer, and its uninstaller are signed when a code-signing
# certificate is available: -CertificateThumbprint, or else a valid one in your store whose subject is the
# package publisher (CN=Ian Rastall). -Unsigned skips signing. Signatures are timestamped.
param([switch]$SkipTests, [string]$CertificateThumbprint, [switch]$Unsigned, [string]$TimestampUrl = 'http://timestamp.digicert.com')
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
. "$PSScriptRoot\signing.ps1"
$certificate = if ($Unsigned) { $null } else { Resolve-SigningCertificate $CertificateThumbprint (Get-PackagePublisher $root) }
if ($certificate) { Write-Host "Signing with $($certificate.Subject) ($($certificate.Thumbprint), valid until $($certificate.NotAfter.ToString('yyyy-MM-dd')))." }
else { Write-Host 'No code-signing certificate selected; the release will be unsigned.' }
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

$innoArguments = @('/Qp', "/DAppVersion=$version", "/DSourceDir=$app", "/DOutputDir=$release")
if ($certificate) {
    # md-viewer's own code; third-party binaries (.NET, Windows App SDK, PDFium) keep their publishers' signatures.
    Invoke-CodeSigning $certificate $TimestampUrl @('MdViewer.exe', 'MdViewer.dll', 'MdViewer.Interop.dll', 'mdv_native.dll' | ForEach-Object { Join-Path $app $_ })
    # Inno Setup runs this for setup.exe and the uninstaller; $f is the file and $q a quote.
    $signCommand = '$q' + (Get-SignTool) + '$q ' + ((Get-SignArguments $certificate $TimestampUrl) -join ' ') + ' $f'
    $innoArguments += @('/DSign', "/Smdviewer=$signCommand")
}
& $iscc @innoArguments (Join-Path $root 'installer\md-viewer.iss')
if ($LASTEXITCODE -ne 0) { throw 'Inno Setup could not build the installer.' }
$setup = Join-Path $release "md-viewer-$version-setup.exe"

$notes = @"
md-viewer $version for Windows 10 (version 2004) and Windows 11, 64-bit

Install
  1. Run md-viewer-$version-setup.exe. It installs for your account only and does not need administrator rights.
  2. Windows SmartScreen may say "Windows protected your PC" for a new release.
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
if ($certificate) { foreach ($file in $setup, (Join-Path $app 'MdViewer.exe'), (Join-Path $app 'mdv_native.dll')) { Show-Signature $file } }
