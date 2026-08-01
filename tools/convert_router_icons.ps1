param(
    [string]$InputDirectory = (Join-Path $PSScriptRoot '..\src\AIUsageMonitor\Resources\RouterIcons')
)

Add-Type -AssemblyName System.Drawing

function Convert-ToGrayIcon([string]$sourceName, [string]$targetName) {
    $source = Join-Path $InputDirectory $sourceName
    $target = Join-Path $InputDirectory $targetName
    if ([System.IO.Path]::GetExtension($source) -eq '.ico') {
        $icon = [System.Drawing.Icon]::new($source, [System.Drawing.Size]::new(32, 32))
        $sourceBitmap = $icon.ToBitmap()
    } else {
        $icon = $null
        $sourceBitmap = [System.Drawing.Image]::FromFile($source)
    }
    $output = [System.Drawing.Bitmap]::new(32, 32, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $graphics = [System.Drawing.Graphics]::FromImage($output)
    $graphics.DrawImage($sourceBitmap, [System.Drawing.Rectangle]::new(0, 0, 32, 32))
    $background = $output.GetPixel(0, 0)
    $muted = [System.Drawing.Color]::FromArgb(139, 148, 161)
    for ($y = 0; $y -lt 32; $y++) {
        for ($x = 0; $x -lt 32; $x++) {
            $pixel = $output.GetPixel($x, $y)
            $distance = [Math]::Abs($pixel.R - $background.R) + [Math]::Abs($pixel.G - $background.G) + [Math]::Abs($pixel.B - $background.B)
            if ($pixel.A -eq 0 -or $distance -lt 18) {
                $output.SetPixel($x, $y, [System.Drawing.Color]::Transparent)
            } else {
                $output.SetPixel($x, $y, [System.Drawing.Color]::FromArgb($pixel.A, $muted.R, $muted.G, $muted.B))
            }
        }
    }
    $output.Save($target, [System.Drawing.Imaging.ImageFormat]::Png)
    $graphics.Dispose(); $output.Dispose(); $sourceBitmap.Dispose(); if ($null -ne $icon) { $icon.Dispose() }
}

Convert-ToGrayIcon 'openrouter-source.png' 'openrouter-gray.png'
Convert-ToGrayIcon 'nanogpt-source.ico' 'nanogpt-gray.png'
