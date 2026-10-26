# Publishes the mictap CLI to %LOCALAPPDATA%\mictap\bin and puts that folder on the user's PATH (once).
# Run from any folder: powershell -File tools\install-cli.ps1. New terminals see `mictap` afterwards.
$ErrorActionPreference = 'Stop'

$bin = Join-Path $env:LOCALAPPDATA 'mictap\bin'
dotnet publish (Join-Path $PSScriptRoot '..\src\MicTap.Cli') -c Release -o $bin
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed with exit code $LASTEXITCODE" }

$path = [Environment]::GetEnvironmentVariable('Path', 'User')
if (-not (($path -split ';') -contains $bin)) {
    [Environment]::SetEnvironmentVariable('Path', ($path.TrimEnd(';') + ';' + $bin), 'User')
    "Added $bin to the user PATH; open a new terminal to use mictap."
} else {
    "$bin is already on the user PATH."
}
