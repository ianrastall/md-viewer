param([switch]$Test)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
# PDFium comes prebuilt from pdfium-binaries, pinned by release and SHA-256.
$pdfiumRelease = 'chromium/8076'
$pdfiumSha256 = '808d36da9bc5a3104315fb307c80998121f565ee53953633bf33e80d7429e5ac'
Push-Location $root
try {
    $pdfium = Join-Path $root 'build\pdfium'
    if (-not (Test-Path -LiteralPath (Join-Path $pdfium 'include\fpdfview.h'))) {
        New-Item -ItemType Directory -Force $pdfium | Out-Null
        $archive = Join-Path $root 'build\pdfium-win-x64.tgz'
        $url = "https://github.com/bblanchon/pdfium-binaries/releases/download/$([uri]::EscapeDataString($pdfiumRelease))/pdfium-win-x64.tgz"
        Write-Host "Downloading PDFium $pdfiumRelease..."
        Invoke-WebRequest -Uri $url -OutFile $archive -UseBasicParsing
        if ((Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash.ToLowerInvariant() -ne $pdfiumSha256) { throw 'The PDFium download does not match its pinned SHA-256.' }
        & tar -xzf $archive -C $pdfium
        if ($LASTEXITCODE -ne 0) { throw 'Could not extract PDFium.' }
    }

    $vswherePath = "${env:ProgramFiles(x86)}\Microsoft Visual Studio\Installer\vswhere.exe"
    if (-not (Test-Path -LiteralPath $vswherePath)) { throw 'Install Visual Studio 2026 or 2022 with Desktop development with C++ and a Windows SDK.' }
    $installation = (& $vswherePath -latest -products '*' -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -format json | ConvertFrom-Json) | Select-Object -First 1
    if (-not $installation) { throw 'The Visual C++ x64 build tools are required.' }
    $generator = if ([version]$installation.installationVersion -ge [version]'18.0') { 'Visual Studio 18 2026' } else { 'Visual Studio 17 2022' }
    if (-not (Get-Command cmake -ErrorAction SilentlyContinue)) { throw 'CMake is required on PATH (4.2+ for Visual Studio 2026).' }
    & cmake -S native -B build/native -G $generator -A x64 "-DCMAKE_GENERATOR_INSTANCE=$($installation.installationPath)"
    if ($LASTEXITCODE -ne 0) { throw 'CMake configuration failed.' }
    & cmake --build build/native --config Release
    if ($LASTEXITCODE -ne 0) { throw 'Native build failed.' }
    if ($Test) {
        & ./build/native/Release/mdv_tests.exe
        if ($LASTEXITCODE -ne 0) { throw 'Native checks failed.' }
    }
    & dotnet build src/MdViewer/MdViewer.csproj -c Release --nologo '-p:OutputPath=../../artifacts/app/'
    if ($LASTEXITCODE -ne 0) { throw 'WinUI build failed. Close md-viewer if its output files are locked.' }
    if ($Test) {
        & dotnet run --project tests/MdViewer.Tests -c Release
        if ($LASTEXITCODE -ne 0) { throw 'View-model checks failed.' }
    }
    Write-Host "Built: $root\artifacts\app\MdViewer.exe"
}
finally { Pop-Location }
