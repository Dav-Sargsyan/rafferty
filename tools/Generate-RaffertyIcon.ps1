param(
    [Parameter(Mandatory = $true)]
    [string] $OutputPath
)

Add-Type -AssemblyName System.Drawing

$size = 256
$bitmap = [System.Drawing.Bitmap]::new($size, $size, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
$graphics = [System.Drawing.Graphics]::FromImage($bitmap)
$graphics.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
$graphics.TextRenderingHint = [System.Drawing.Text.TextRenderingHint]::AntiAliasGridFit
$graphics.Clear([System.Drawing.Color]::Transparent)

$path = [System.Drawing.Drawing2D.GraphicsPath]::new()
$radius = 54
$diameter = $radius * 2
$path.AddArc(10, 10, $diameter, $diameter, 180, 90)
$path.AddArc($size - 10 - $diameter, 10, $diameter, $diameter, 270, 90)
$path.AddArc($size - 10 - $diameter, $size - 10 - $diameter, $diameter, $diameter, 0, 90)
$path.AddArc(10, $size - 10 - $diameter, $diameter, $diameter, 90, 90)
$path.CloseFigure()

$background = [System.Drawing.Drawing2D.LinearGradientBrush]::new(
    [System.Drawing.Point]::new(24, 16),
    [System.Drawing.Point]::new(232, 240),
    [System.Drawing.Color]::FromArgb(255, 36, 31, 70),
    [System.Drawing.Color]::FromArgb(255, 108, 88, 224))
$graphics.FillPath($background, $path)
$pen = [System.Drawing.Pen]::new([System.Drawing.Color]::FromArgb(255, 151, 137, 255), 5)
$graphics.DrawPath($pen, $path)

$font = [System.Drawing.Font]::new("Segoe UI", 132, [System.Drawing.FontStyle]::Bold, [System.Drawing.GraphicsUnit]::Pixel)
$format = [System.Drawing.StringFormat]::new()
$format.Alignment = [System.Drawing.StringAlignment]::Center
$format.LineAlignment = [System.Drawing.StringAlignment]::Center
$brush = [System.Drawing.SolidBrush]::new([System.Drawing.Color]::FromArgb(255, 245, 243, 255))
$graphics.DrawString("R", $font, $brush, [System.Drawing.RectangleF]::new(0, -5, $size, $size), $format)

$pngStream = [System.IO.MemoryStream]::new()
$bitmap.Save($pngStream, [System.Drawing.Imaging.ImageFormat]::Png)
$bitmap.Save([System.IO.Path]::ChangeExtension($OutputPath, ".png"), [System.Drawing.Imaging.ImageFormat]::Png)
$png = $pngStream.ToArray()
$output = [System.IO.File]::Open($OutputPath, [System.IO.FileMode]::Create, [System.IO.FileAccess]::Write)
$writer = [System.IO.BinaryWriter]::new($output)
$writer.Write([uint16]0); $writer.Write([uint16]1); $writer.Write([uint16]1)
$writer.Write([byte]0); $writer.Write([byte]0); $writer.Write([byte]0); $writer.Write([byte]0)
$writer.Write([uint16]1); $writer.Write([uint16]32); $writer.Write([uint32]$png.Length); $writer.Write([uint32]22)
$writer.Write($png)
$writer.Dispose()

$brush.Dispose(); $format.Dispose(); $font.Dispose(); $pen.Dispose(); $background.Dispose(); $path.Dispose()
$graphics.Dispose(); $bitmap.Dispose(); $pngStream.Dispose()
