# Runs a PowerShell script outside a packaged app, as a terminal of your own would.
#
# Terminals inside a packaged (MSIX) app — the Claude desktop app's, for one — get that package's private copy of
# AppData: what they install into %LOCALAPPDATA% lands in ...\Packages\<app>\LocalCache\Local\..., where Explorer,
# shortcuts and your own terminals do not look. Windows (WMI) starts the script here as a process of its own, outside
# the package, waits for it and prints what it printed.
#
#   powershell -File tools\run-outside.ps1 tools\install-cli.ps1
param(
    [Parameter(Mandatory)] [string]$Script,
    [int]$TimeoutSeconds = 600
)
$ErrorActionPreference = 'Stop'

$script = (Resolve-Path $Script).Path
$log = Join-Path ([IO.Path]::GetTempPath()) "mictap-outside-$([Guid]::NewGuid().ToString('N')).log"
$command = "powershell.exe -NoProfile -ExecutionPolicy Bypass -Command `"& '$script' *> '$log'; exit `$LASTEXITCODE`""

$started = Invoke-CimMethod -ClassName Win32_Process -MethodName Create -Arguments @{
    CommandLine      = $command
    CurrentDirectory = (Split-Path $script)
}
if ($started.ReturnValue -ne 0) { throw "Windows did not start the script: Win32_Process.Create returned $($started.ReturnValue)" }

$deadline = (Get-Date).AddSeconds($TimeoutSeconds)
while (Get-Process -Id $started.ProcessId -ErrorAction SilentlyContinue) {
    if ((Get-Date) -gt $deadline) { throw "$Script still runs after $TimeoutSeconds s (process $($started.ProcessId)); its log: $log" }
    Start-Sleep -Milliseconds 500
}

if (Test-Path $log) {
    Get-Content $log
    Remove-Item $log
}
