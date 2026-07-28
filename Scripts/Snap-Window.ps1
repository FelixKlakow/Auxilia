<#
.SYNOPSIS
Snapshots one application window to a PNG — without changing focus or z-order.

.DESCRIPTION
Finds the target window by process name (and optional title fragment) and renders it via
PrintWindow with PW_RENDERFULLCONTENT, which captures WPF/DirectX-composed content even
when the window is in the background or partially occluded. Never captures the desktop
and never calls SetForegroundWindow.

.EXAMPLE
./Snap-Window.ps1 -ProcessName "Auxilia Steering" -OutPath steering.png

.EXAMPLE
./Snap-Window.ps1 -ProcessName AgentView.Wpf.Demo -TitleLike "Demo" -OutPath demo.png
#>
param(
    [Parameter(Mandatory)] [string] $ProcessName,
    [string] $TitleLike,
    [Parameter(Mandatory)] [string] $OutPath
)

$ErrorActionPreference = 'Stop'

Add-Type -AssemblyName System.Drawing
if (-not ('AuxiliaSnap.Native' -as [type])) {
    Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
namespace AuxiliaSnap {
  public static class Native {
    [DllImport("user32.dll")] public static extern bool PrintWindow(IntPtr hwnd, IntPtr hdc, uint flags);
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr hwnd, out RECT rect);
    [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
    [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }
  }
}
'@
}

# Without DPI awareness GetWindowRect returns scaled coordinates and the capture is blurry/cropped.
[AuxiliaSnap.Native]::SetProcessDPIAware() | Out-Null

$candidates = Get-Process -Name $ProcessName -ErrorAction SilentlyContinue |
    Where-Object { $_.MainWindowHandle -ne [IntPtr]::Zero }
if ($TitleLike) {
    $candidates = $candidates | Where-Object { $_.MainWindowTitle -like "*$TitleLike*" }
}
$process = $candidates | Select-Object -First 1
if (-not $process) {
    throw "No window found for process '$ProcessName'$(if ($TitleLike) { " with title like '*$TitleLike*'" })."
}

$rect = New-Object AuxiliaSnap.Native+RECT
[AuxiliaSnap.Native]::GetWindowRect($process.MainWindowHandle, [ref] $rect) | Out-Null
$width = $rect.Right - $rect.Left
$height = $rect.Bottom - $rect.Top
if ($width -le 0 -or $height -le 0) {
    throw "Window of '$ProcessName' has no size (minimized?)."
}

$bitmap = New-Object System.Drawing.Bitmap($width, $height)
$graphics = [System.Drawing.Graphics]::FromImage($bitmap)
try {
    $hdc = $graphics.GetHdc()
    try {
        # 2 = PW_RENDERFULLCONTENT: include DirectX/WPF-composed content, works while backgrounded.
        if (-not [AuxiliaSnap.Native]::PrintWindow($process.MainWindowHandle, $hdc, 2)) {
            throw "PrintWindow failed for '$ProcessName'."
        }
    }
    finally { $graphics.ReleaseHdc($hdc) }

    $directory = Split-Path -Parent ([System.IO.Path]::GetFullPath($OutPath))
    if ($directory -and -not (Test-Path $directory)) {
        New-Item -ItemType Directory -Force $directory | Out-Null
    }
    $bitmap.Save($OutPath, [System.Drawing.Imaging.ImageFormat]::Png)
}
finally {
    $graphics.Dispose()
    $bitmap.Dispose()
}

Write-Output "Saved $([System.IO.Path]::GetFullPath($OutPath)) (${width}x${height}, '$($process.MainWindowTitle)')"
