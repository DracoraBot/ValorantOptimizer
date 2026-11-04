param(
    [string]$ProcessName = 'ValorantOptimizer',
    [string]$OutputPath = 'artifacts\screenshots\ui.png',
    [int]$WaitSeconds = 1
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

Add-Type -TypeDefinition @"
using System;
using System.Runtime.InteropServices;
public static class WindowCaptureNative
{
    [StructLayout(LayoutKind.Sequential)]
    public struct RECT { public int Left; public int Top; public int Right; public int Bottom; }

    [DllImport("user32.dll")]
    public static extern bool GetWindowRect(IntPtr hWnd, out RECT rect);

    [DllImport("user32.dll")]
    public static extern bool PrintWindow(IntPtr hWnd, IntPtr hdcBlt, uint flags);

    [DllImport("user32.dll")]
    public static extern bool SetProcessDpiAwarenessContext(IntPtr value);
}
"@

[WindowCaptureNative]::SetProcessDpiAwarenessContext([IntPtr]::new(-4)) | Out-Null

if ($WaitSeconds -gt 0) {
    Start-Sleep -Seconds $WaitSeconds
}

$process = Get-Process -Name $ProcessName |
    Where-Object { $_.MainWindowHandle -ne [IntPtr]::Zero } |
    Select-Object -First 1

if ($null -eq $process) {
    throw "No visible window found for process '$ProcessName'."
}

$handle = $process.MainWindowHandle
$rect = New-Object WindowCaptureNative+RECT
[WindowCaptureNative]::GetWindowRect($handle, [ref]$rect) | Out-Null
$width = $rect.Right - $rect.Left
$height = $rect.Bottom - $rect.Top
if ($width -le 0 -or $height -le 0) {
    throw "Window '$($process.MainWindowTitle)' has invalid bounds ${width}x${height}."
}

$bitmap = New-Object System.Drawing.Bitmap($width, $height)
$graphics = [System.Drawing.Graphics]::FromImage($bitmap)
$deviceContext = $graphics.GetHdc()
try {
    $captured = [WindowCaptureNative]::PrintWindow($handle, $deviceContext, 2)
}
finally {
    $graphics.ReleaseHdc($deviceContext)
    $graphics.Dispose()
}

if (-not $captured) {
    $bitmap.Dispose()
    throw "PrintWindow failed for '$($process.MainWindowTitle)'."
}

$directory = [System.IO.Path]::GetDirectoryName([System.IO.Path]::GetFullPath($OutputPath))
if ($directory) {
    New-Item -ItemType Directory -Force -Path $directory | Out-Null
}

$fullOutputPath = [System.IO.Path]::GetFullPath($OutputPath)
$bitmap.Save($fullOutputPath, [System.Drawing.Imaging.ImageFormat]::Png)
$bitmap.Dispose()
Write-Output "Captured $($process.MainWindowTitle) ${width}x${height} -> $fullOutputPath"
