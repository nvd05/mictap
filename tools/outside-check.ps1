# Dot-sourced by the install scripts. Terminals of a packaged (MSIX) app — the Claude desktop app's, for one — get a
# private copy of AppData: files written into %LOCALAPPDATA% land in ...\Packages\<app>\LocalCache\..., where Explorer,
# shortcuts and your own terminals never look. Such a terminal has no package identity of its own, so the only sure
# sign is where a written file really ends up.

Add-Type -Namespace MicTapInstall -Name Paths -MemberDefinition @'
[DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
public static extern uint GetFinalPathNameByHandle(Microsoft.Win32.SafeHandles.SafeFileHandle file, System.Text.StringBuilder path, uint size, uint flags);
'@

# Where a file really is on disk.
function Get-RealPath([string]$File) {
    $stream = [System.IO.File]::OpenRead($File)
    try {
        $path = New-Object System.Text.StringBuilder 1024
        $length = [MicTapInstall.Paths]::GetFinalPathNameByHandle($stream.SafeFileHandle, $path, $path.Capacity, 0)
        if ($length -eq 0 -or $length -ge $path.Capacity) { throw "GetFinalPathNameByHandle failed: $([Runtime.InteropServices.Marshal]::GetLastWin32Error())" }
        $path.ToString() -replace '^\\\\\?\\', ''
    }
    finally {
        $stream.Dispose()
    }
}

# Stops the install before it writes anything when this terminal's writes into $Folder would go to a package's copy.
function Assert-WritesLandForReal([string]$Folder) {
    New-Item -ItemType Directory -Force $Folder | Out-Null
    $probe = Join-Path $Folder ".probe-$([Guid]::NewGuid().ToString('N'))"
    Set-Content $probe 'probe'
    try {
        $real = Get-RealPath $probe
    }
    finally {
        Remove-Item $probe
    }

    if ($real -like '*\Packages\*\LocalCache\*') {
        throw "This terminal belongs to a packaged app: $Folder would really be $(Split-Path $real), which Explorer and other terminals do not see.`n" +
            "Run the install from a terminal of your own, or: powershell -File tools\run-outside.ps1 $($MyInvocation.PSCommandPath)"
    }
}
