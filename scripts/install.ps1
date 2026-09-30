param([switch]$Launch)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$source = Join-Path $root 'artifacts\package'
if (-not (Test-Path -LiteralPath (Join-Path $source 'AppxManifest.xml'))) { throw 'Run scripts\package.ps1 first.' }
$developerMode = Get-ItemPropertyValue 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\AppModelUnlock' -Name AllowDevelopmentWithoutDevLicense -ErrorAction SilentlyContinue
if ($developerMode -ne 1) { throw 'Local unsigned registration requires Windows Developer Mode. Alternatively, sign and install the MSIX. This script does not change security settings.' }
if (Get-Process MdViewer -ErrorAction SilentlyContinue) { throw 'Save your work and close md-viewer before installing/updating.' }
$installRoot = Join-Path $env:LOCALAPPDATA 'Programs\md-viewer'
[xml]$manifest = Get-Content -LiteralPath (Join-Path $source 'AppxManifest.xml') -Raw
$version = [version]$manifest.Package.Identity.Version
$existing = Get-AppxPackage -Name 'IanRastall.MdViewer'
# Windows keeps the old location for an identical package identity/version.
# Advance the local registration version to make repeated installs real updates,
# retaining the package family (taskbar pins and app data).
if ($existing -and [version]$existing.Version -ge $version) {
    $installedVersion = [version]$existing.Version
    $parts = @($installedVersion.Major, $installedVersion.Minor, $installedVersion.Build, $installedVersion.Revision)
    for ($index = 3; $index -ge 0; $index--) {
        if ($parts[$index] -lt 65535) { $parts[$index]++; break }
        $parts[$index] = 0
    }
    if ($index -lt 0) { throw 'Package version exhausted.' }
    $version = [version]($parts -join '.')
}
$manifest.Package.Identity.Version = $version.ToString()
# Use a separate immutable directory per install so rebuilding cannot damage the installed app.
$destination = Join-Path $installRoot ($version.ToString() + '-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $destination -Force | Out-Null
Copy-Item -Path (Join-Path $source '*') -Destination $destination -Recurse -Force
$manifest.Save((Join-Path $destination 'AppxManifest.xml'))
Add-AppxPackage -Register (Join-Path $destination 'AppxManifest.xml')
$package = Get-AppxPackage -Name 'IanRastall.MdViewer'
if (-not $package -or $package.InstallLocation -ne $destination) { throw 'Package registration did not resolve to the new installation.' }
$appId = $package.PackageFamilyName + '!App'
Write-Host "Installed: $destination"
Write-Host 'Open Start, search md-viewer, then right-click it and choose Pin to taskbar.'
if ($Launch) { Start-Process explorer.exe -ArgumentList "shell:AppsFolder\$appId" -WindowStyle Normal }
