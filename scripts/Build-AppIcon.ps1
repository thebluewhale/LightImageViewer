$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
$sourcePath = Join-Path $projectRoot 'src/ImageViewer/Assets/imageviewericon.png'
$outputPath = Join-Path $projectRoot 'src/ImageViewer/Assets/app.ico'
Add-Type -AssemblyName PresentationCore,WindowsBase
$source = [Windows.Media.Imaging.BitmapFrame]::Create([Uri]$sourcePath,
    [Windows.Media.Imaging.BitmapCreateOptions]::PreservePixelFormat,
    [Windows.Media.Imaging.BitmapCacheOption]::OnLoad)
$source.Freeze()
$sizes = @(16,20,24,32,40,48,64,128,256)
$frames = @()
foreach ($size in $sizes) {
    $scale = $size / [double][Math]::Max($source.PixelWidth, $source.PixelHeight)
    $width = $source.PixelWidth * $scale
    $height = $source.PixelHeight * $scale
    $visual = [Windows.Media.DrawingVisual]::new()
    [Windows.Media.RenderOptions]::SetBitmapScalingMode($visual, [Windows.Media.BitmapScalingMode]::HighQuality)
    $drawing = $visual.RenderOpen()
    try { $drawing.DrawImage($source, [Windows.Rect]::new(($size-$width)/2, ($size-$height)/2, $width, $height)) }
    finally { $drawing.Close() }
    $bitmap = [Windows.Media.Imaging.RenderTargetBitmap]::new($size, $size, 96, 96, [Windows.Media.PixelFormats]::Pbgra32)
    $bitmap.Render($visual)
    $encoder = [Windows.Media.Imaging.PngBitmapEncoder]::new()
    $encoder.Frames.Add([Windows.Media.Imaging.BitmapFrame]::Create($bitmap))
    $stream = [IO.MemoryStream]::new()
    try { $encoder.Save($stream); $frames += ,$stream.ToArray() }
    finally { $stream.Dispose() }
}
# ICO directory plus PNG-compressed frames for Windows 10/11 and high-DPI displays.
$stream = [IO.File]::Create($outputPath)
$writer = [IO.BinaryWriter]::new($stream)
try {
    $writer.Write([uint16]0); $writer.Write([uint16]1); $writer.Write([uint16]$sizes.Count)
    $offset = 6 + 16 * $sizes.Count
    for ($index = 0; $index -lt $sizes.Count; $index++) {
        $dimension = if ($sizes[$index] -eq 256) { 0 } else { $sizes[$index] }
        $writer.Write([byte]$dimension); $writer.Write([byte]$dimension)
        $writer.Write([byte]0); $writer.Write([byte]0)
        $writer.Write([uint16]1); $writer.Write([uint16]32)
        $writer.Write([uint32]$frames[$index].Length); $writer.Write([uint32]$offset)
        $offset += $frames[$index].Length
    }
    foreach ($frame in $frames) { $writer.Write([byte[]]$frame) }
}
finally { $writer.Dispose(); $stream.Dispose() }
Write-Output ('App icon generated: ' + ($sizes -join ', ') + ' px')
