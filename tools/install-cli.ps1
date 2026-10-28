# Publishes the mictap CLI to %LOCALAPPDATA%\mictap\bin and puts that folder on the user's PATH (once).
# Run from any folder: powershell -File tools\install-cli.ps1. New terminals see `mictap` afterwards.
$ErrorActionPreference = 'Stop'

$bin = Join-Path $env:LOCALAPPDATA 'mictap\bin'
. (Join-Path $PSScriptRoot 'outside-check.ps1')
Assert-WritesLandForReal $bin

# --disable-build-servers: an MSBuild node left running by another terminal would copy the files in its place —
# from a packaged app's terminal, into that app's private AppData.
dotnet publish (Join-Path $PSScriptRoot '..\src\MicTap.Cli') -c Release -o $bin --disable-build-servers
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed with exit code $LASTEXITCODE" }

$path = [Environment]::GetEnvironmentVariable('Path', 'User')
if (-not (($path -split ';') -contains $bin)) {
    [Environment]::SetEnvironmentVariable('Path', ($path.TrimEnd(';') + ';' + $bin), 'User')
    "Added $bin to the user PATH; open a new terminal to use mictap."
} else {
    "$bin is already on the user PATH."
}
