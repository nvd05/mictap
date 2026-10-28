# Publishes the mictap window app to %LOCALAPPDATA%\mictap\app and puts a «mictap» shortcut on the desktop and in
# the Start menu. Run from any folder: powershell -File tools\install-app.ps1. Run again after an update; close
# mictap first (tray icon → Выход), its files cannot be replaced while it runs.
$ErrorActionPreference = 'Stop'

if (Get-Process MicTap.App -ErrorAction SilentlyContinue) {
    throw 'mictap is running: close it (tray icon → Выход) and run this again.'
}

$app = Join-Path $env:LOCALAPPDATA 'mictap\app'
dotnet publish (Join-Path $PSScriptRoot '..\src\MicTap.App') -c Release -o $app
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed with exit code $LASTEXITCODE" }

# Where the exe really is. Run from a packaged app (the Claude app's terminal), AppData writes land in that package's
# own copy (...\Packages\<app>\LocalCache\Local\...); Explorer does not see %LOCALAPPDATA%\mictap\app then, and a
# shortcut pointing there gets a blank icon.
Add-Type -Namespace MicTapInstall -Name Files -MemberDefinition @'
[DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
public static extern uint GetFinalPathNameByHandle(Microsoft.Win32.SafeHandles.SafeFileHandle file, System.Text.StringBuilder path, uint size, uint flags);
'@
$stream = [System.IO.File]::OpenRead((Join-Path $app 'MicTap.App.exe'))
$path = New-Object System.Text.StringBuilder 1024
$length = [MicTapInstall.Files]::GetFinalPathNameByHandle($stream.SafeFileHandle, $path, $path.Capacity, 0)
$stream.Dispose()
if ($length -eq 0 -or $length -ge $path.Capacity) { throw "GetFinalPathNameByHandle failed: $([Runtime.InteropServices.Marshal]::GetLastWin32Error())" }
$exe = $path.ToString() -replace '^\\\\\?\\', ''
if ($exe -like '*\Packages\*\LocalCache\*') {
    "Installed inside a packaged app's private AppData: $exe"
    "Start mictap from its shortcut (Explorer), not from this terminal: started from here it gets that private AppData too."
}

$shell = New-Object -ComObject WScript.Shell
foreach ($folder in [Environment]::GetFolderPath('Desktop'), [Environment]::GetFolderPath('Programs')) {
    $shortcut = $shell.CreateShortcut((Join-Path $folder 'mictap.lnk'))
    $shortcut.TargetPath = $exe
    $shortcut.WorkingDirectory = Split-Path $exe
    $shortcut.IconLocation = "$exe,0"
    $shortcut.Description = 'mictap: запись и распознавание речи'
    $shortcut.Save()
    "Shortcut: $($shortcut.FullName)"
}
