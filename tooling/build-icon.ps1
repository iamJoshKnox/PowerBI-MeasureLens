<#
.SYNOPSIS
    Regenerates the app icon: the Cooptimize logo composited inside a magnifying-glass lens.

.DESCRIPTION
    Renders the brand SVG to a transparent PNG via headless Edge, composites it inside a
    navy magnifier (GDI+), then writes a multi-size .ico (16-256px) for the app/window icon
    and a 32px base64 data-URI (icon-base64.txt) for the External Tools ribbon button.

.PARAMETER LogoSvg
    Path to the brand SVG. Defaults to the Cooptimize icon in the repo root.
#>
[CmdletBinding()]
param(
    [string]$LogoSvg = (Join-Path $PSScriptRoot "..\Cooptimize Logo_Icon-Color.svg")
)

$ErrorActionPreference = "Stop"
Add-Type -AssemblyName System.Drawing

$repo = Resolve-Path (Join-Path $PSScriptRoot "..")
$icoPath = Join-Path $repo "src\PbiMeasureLens\appicon.ico"
$b64Path = Join-Path $PSScriptRoot "icon-base64.txt"

# 1) Render the SVG to a 512px transparent PNG with headless Edge.
$edge = @(
    "C:\Program Files (x86)\Microsoft\Edge\Application\msedge.exe",
    "C:\Program Files\Microsoft\Edge\Application\msedge.exe"
) | Where-Object { Test-Path $_ } | Select-Object -First 1
if (-not $edge) { throw "Microsoft Edge not found (needed to rasterize the SVG)." }

$logoPng = Join-Path $env:TEMP "cooptimize_logo512.png"
Remove-Item $logoPng -ErrorAction SilentlyContinue
$svgUri = "file:///" + ((Resolve-Path $LogoSvg).Path -replace '\\','/')
$edgeArgs = @(
    "--headless=new", "--disable-gpu", "--hide-scrollbars", "--force-device-scale-factor=1",
    "--default-background-color=00000000", "--screenshot=$logoPng", "--window-size=512,512", $svgUri
)
# Call operator splats each arg correctly (Start-Process mangles spaces in PS 5.1). Relax
# ErrorActionPreference so Edge's stderr status line isn't promoted to a terminating error.
$prev = $ErrorActionPreference; $ErrorActionPreference = 'Continue'
& $edge @edgeArgs 2>$null | Out-Null
$ErrorActionPreference = $prev
Start-Sleep -Seconds 1
if (-not (Test-Path $logoPng)) { throw "SVG render failed." }

# 2) Composite the logo inside a magnifying-glass lens at 512px.
function New-Design {
    param([string]$logoFile)
    $logo = [System.Drawing.Image]::FromFile($logoFile)
    $S = 512
    $bmp = New-Object System.Drawing.Bitmap($S, $S)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $g.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
    $g.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
    $g.Clear([System.Drawing.Color]::Transparent)

    $navy = [System.Drawing.ColorTranslator]::FromHtml("#00416b")
    $cx = 205.0; $cy = 205.0; $outerR = 178.0; $rimThick = 24.0
    $innerR = $outerR - $rimThick; $fillR = $innerR - 1

    $gp = New-Object System.Drawing.Drawing2D.GraphicsPath
    $gp.AddEllipse(($cx-$fillR), ($cy-$fillR), (2*$fillR), (2*$fillR))
    $g.FillPath((New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::White)), $gp)

    $g.SetClip($gp)
    $ls = 2*$fillR*1.10
    $g.DrawImage($logo, [single]($cx-$ls/2), [single]($cy-$ls/2), [single]$ls, [single]$ls)
    $g.ResetClip()

    $pen = New-Object System.Drawing.Pen($navy, 38)
    $pen.StartCap = [System.Drawing.Drawing2D.LineCap]::Round
    $pen.EndCap = [System.Drawing.Drawing2D.LineCap]::Round
    $a = [Math]::PI/4
    $g.DrawLine($pen, [single]($cx+$outerR*[Math]::Cos($a)), [single]($cy+$outerR*[Math]::Sin($a)), [single]470, [single]470)

    $rimPen = New-Object System.Drawing.Pen($navy, $rimThick)
    $rimMid = $outerR - $rimThick/2
    $g.DrawEllipse($rimPen, [single]($cx-$rimMid), [single]($cy-$rimMid), [single](2*$rimMid), [single](2*$rimMid))

    $g.Dispose(); $logo.Dispose()
    return $bmp
}

$design = New-Design $logoPng

# 3) Build PNG frames at each size.
function Get-PngBytes {
    param($srcBmp, [int]$size)
    $b = New-Object System.Drawing.Bitmap($size, $size)
    $g = [System.Drawing.Graphics]::FromImage($b)
    $g.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
    $g.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
    $g.Clear([System.Drawing.Color]::Transparent)
    $g.DrawImage($srcBmp, 0, 0, $size, $size)
    $g.Dispose()
    $ms = New-Object System.IO.MemoryStream
    $b.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
    $b.Dispose()
    return ,$ms.ToArray()
}

$sizes = 16,32,48,64,128,256
$frames = @{}
foreach ($s in $sizes) { $frames[$s] = Get-PngBytes $design $s }

# 4) Write a multi-size .ico (PNG-compressed frames; supported on Windows Vista+).
$ms = New-Object System.IO.MemoryStream
$bw = New-Object System.IO.BinaryWriter($ms)
$bw.Write([uint16]0); $bw.Write([uint16]1); $bw.Write([uint16]$sizes.Count)
$offset = 6 + 16*$sizes.Count
foreach ($s in $sizes) {
    $len = $frames[$s].Length
    $dim = if ($s -ge 256) { 0 } else { $s }
    $bw.Write([byte]$dim); $bw.Write([byte]$dim); $bw.Write([byte]0); $bw.Write([byte]0)
    $bw.Write([uint16]1); $bw.Write([uint16]32); $bw.Write([uint32]$len); $bw.Write([uint32]$offset)
    $offset += $len
}
foreach ($s in $sizes) { $bw.Write($frames[$s]) }
$bw.Flush()
[System.IO.File]::WriteAllBytes($icoPath, $ms.ToArray())
$bw.Dispose()

# 5) 32px base64 data-URI for the ribbon button.
$b64 = "data:image/png;base64," + [Convert]::ToBase64String($frames[32])
[System.IO.File]::WriteAllText($b64Path, $b64)

$design.Dispose()
Write-Host "Wrote $icoPath ($((Get-Item $icoPath).Length) bytes) and $b64Path"
