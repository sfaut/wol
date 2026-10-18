<#
.SYNOPSIS
    Builds the GUI icons (.ico) from the SVG sources in design/assets.

.DESCRIPTION
    Renders every SVG at each size with Microsoft Edge in headless mode (transparent
    background), then packs the PNG images into one .ico per icon (PNG-compressed entries,
    supported since Windows Vista). Run it again after changing an SVG source; the .ico files
    are committed, so building the GUI does not need Edge.

    Sizes follow design/specs/app-icon.md: the executable icon uses the hand-hinted 16 px
    drawing up to 24 px and the master drawing above; notification-area icons are 16 to 32 px.
#>
[CmdletBinding()]
param(
    # Folder of the SVG sources.
    [string]$Source = (Join-Path $PSScriptRoot '..\..\design\assets'),

    # Folder receiving the .ico files.
    [string]$Output = (Join-Path $PSScriptRoot '..\src\Wol.Desktop\Assets')
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

$edge = @(
    "${env:ProgramFiles(x86)}\Microsoft\Edge\Application\msedge.exe",
    "$env:ProgramFiles\Microsoft\Edge\Application\msedge.exe"
) | Where-Object { Test-Path $_ } | Select-Object -First 1
if (-not $edge) {
    throw 'Microsoft Edge is needed to render the SVG sources.'
}

$work = Join-Path ([System.IO.Path]::GetTempPath()) "wol-icons-$PID"
New-Item -ItemType Directory -Force $work | Out-Null
New-Item -ItemType Directory -Force $Output | Out-Null

# Renders an SVG file as a size × size PNG image and returns its bytes.
function Render([string]$svg, [int]$size) {
    $page = Join-Path $work 'page.html'
    $shot = Join-Path $work 'shot.png'
    $uri = ([System.Uri](Resolve-Path $svg).Path).AbsoluteUri
    Set-Content -Encoding utf8 $page "<!doctype html><html><body style='margin:0;background:transparent'><img src='$uri' width='$size' height='$size' style='display:block'></body></html>"
    Remove-Item $shot -ErrorAction SilentlyContinue
    $arguments = @(
        '--headless', '--disable-gpu', '--hide-scrollbars', '--force-device-scale-factor=1',
        '--default-background-color=00000000', "--user-data-dir=$work\profile",
        '--window-size=256,256', "--screenshot=$shot", ([System.Uri]$page).AbsoluteUri
    )
    Start-Process -FilePath $edge -ArgumentList $arguments -Wait -WindowStyle Hidden
    if (-not (Test-Path $shot)) {
        throw "Edge did not render $svg"
    }

    # Crops the top-left size × size square.
    $sheet = [System.Drawing.Bitmap]::FromFile($shot)
    try {
        $image = $sheet.Clone((New-Object System.Drawing.Rectangle 0, 0, $size, $size), [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
        $stream = New-Object System.IO.MemoryStream
        $image.Save($stream, [System.Drawing.Imaging.ImageFormat]::Png)
        $image.Dispose()
        return , $stream.ToArray()
    }
    finally {
        $sheet.Dispose()
    }
}

# Encodes a PNG image as an icon DIB: 32-bit BGRA rows bottom-up, then an empty AND mask
# (transparency comes from the alpha channel).
function ConvertTo-Dib([byte[]]$png, [int]$size) {
    $image = New-Object System.Drawing.Bitmap (New-Object System.IO.MemoryStream (, $png))
    $stream = New-Object System.IO.MemoryStream
    $writer = New-Object System.IO.BinaryWriter $stream
    $writer.Write([uint32]40)
    $writer.Write([int32]$size)
    $writer.Write([int32]($size * 2))
    $writer.Write([uint16]1)
    $writer.Write([uint16]32)
    $writer.Write([uint32]0)
    $writer.Write([uint32]0)
    $writer.Write([int32]0)
    $writer.Write([int32]0)
    $writer.Write([uint32]0)
    $writer.Write([uint32]0)
    for ($y = $size - 1; $y -ge 0; $y--) {
        for ($x = 0; $x -lt $size; $x++) {
            $pixel = $image.GetPixel($x, $y)
            $writer.Write([byte]$pixel.B)
            $writer.Write([byte]$pixel.G)
            $writer.Write([byte]$pixel.R)
            $writer.Write([byte]$pixel.A)
        }
    }
    $maskRow = [Math]::Ceiling($size / 32) * 4
    $writer.Write((New-Object byte[] ($maskRow * $size)))
    $writer.Flush()
    $image.Dispose()
    return , $stream.ToArray()
}

# Writes an .ico file from (size, PNG bytes) entries: DIB below 256 px, PNG at 256 px.
function Write-Ico([string]$path, $entries) {
    $entries = foreach ($entry in $entries) {
        $data = if ($entry.Size -ge 256) { $entry.Png } else { ConvertTo-Dib $entry.Png $entry.Size }
        [pscustomobject]@{ Size = $entry.Size; Png = $data }
    }
    $stream = New-Object System.IO.MemoryStream
    $writer = New-Object System.IO.BinaryWriter $stream
    $writer.Write([uint16]0)
    $writer.Write([uint16]1)
    $writer.Write([uint16]$entries.Count)
    $offset = 6 + 16 * $entries.Count
    foreach ($entry in $entries) {
        $side = if ($entry.Size -ge 256) { 0 } else { $entry.Size }
        $writer.Write([byte]$side)
        $writer.Write([byte]$side)
        $writer.Write([byte]0)
        $writer.Write([byte]0)
        $writer.Write([uint16]1)
        $writer.Write([uint16]32)
        $writer.Write([uint32]$entry.Png.Length)
        $writer.Write([uint32]$offset)
        $offset += $entry.Png.Length
    }
    foreach ($entry in $entries) {
        $writer.Write([byte[]]$entry.Png)
    }
    $writer.Flush()
    [System.IO.File]::WriteAllBytes($path, $stream.ToArray())
    Write-Host "$path ($($entries.Count) sizes)"
}

try {
    # Executable and window icon.
    $entries = foreach ($size in 16, 20, 24, 32, 40, 48, 64, 256) {
        $svg = if ($size -le 24) { 'app-icon-16.svg' } else { 'app-icon.svg' }
        [pscustomobject]@{ Size = $size; Png = (Render (Join-Path $Source $svg) $size) }
    }
    Write-Ico (Join-Path $Output 'wol-gui.ico') $entries

    # Notification-area icons: status × taskbar theme.
    foreach ($status in 'online', 'offline', 'error') {
        foreach ($theme in 'light', 'dark') {
            $svg = Join-Path $Source "tray-$status-$theme-taskbar.svg"
            $entries = foreach ($size in 16, 20, 24, 32) {
                [pscustomobject]@{ Size = $size; Png = (Render $svg $size) }
            }
            Write-Ico (Join-Path $Output "tray-$status-$theme.ico") $entries
        }
    }
}
finally {
    Remove-Item -Recurse -Force $work -ErrorAction SilentlyContinue
}
