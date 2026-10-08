<#
.SYNOPSIS
    Generates the Trawler plugin thumbnail (flat, two-tone, 600x338) — matches the Reel style.

.DESCRIPTION
    Original artwork for Trawler: a fishing line descends from the top into an orange hook
    that catches a cream film strip; the word TRAWLER lives inside the strip.
    Palette/typography identical to Reel for sibling consistency:
      deep indigo #171B33, cream #F2EDE4, accent #E8862B, Segoe UI.
    Output: src/Trawler/thumb.jpg (embedded as Trawler.thumb.jpg).

.EXAMPLE
    powershell -File tools/make-thumb.ps1
#>
$ErrorActionPreference = 'Stop'

Add-Type -AssemblyName System.Drawing

$w = 600
$h = 338
$outDir = Join-Path (Split-Path -Parent $PSScriptRoot) 'src/Trawler'
New-Item -ItemType Directory -Force -Path $outDir | Out-Null

$bg = [System.Drawing.Color]::FromArgb(23, 27, 51)     # #171B33 deep indigo (same as Reel)
$fg = [System.Drawing.Color]::FromArgb(242, 237, 228)   # #F2EDE4 cream
$ac = [System.Drawing.Color]::FromArgb(232, 134, 43)    # #E8862B warm accent

$bmp = New-Object System.Drawing.Bitmap($w, $h)
$g = [System.Drawing.Graphics]::FromImage($bmp)
$g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
$g.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
$g.TextRenderingHint = [System.Drawing.Text.TextRenderingHint]::AntiAliasGridFit

$bgBrush = New-Object System.Drawing.SolidBrush($bg)
$fgBrush = New-Object System.Drawing.SolidBrush($fg)
$acBrush = New-Object System.Drawing.SolidBrush($ac)
$penFg6  = New-Object System.Drawing.Pen($fg, 6)    # fishing line (cream)
$penAc16 = New-Object System.Drawing.Pen($ac, 16)   # hook (accent)
$penAc5  = New-Object System.Drawing.Pen($ac, 5)    # caption rule

function New-RoundedRect([float]$x, [float]$y, [float]$w2, [float]$h2, [float]$r) {
    $p = New-Object System.Drawing.Drawing2D.GraphicsPath
    $p.AddArc($x, $y, 2*$r, 2*$r, 180, 90)
    $p.AddArc($x+$w2-2*$r, $y, 2*$r, 2*$r, 270, 90)
    $p.AddArc($x+$w2-2*$r, $y+$h2-2*$r, 2*$r, 2*$r, 0, 90)
    $p.AddArc($x, $y+$h2-2*$r, 2*$r, 2*$r, 90, 90)
    $p.CloseFigure()
    return $p
}

