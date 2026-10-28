# Publishes the mictap window app to %LOCALAPPDATA%\mictap\app and puts a «mictap» shortcut on the desktop and in
# the Start menu. Run from any folder: powershell -File tools\install-app.ps1. Run again after an update; close
# mictap first (tray icon → Выход), its files cannot be replaced while it runs.
$ErrorActionPreference = 'Stop'

if (Get-Process MicTap.App -ErrorAction SilentlyContinue) {
    throw 'mictap is running: close it (tray icon → Выход) and run this again.'
}

$app = Join-Path $env:LOCALAPPDATA 'mictap\app'
. (Join-Path $PSScriptRoot 'outside-check.ps1')
Assert-WritesLandForReal $app

dotnet publish (Join-Path $PSScriptRoot '..\src\MicTap.App') -c Release -o $app
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed with exit code $LASTEXITCODE" }

$exe = Join-Path $app 'MicTap.App.exe'

$shell = New-Object -ComObject WScript.Shell
foreach ($folder in [Environment]::GetFolderPath('Desktop'), [Environment]::GetFolderPath('Programs')) {
    $shortcut = $shell.CreateShortcut((Join-Path $folder 'mictap.lnk'))
    $shortcut.TargetPath = $exe
    $shortcut.WorkingDirectory = $app
    $shortcut.IconLocation = "$exe,0"
    $shortcut.Description = 'mictap: запись и распознавание речи'
    $shortcut.Save()
    "Shortcut: $($shortcut.FullName)"
}
