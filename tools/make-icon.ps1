# Gera UsageCockpit/Assets/app.ico (mostrador com ponteiro) em 16, 24, 32, 48, 64 e 256 px.
# Uso: powershell -ExecutionPolicy Bypass -File tools/make-icon.ps1
Add-Type -AssemblyName System.Drawing

$out = Join-Path $PSScriptRoot "..\UsageCockpit\Assets\app.ico"
New-Item -ItemType Directory -Force (Split-Path $out) | Out-Null

function Render([int]$size) {
    $bmp = New-Object System.Drawing.Bitmap $size, $size
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = 'AntiAlias'
    $s = $size / 32.0
    $bg = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(0x0F, 0x12, 0x16))
    $g.FillEllipse($bg, 0.5 * $s, 0.5 * $s, 31 * $s, 31 * $s)
    $w = [Math]::Max(1.5, 3.6 * $s)
    $track = New-Object System.Drawing.Pen ([System.Drawing.Color]::FromArgb(0x3A, 0x40, 0x48)), $w
    $g.DrawArc($track, 5 * $s, 5 * $s, 22 * $s, 22 * $s, 135, 270)
    $arc = New-Object System.Drawing.Pen ([System.Drawing.Color]::FromArgb(0xD9, 0x77, 0x57)), $w
    $g.DrawArc($arc, 5 * $s, 5 * $s, 22 * $s, 22 * $s, 135, 130)
    $arc2 = New-Object System.Drawing.Pen ([System.Drawing.Color]::FromArgb(0x10, 0xA3, 0x7F)), $w
    $g.DrawArc($arc2, 5 * $s, 5 * $s, 22 * $s, 22 * $s, 268, 50)
    $needle = New-Object System.Drawing.Pen ([System.Drawing.Color]::WhiteSmoke), ([Math]::Max(1.2, 2.4 * $s))
    $needle.StartCap = 'Round'; $needle.EndCap = 'Round'
    $g.DrawLine($needle, 16 * $s, 16 * $s, 23.5 * $s, 9 * $s)
    $hub = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::WhiteSmoke)
    $g.FillEllipse($hub, 14 * $s, 14 * $s, 4 * $s, 4 * $s)
    $g.Dispose()
    $ms = New-Object System.IO.MemoryStream
    $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
    $bmp.Dispose()
    return , $ms.ToArray()
}

# Formato ICO com imagens PNG embutidas (suportado desde o Windows Vista).
$sizes = 16, 24, 32, 48, 64, 256
$images = foreach ($sz in $sizes) { , (Render $sz) }
$fs = [System.IO.File]::Create($out)
$bw = New-Object System.IO.BinaryWriter $fs
$bw.Write([UInt16]0); $bw.Write([UInt16]1); $bw.Write([UInt16]$sizes.Count)
$offset = 6 + 16 * $sizes.Count
for ($i = 0; $i -lt $sizes.Count; $i++) {
    $sz = $sizes[$i]; $len = $images[$i].Length
    $dim = if ($sz -ge 256) { 0 } else { $sz }
    $bw.Write([byte]$dim); $bw.Write([byte]$dim); $bw.Write([byte]0); $bw.Write([byte]0)
    $bw.Write([UInt16]1); $bw.Write([UInt16]32); $bw.Write([UInt32]$len); $bw.Write([UInt32]$offset)
    $offset += $len
}
foreach ($img in $images) { $bw.Write($img) }
$bw.Close()
Write-Host "Icone gerado em $out"
