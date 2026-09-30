$ErrorActionPreference = 'Stop'
$assets = Join-Path (Split-Path -Parent $PSScriptRoot) 'src\MdViewer\Assets'
$master = Join-Path $assets 'MdViewerIconMaster.png'
if (-not (Get-Command magick -ErrorAction SilentlyContinue)) { throw 'ImageMagick is required only to regenerate the checked-in icon sizes.' }
$icons = @{
    'Square44x44Logo.png' = 44
    'Square44x44Logo.scale-200.png' = 88
    'Square150x150Logo.png' = 150
    'Square150x150Logo.scale-200.png' = 300
    'StoreLogo.png' = 50
}
foreach ($size in @(16, 24, 32, 48, 64, 256)) {
    $icons["Square44x44Logo.targetsize-${size}_altform-unplated.png"] = $size
    $icons["Square44x44Logo.targetsize-${size}_altform-lightunplated.png"] = $size
}
foreach ($name in $icons.Keys) {
    $size = $icons[$name]
    & magick $master -resize "${size}x${size}" (Join-Path $assets $name)
    if ($LASTEXITCODE -ne 0) { throw "Could not generate $name" }
}
& magick $master -define icon:auto-resize=256,128,64,48,32,24,16 (Join-Path $assets 'AppIcon.ico')
if ($LASTEXITCODE -ne 0) { throw 'Could not generate AppIcon.ico' }