try {
    $g.Clear($bg)

    # ================= FILM STRIP (holds the word) =================
    $sx = 70; $sy = 142; $sw = 460; $sh = 118   # strip rect
    $strip = New-RoundedRect $sx $sy $sw $sh 12
    $g.FillPath($fgBrush, $strip)

    # sprocket holes (indigo) — two rows
    $holeH = 18; $holeW = 26; $pitch = 38
    $topY = $sy + 10
    $botY = $sy + $sh - 10 - $holeH
    $n = [Math]::Floor(($sw - 28 + $pitch) / $pitch)
    for ($i = 0; $i -lt $n; $i++) {
        $hx = $sx + 14 + $i * $pitch
        if (($hx + $holeW) -gt ($sx + $sw - 14)) { continue }
        $g.FillPath($bgBrush, (New-RoundedRect $hx $topY $holeW $holeH 4))
        $g.FillPath($bgBrush, (New-RoundedRect $hx $botY $holeW $holeH 4))
    }

    # film frame dividers framing the word zone
    $penBg5 = New-Object System.Drawing.Pen($bg, 5)
    $g.DrawLine($penBg5, ($sx + 46), ($sy + 36), ($sx + 46), ($sy + $sh - 36))
    $g.DrawLine($penBg5, ($sx + $sw - 46), ($sy + 36), ($sx + $sw - 46), ($sy + $sh - 36))

    # ================= THE WORD: TRAWLER =================
    $targetW = 350                       # keep clear of dividers
    $cxStrip = $sx + $sw / 2             # 300
    $fs = 62; $titleFont = $null; $mW = 0
    for ($it = 0; $it -lt 8; $it++) {
        if ($titleFont) { $titleFont.Dispose() }
        $titleFont = New-Object System.Drawing.Font('Segoe UI', $fs, [System.Drawing.FontStyle]::Bold, [System.Drawing.GraphicsUnit]::Pixel)
        $mW = $g.MeasureString('TRAWLER', $titleFont).Width
        if ($mW -le $targetW) { break }
        $fs = [Math]::Floor($fs * ($targetW / $mW))
        if ($fs -lt 28) { $fs = 28; break }
    }
    $titleRect = New-Object System.Drawing.RectangleF -ArgumentList ($cxStrip - $targetW/2), ($sy + 34), $targetW, 56
    $sf = New-Object System.Drawing.StringFormat
    $sf.Alignment = [System.Drawing.StringAlignment]::Center
    $sf.LineAlignment = [System.Drawing.StringAlignment]::Center
    $g.DrawString('TRAWLER', $titleFont, $bgBrush, $titleRect, $sf)
    "title fitted: width=$([Math]::Round($mW)) font=$fs"

    # ================= LINE + HOOK (from top, catching the strip) =================
    # fishing line: cream, from top edge down to the hook
    $penFg6.StartCap = [System.Drawing.Drawing2D.LineCap]::Round
    $g.DrawLine($penFg6, $cxStrip, 0, $cxStrip, 100)

    # hook: accent, dips over the strip's top edge, curls up (classic J) above the word zone
    $penAc16.StartCap = [System.Drawing.Drawing2D.LineCap]::Round
    $penAc16.EndCap = [System.Drawing.Drawing2D.LineCap]::Round
    $hookPts = [System.Drawing.PointF[]]@(
        (New-Object System.Drawing.PointF($cxStrip, 96)),
        (New-Object System.Drawing.PointF($cxStrip, 138)),
        (New-Object System.Drawing.PointF($cxStrip, 158)),
        (New-Object System.Drawing.PointF(($cxStrip + 14), 170)),
        (New-Object System.Drawing.PointF(($cxStrip + 34), 168)),
        (New-Object System.Drawing.PointF(($cxStrip + 44), 154))   # tip curls back up
    )
    $g.DrawCurve($penAc16, $hookPts)
    # barb tick at the tip
    $g.DrawLine($penAc16, ($cxStrip + 44), 154, ($cxStrip + 34), 146)

    # ================= caption (mirrors Reel's subtitle + rule) =================
    $subFont = New-Object System.Drawing.Font('Segoe UI', 22, [System.Drawing.FontStyle]::Regular, [System.Drawing.GraphicsUnit]::Pixel)
    try {
        $cap = 'MOVIE TRAILERS'
        $capW = $g.MeasureString($cap, $subFont).Width
        $capRect = New-Object System.Drawing.RectangleF -ArgumentList ($cxStrip - 200), 276, 400, 30
        $sfCap = New-Object System.Drawing.StringFormat
        $sfCap.Alignment = [System.Drawing.StringAlignment]::Center
        $g.DrawString($cap, $subFont, $acBrush, $capRect, $sfCap)

        # accent rule under the caption, matched to its width (like Reel)
        $ruleW = [Math]::Max(200, [Math]::Min($capW + 40, 320))
        $g.FillRectangle($acBrush, ($cxStrip - $ruleW / 2), 310, $ruleW, 5)
    }
    finally {
        $titleFont.Dispose()
        $subFont.Dispose()
    }

    # save as JPEG (plugin embeds thumb.jpg)
    $jpgCodec = [System.Drawing.Imaging.ImageCodecInfo]::GetImageEncoders() | Where-Object { $_.MimeType -eq 'image/jpeg' }
    $ep = New-Object System.Drawing.Imaging.EncoderParameters(1)
    $ep.Param[0] = New-Object System.Drawing.Imaging.EncoderParameter([System.Drawing.Imaging.Encoder]::Quality, [long]90)
    $bmp.Save((Join-Path $outDir 'thumb.jpg'), $jpgCodec, $ep)

    Write-Host "wrote thumb.jpg ($w x $h) to $outDir"
}
finally {
    $g.Dispose()
    $bmp.Dispose()
    $bgBrush.Dispose()
    $fgBrush.Dispose()
    $acBrush.Dispose()
    $penFg6.Dispose()
    $penAc16.Dispose()
    $penAc5.Dispose()
}
