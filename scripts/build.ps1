param([switch]$Test)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
Push-Location $root
try {
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
    & dotnet build src/MdViewer/MdViewer.csproj -c Release --nologo '-p:OutputPath=../../artifacts/app/'
    if ($LASTEXITCODE -ne 0) { throw 'WinUI build failed. Close md-viewer if its output files are locked.' }
    if ($Test) {
        & dotnet run --project tests/MdViewer.Core.Tests -c Release
        if ($LASTEXITCODE -ne 0) { throw 'Core checks failed.' }
        & dotnet run --project tests/MdViewer.Tests -c Release
        if ($LASTEXITCODE -ne 0) { throw 'View-model checks failed.' }
    }
    Write-Host "Built: $root\artifacts\app\MdViewer.exe"
}
finally { Pop-Location }
