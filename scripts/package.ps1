param([switch]$Install, [switch]$Test)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$layout = Join-Path $root 'artifacts\package'
[xml]$manifest = Get-Content -LiteralPath (Join-Path $root 'packaging\AppxManifest.xml') -Raw
$msix = Join-Path $root "artifacts\MdViewer_$($manifest.Package.Identity.Version)_x64.msix"
$sdkBin = Join-Path ${env:ProgramFiles(x86)} 'Windows Kits\10\bin'
$makeAppx = Get-ChildItem -LiteralPath $sdkBin -Directory |
    Where-Object Name -Match '^10\.0\.\d+\.\d+$' |
    Sort-Object { [version]$_.Name } -Descending |
    ForEach-Object { Join-Path $_.FullName 'x64\makeappx.exe' } |
    Where-Object { Test-Path -LiteralPath $_ } | Select-Object -First 1
if (-not $makeAppx) { throw 'Install the Windows SDK with MakeAppx.' }
& "$PSScriptRoot\build.ps1" -Test:$Test
# Every package starts from a clean staging directory; installed files live elsewhere.
if (Test-Path -LiteralPath $layout) {
    $resolvedLayout = (Resolve-Path -LiteralPath $layout).Path
    $expectedLayout = [IO.Path]::GetFullPath((Join-Path $root 'artifacts\package'))
    if ($resolvedLayout -ne $expectedLayout -or (Get-Item -LiteralPath $layout).Attributes -band [IO.FileAttributes]::ReparsePoint) { throw 'Unsafe package staging path.' }
    Remove-Item -LiteralPath $resolvedLayout -Recurse -Force
}
& dotnet publish (Join-Path $root 'src\MdViewer\MdViewer.csproj') -c Release -r win-x64 --self-contained true --nologo -o $layout
if ($LASTEXITCODE -ne 0) { throw 'Self-contained publish failed.' }
foreach ($required in @('MdViewer.exe', 'MdViewer.pri', 'App.xbf', 'MainWindow.xbf', 'coreclr.dll', 'Microsoft.UI.Xaml.dll', 'mdv_native.dll', 'Assets\AppIcon.ico', 'Assets\Square44x44Logo.png', 'licenses\md4c.txt')) {
    if (-not (Test-Path -LiteralPath (Join-Path $layout $required))) { throw "Package is missing $required" }
}
Copy-Item -LiteralPath (Join-Path $root 'packaging\AppxManifest.xml') -Destination $layout
Copy-Item -LiteralPath (Join-Path $root 'THIRD_PARTY_NOTICES.md') -Destination $layout
& $makeAppx pack /d $layout /p $msix /o *> (Join-Path $root 'artifacts\package-build.log')
if ($LASTEXITCODE -ne 0) { throw 'MSIX validation/packing failed. See artifacts\package-build.log.' }
Write-Host "Package: $msix (unsigned; sign before distribution)"
if ($Install) { & "$PSScriptRoot\install.ps1" }
