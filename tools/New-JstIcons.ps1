Add-Type -AssemblyName System.Drawing

function Add-IcoImage([System.IO.BinaryWriter] $writer, [System.Drawing.Bitmap] $bitmap) {
    $stream = [System.IO.MemoryStream]::new()
    $bitmap.Save($stream, [System.Drawing.Imaging.ImageFormat]::Png)
    $bytes = $stream.ToArray()
    $writer.Write([byte]($(if ($bitmap.Width -eq 256) { 0 } else { $bitmap.Width })))
    $writer.Write([byte]($(if ($bitmap.Height -eq 256) { 0 } else { $bitmap.Height })))
    $writer.Write([byte]0)
    $writer.Write([byte]0)
    $writer.Write([UInt16]1)
    $writer.Write([UInt16]32)
    $writer.Write([UInt32]$bytes.Length)
    $writer.Write([UInt32]0) # Filled after all directory entries are written.
    return ,$bytes
}

function New-JstIcon([string] $path, [bool] $isAgent) {
    $sizes = 16, 24, 32, 48, 64, 128, 256
    $images = [System.Collections.Generic.List[byte[]]]::new()
    $bitmaps = [System.Collections.Generic.List[System.Drawing.Bitmap]]::new()
    foreach ($size in $sizes) {
        $bitmap = [System.Drawing.Bitmap]::new($size, $size, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
        $graphics = [System.Drawing.Graphics]::FromImage($bitmap)
        $graphics.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
        $graphics.TextRenderingHint = [System.Drawing.Text.TextRenderingHint]::AntiAliasGridFit
        $graphics.Clear([System.Drawing.Color]::Transparent)
        $blue = [System.Drawing.Color]::FromArgb(20, 57, 133)
        $cyan = [System.Drawing.Color]::FromArgb(44, 193, 202)
        $white = [System.Drawing.Color]::White
        $pad = [Math]::Max(1, [int]($size * 0.055))
        $graphics.FillRectangle([System.Drawing.SolidBrush]::new($blue), $pad, $pad, $size - (2 * $pad), $size - (2 * $pad))

        $fontSize = [Math]::Max(6, $size * 0.34)
        $font = [System.Drawing.Font]::new('Arial', $fontSize, [System.Drawing.FontStyle]::Bold -bor [System.Drawing.FontStyle]::Italic, [System.Drawing.GraphicsUnit]::Pixel)
        $format = [System.Drawing.StringFormat]::new()
        $format.Alignment = [System.Drawing.StringAlignment]::Center
        $format.LineAlignment = [System.Drawing.StringAlignment]::Center
        $textArea = [System.Drawing.RectangleF]::new(0, $size * 0.04, $size, $size * 0.47)
        $graphics.DrawString('JST', $font, [System.Drawing.SolidBrush]::new($white), $textArea, $format)

        $pen = [System.Drawing.Pen]::new($cyan, [Math]::Max(1, $size * 0.055))
        $pen.StartCap = [System.Drawing.Drawing2D.LineCap]::Round
        $pen.EndCap = [System.Drawing.Drawing2D.LineCap]::Round
        if ($isAgent) {
            $graphics.DrawArc($pen, $size * 0.22, $size * 0.43, $size * 0.56, $size * 0.42, 35, 255)
            $graphics.FillPolygon([System.Drawing.SolidBrush]::new($cyan), @([System.Drawing.PointF]::new($size * 0.72, $size * 0.46), [System.Drawing.PointF]::new($size * 0.87, $size * 0.47), [System.Drawing.PointF]::new($size * 0.78, $size * 0.59)))
        } else {
            $graphics.DrawLine($pen, $size * 0.22, $size * 0.72, $size * 0.50, $size * 0.58)
            $graphics.DrawLine($pen, $size * 0.50, $size * 0.58, $size * 0.78, $size * 0.72)
            foreach ($point in @([System.Drawing.PointF]::new($size * 0.22, $size * 0.72), [System.Drawing.PointF]::new($size * 0.50, $size * 0.58), [System.Drawing.PointF]::new($size * 0.78, $size * 0.72))) {
                $radius = [Math]::Max(1, $size * 0.07)
                $graphics.FillEllipse([System.Drawing.SolidBrush]::new($cyan), $point.X - $radius, $point.Y - $radius, 2 * $radius, 2 * $radius)
            }
        }
        $pen.Dispose(); $font.Dispose(); $format.Dispose(); $graphics.Dispose()
        $bitmaps.Add($bitmap)
    }

    $file = [System.IO.File]::Create($path)
    $writer = [System.IO.BinaryWriter]::new($file)
    $writer.Write([UInt16]0); $writer.Write([UInt16]1); $writer.Write([UInt16]$sizes.Count)
    foreach ($bitmap in $bitmaps) { $images.Add((Add-IcoImage $writer $bitmap)) }
    $offset = 6 + (16 * $sizes.Count)
    $writer.BaseStream.Position = 6
    foreach ($image in $images) { $writer.BaseStream.Position += 12; $writer.Write([UInt32]$offset); $offset += $image.Length }
    $writer.BaseStream.Position = 6 + (16 * $sizes.Count)
    foreach ($image in $images) { $writer.Write($image) }
    $writer.Dispose(); $file.Dispose()
    foreach ($bitmap in $bitmaps) { $bitmap.Dispose() }
}

$assets = Join-Path $PSScriptRoot '..\src\Jst.PlcFinder\Assets'
New-Item -ItemType Directory -Force -Path $assets | Out-Null
New-JstIcon (Join-Path $assets 'JstPlcFinder.ico') $false
New-JstIcon (Join-Path $assets 'JstEchoAgent.ico') $true
