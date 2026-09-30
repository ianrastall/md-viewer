param([string]$File = '', [switch]$Build)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$executable = Join-Path $root 'artifacts\app\MdViewer.exe'
if ($Build -or -not (Test-Path -LiteralPath $executable)) { & "$PSScriptRoot\build.ps1" }
$startOptions = @{ FilePath = $executable; WorkingDirectory = $root; WindowStyle = 'Normal' }
if ($File) { $startOptions.ArgumentList = '"' + (Resolve-Path -LiteralPath $File).Path + '"' }
Start-Process @startOptions
