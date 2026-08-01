param(
    [string]$OutputPath = (Join-Path $PSScriptRoot '..\src\AIUsageMonitor\Resources\app.ico')
)

Add-Type -AssemblyName System.Drawing

$directory = Split-Path -Parent $OutputPath
New-Item -ItemType Directory -Force -Path $directory | Out-Null

$size = 64
$bitmap = [System.Drawing.Bitmap]::new($size, $size, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
$graphics = [System.Drawing.Graphics]::FromImage($bitmap)
$graphics.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
$graphics.Clear([System.Drawing.Color]::Transparent)

$centerX = $size / 2.0
$centerY = $size * 0.60
$radius = $size * 0.34
$stroke = [Math]::Max(2, $size * 0.135)
$rectangle = [System.Drawing.RectangleF]::new($centerX - $radius, $centerY - $radius, $radius * 2, $radius * 2)

function New-GaugePen([System.Drawing.Color]$color) {
    $pen = [System.Drawing.Pen]::new($color, $stroke)
    $pen.StartCap = [System.Drawing.Drawing2D.LineCap]::Round
    $pen.EndCap = [System.Drawing.Drawing2D.LineCap]::Round
    return $pen
}

$green = [System.Drawing.ColorTranslator]::FromHtml('#22C55E')
$amber = [System.Drawing.ColorTranslator]::FromHtml('#F59E0B')
$red = [System.Drawing.ColorTranslator]::FromHtml('#EF4444')
foreach ($arc in @(@($green, 135), @($amber, 225), @($red, 315))) {
    $pen = New-GaugePen $arc[0]
    $graphics.DrawArc($pen, $rectangle, $arc[1], 90)
    $pen.Dispose()
}

$angle = 252 * [Math]::PI / 180
$needle = [System.Drawing.PointF]::new($centerX + [Math]::Cos($angle) * $radius * 0.82, $centerY + [Math]::Sin($angle) * $radius * 0.82)
$needlePen = New-GaugePen $red
$needlePen.Width = $stroke * 0.8
$graphics.DrawLine($needlePen, $centerX, $centerY, $needle.X, $needle.Y)
$needlePen.Dispose()
$pivotRadius = $stroke * 0.62
$pivot = [System.Drawing.SolidBrush]::new($red)
$graphics.FillEllipse($pivot, $centerX - $pivotRadius, $centerY - $pivotRadius, $pivotRadius * 2, $pivotRadius * 2)
$pivot.Dispose()

$icon = [System.Drawing.Icon]::FromHandle($bitmap.GetHicon())
$stream = [System.IO.FileStream]::new($OutputPath, [System.IO.FileMode]::Create)
$icon.Save($stream)
$stream.Dispose()
$icon.Dispose()
$graphics.Dispose()
$bitmap.Dispose()
