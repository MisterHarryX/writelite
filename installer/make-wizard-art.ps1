# Generates the WriteLite installer wizard artwork as 24-bit BMPs.
#
# Inno Setup only accepts BMP for WizardImageFile / WizardSmallImageFile, so the
# branding is drawn here with GDI+ rather than shipped as PNG. The palette is
# the site's: near-black ground, warm off-white type, one orange accent.

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

$outDir = Split-Path -Parent $MyInvocation.MyCommand.Path

$BG      = [System.Drawing.Color]::FromArgb(8, 8, 8)
$PANEL   = [System.Drawing.Color]::FromArgb(19, 19, 19)
$TEXT    = [System.Drawing.Color]::FromArgb(241, 239, 236)
$SUB     = [System.Drawing.Color]::FromArgb(160, 157, 152)
$MUTED   = [System.Drawing.Color]::FromArgb(101, 98, 94)
$ORANGE  = [System.Drawing.Color]::FromArgb(236, 108, 8)

function New-Canvas([int]$w, [int]$h) {
    $bmp = New-Object System.Drawing.Bitmap($w, $h, [System.Drawing.Imaging.PixelFormat]::Format24bppRgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode     = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $g.TextRenderingHint = [System.Drawing.Text.TextRenderingHint]::ClearTypeGridFit
    $g.Clear($BG)
    return @{ Bitmap = $bmp; Graphics = $g }
}

function Save-Bmp($canvas, [string]$name) {
    $path = Join-Path $outDir $name
    $canvas.Graphics.Dispose()
    $canvas.Bitmap.Save($path, [System.Drawing.Imaging.ImageFormat]::Bmp)
    $canvas.Bitmap.Dispose()
    $size = (Get-Item $path).Length
    Write-Output ("{0,-28} {1,5}x{2,-5} {3,9:N0} bytes" -f $name, $canvas.W, $canvas.H, $size)
}

# ---------------------------------------------------------------- large panel
# Shown down the left of the welcome and finished pages.
function New-WizardImage([int]$w, [int]$h, [string]$name) {
    $c = New-Canvas $w $h
    $g = $c.Graphics
    $scale = $h / 892.0

    # A single warm light from the top, the same lamp the website uses.
    $glowRect = New-Object System.Drawing.Rectangle(0, 0, $w, [int](420 * $scale))
    $glow = New-Object System.Drawing.Drawing2D.LinearGradientBrush(
        $glowRect,
        [System.Drawing.Color]::FromArgb(38, 236, 108, 8),
        $BG,
        [System.Drawing.Drawing2D.LinearGradientMode]::Vertical)
    $g.FillRectangle($glow, $glowRect)
    $glow.Dispose()

    # Faint modular grid — the typesetter's baseline.
    $gridPen = New-Object System.Drawing.Pen([System.Drawing.Color]::FromArgb(14, 255, 255, 255), 1)
    $step = [int](44 * $scale)
    for ($x = $step; $x -lt $w; $x += $step) { $g.DrawLine($gridPen, $x, 0, $x, $h) }
    for ($y = $step; $y -lt $h; $y += $step) { $g.DrawLine($gridPen, 0, $y, $w, $y) }
    $gridPen.Dispose()

    $margin = [int](46 * $scale)

    # Orange margin rule.
    $accent = New-Object System.Drawing.SolidBrush($ORANGE)
    $g.FillRectangle($accent, $margin, [int](150 * $scale), [int](3 * $scale), [int](84 * $scale))

    # Wordmark.
    $fName = New-Object System.Drawing.Font("Segoe UI Light", (52 * $scale), [System.Drawing.FontStyle]::Regular, [System.Drawing.GraphicsUnit]::Pixel)
    $bText = New-Object System.Drawing.SolidBrush($TEXT)
    $g.DrawString("WriteLite", $fName, $bText, ($margin + [int](22 * $scale)), [int](148 * $scale))

    # Strapline.
    $fSub = New-Object System.Drawing.Font("Segoe UI", (17 * $scale), [System.Drawing.FontStyle]::Regular, [System.Drawing.GraphicsUnit]::Pixel)
    $bSub = New-Object System.Drawing.SolidBrush($SUB)
    $g.DrawString("Локальный помощник", $fSub, $bSub, ($margin + [int](24 * $scale)), [int](226 * $scale))
    $g.DrawString("для работы с текстом", $fSub, $bSub, ($margin + [int](24 * $scale)), [int](252 * $scale))

    # --- the motif: a line of prose with one word marked by the editor ---
    $cardTop = [int](430 * $scale)
    $cardH   = [int](150 * $scale)
    $cardW   = $w - ($margin * 2)
    $bPanel = New-Object System.Drawing.SolidBrush($PANEL)
    $g.FillRectangle($bPanel, $margin, $cardTop, $cardW, $cardH)
    $linePen = New-Object System.Drawing.Pen([System.Drawing.Color]::FromArgb(26, 255, 255, 255), 1)
    $g.DrawRectangle($linePen, $margin, $cardTop, $cardW, $cardH)

    $fBody = New-Object System.Drawing.Font("Georgia", (20 * $scale), [System.Drawing.FontStyle]::Regular, [System.Drawing.GraphicsUnit]::Pixel)
    $bBody = New-Object System.Drawing.SolidBrush([System.Drawing.Color]::FromArgb(216, 211, 206))
    $tx = $margin + [int](22 * $scale)
    $ty = $cardTop + [int](34 * $scale)
    $part1 = "Проблема "
    $word  = "становиться"
    $g.DrawString($part1, $fBody, $bBody, $tx, $ty)
    $w1 = $g.MeasureString($part1, $fBody).Width
    $g.DrawString($word, $fBody, $bBody, ($tx + $w1 - (4 * $scale)), $ty)
    $wWord = $g.MeasureString($word, $fBody).Width

    # Wavy underline, hand-drawn the way the app draws it.
    $wavePen = New-Object System.Drawing.Pen($ORANGE, (2 * $scale))
    $ux = $tx + $w1 - (4 * $scale)
    $uy = $ty + $g.MeasureString($word, $fBody).Height - (4 * $scale)
    $amp = 2.2 * $scale
    $pts = New-Object System.Collections.Generic.List[System.Drawing.PointF]
    for ($i = 0; $i -le [int]($wWord - 8); $i += 2) {
        $yy = $uy + [math]::Sin($i / (3.0 * $scale)) * $amp
        $pts.Add((New-Object System.Drawing.PointF(($ux + $i), $yy)))
    }
    if ($pts.Count -gt 1) { $g.DrawLines($wavePen, $pts.ToArray()) }
    $wavePen.Dispose()

    # The correction, as the app would present it.
    $fMono = New-Object System.Drawing.Font("Consolas", (16 * $scale), [System.Drawing.FontStyle]::Regular, [System.Drawing.GraphicsUnit]::Pixel)
    $bOrange = New-Object System.Drawing.SolidBrush($ORANGE)
    $bMuted = New-Object System.Drawing.SolidBrush($MUTED)
    $ay = $cardTop + $cardH - [int](46 * $scale)
    $g.DrawString("становиться", $fMono, $bMuted, $tx, $ay)
    $wOld = $g.MeasureString("становиться", $fMono).Width
    $g.DrawString("→", $fMono, $bOrange, ($tx + $wOld + (6 * $scale)), $ay)
    $wArrow = $g.MeasureString("→", $fMono).Width
    $g.DrawString("становится", $fMono, $bText, ($tx + $wOld + $wArrow + (14 * $scale)), $ay)

    # Footer: the three promises, set as a folio.
    $fFoot = New-Object System.Drawing.Font("Segoe UI", (14 * $scale), [System.Drawing.FontStyle]::Regular, [System.Drawing.GraphicsUnit]::Pixel)
    $fy = $h - [int](130 * $scale)
    foreach ($line in @("Работает локально", "Без регистрации", "Данные остаются на компьютере")) {
        $g.FillRectangle($accent, $margin, ($fy + [int](7 * $scale)), [int](10 * $scale), [int](2 * $scale))
        $g.DrawString($line, $fFoot, $bSub, ($margin + [int](20 * $scale)), $fy)
        $fy += [int](26 * $scale)
    }

    foreach ($d in @($accent, $bText, $bSub, $bPanel, $bBody, $bOrange, $bMuted, $fName, $fSub, $fBody, $fMono, $fFoot, $linePen)) { $d.Dispose() }

    $c.W = $w; $c.H = $h
    Save-Bmp $c $name
}

# ---------------------------------------------------------------- small badge
# Shown top-right on the inner pages.
function New-WizardSmallImage([int]$w, [int]$h, [string]$name) {
    $c = New-Canvas $w $h
    $g = $c.Graphics
    $scale = $h / 140.0

    $box = [int](74 * $scale)
    $x = [int](($w - $box) / 2)
    $y = [int](($h - $box) / 2)

    $bPanel = New-Object System.Drawing.SolidBrush($PANEL)
    $g.FillRectangle($bPanel, $x, $y, $box, $box)
    $pen = New-Object System.Drawing.Pen([System.Drawing.Color]::FromArgb(60, 236, 108, 8), (1.5 * $scale))
    $g.DrawRectangle($pen, $x, $y, $box, $box)

    $f = New-Object System.Drawing.Font("Georgia", (40 * $scale), [System.Drawing.FontStyle]::Regular, [System.Drawing.GraphicsUnit]::Pixel)
    $b = New-Object System.Drawing.SolidBrush($TEXT)
    $sf = New-Object System.Drawing.StringFormat
    $sf.Alignment = [System.Drawing.StringAlignment]::Center
    $sf.LineAlignment = [System.Drawing.StringAlignment]::Center
    $rect = New-Object System.Drawing.RectangleF($x, $y, $box, $box)
    $g.DrawString("W", $f, $b, $rect, $sf)

    # The live caret parked at the letter's baseline.
    $bO = New-Object System.Drawing.SolidBrush($ORANGE)
    $g.FillRectangle($bO, ($x + $box - [int](14 * $scale)), ($y + $box - [int](22 * $scale)), [int](2 * $scale), [int](12 * $scale))

    foreach ($d in @($bPanel, $pen, $f, $b, $bO, $sf)) { $d.Dispose() }

    $c.W = $w; $c.H = $h
    Save-Bmp $c $name
}

# Inno Setup picks the closest match for the user's DPI, so ship a ladder.
New-WizardImage 164 314 "wizard-164x314.bmp"
New-WizardImage 192 386 "wizard-192x386.bmp"
New-WizardImage 256 459 "wizard-256x459.bmp"
New-WizardImage 384 689 "wizard-384x689.bmp"
New-WizardImage 497 892 "wizard-497x892.bmp"

New-WizardSmallImage 55 58   "wizard-small-55x58.bmp"
New-WizardSmallImage 92 97   "wizard-small-92x97.bmp"
New-WizardSmallImage 110 116 "wizard-small-110x116.bmp"
New-WizardSmallImage 138 140 "wizard-small-138x140.bmp"

Write-Output "wizard art written to $outDir"
