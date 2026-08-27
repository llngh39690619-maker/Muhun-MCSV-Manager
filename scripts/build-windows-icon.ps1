param(
    [string]$SourcePng,
    [string]$DestinationIco
)

$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot

if ([string]::IsNullOrWhiteSpace($SourcePng)) {
    $SourcePng = Join-Path $projectRoot 'src\MinecraftServerManager.App\Assets\MuhunMcsvManager-source.png'
}

if ([string]::IsNullOrWhiteSpace($DestinationIco)) {
    $DestinationIco = Join-Path $projectRoot 'src\MinecraftServerManager.App\Assets\MuhunMcsvManager.ico'
}

$SourcePng = [System.IO.Path]::GetFullPath($SourcePng)
$DestinationIco = [System.IO.Path]::GetFullPath($DestinationIco)

if (-not (Test-Path -LiteralPath $SourcePng -PathType Leaf)) {
    throw "Source PNG not found: $SourcePng"
}

Add-Type -AssemblyName WindowsBase
Add-Type -AssemblyName PresentationCore

$sourceStream = [System.IO.File]::OpenRead($SourcePng)
try {
    $sourceBitmap = [System.Windows.Media.Imaging.BitmapImage]::new()
    $sourceBitmap.BeginInit()
    $sourceBitmap.CacheOption = [System.Windows.Media.Imaging.BitmapCacheOption]::OnLoad
    $sourceBitmap.CreateOptions = [System.Windows.Media.Imaging.BitmapCreateOptions]::PreservePixelFormat
    $sourceBitmap.StreamSource = $sourceStream
    $sourceBitmap.EndInit()
    $sourceBitmap.Freeze()
}
finally {
    $sourceStream.Dispose()
}

if ($sourceBitmap.PixelWidth -lt 256 -or $sourceBitmap.PixelHeight -lt 256) {
    throw "Source PNG must be at least 256 x 256 pixels; actual size is $($sourceBitmap.PixelWidth) x $($sourceBitmap.PixelHeight)."
}

$sizes = @(16, 20, 24, 32, 40, 48, 64, 96, 128, 256)
$images = foreach ($size in $sizes) {
    $drawingVisual = [System.Windows.Media.DrawingVisual]::new()
    [System.Windows.Media.RenderOptions]::SetBitmapScalingMode(
        $drawingVisual,
        [System.Windows.Media.BitmapScalingMode]::Fant)

    $drawingContext = $drawingVisual.RenderOpen()
    try {
        $drawingContext.DrawImage(
            $sourceBitmap,
            [System.Windows.Rect]::new(0, 0, $size, $size))
    }
    finally {
        $drawingContext.Close()
    }

    $rendered = [System.Windows.Media.Imaging.RenderTargetBitmap]::new(
        $size,
        $size,
        96,
        96,
        [System.Windows.Media.PixelFormats]::Pbgra32)
    $rendered.Render($drawingVisual)
    $rendered.Freeze()

    $encoder = [System.Windows.Media.Imaging.PngBitmapEncoder]::new()
    $encoder.Frames.Add([System.Windows.Media.Imaging.BitmapFrame]::Create($rendered))
    $memory = [System.IO.MemoryStream]::new()
    try {
        $encoder.Save($memory)
        [pscustomobject]@{
            Size = $size
            Bytes = $memory.ToArray()
        }
    }
    finally {
        $memory.Dispose()
    }
}

$destinationDirectory = [System.IO.Path]::GetDirectoryName($DestinationIco)
[System.IO.Directory]::CreateDirectory($destinationDirectory) | Out-Null
$temporaryPath = Join-Path $destinationDirectory ('.' + [System.IO.Path]::GetRandomFileName() + '.tmp')

$fileStream = [System.IO.FileStream]::new(
    $temporaryPath,
    [System.IO.FileMode]::CreateNew,
    [System.IO.FileAccess]::Write,
    [System.IO.FileShare]::None)
$writer = [System.IO.BinaryWriter]::new($fileStream)
try {
    $writer.Write([uint16]0)
    $writer.Write([uint16]1)
    $writer.Write([uint16]$images.Count)

    [uint32]$offset = 6 + (16 * $images.Count)
    foreach ($image in $images) {
        $dimension = if ($image.Size -eq 256) { 0 } else { $image.Size }
        $writer.Write([byte]$dimension)
        $writer.Write([byte]$dimension)
        $writer.Write([byte]0)
        $writer.Write([byte]0)
        $writer.Write([uint16]1)
        $writer.Write([uint16]32)
        $writer.Write([uint32]$image.Bytes.Length)
        $writer.Write($offset)
        $offset += [uint32]$image.Bytes.Length
    }

    foreach ($image in $images) {
        $writer.Write([byte[]]$image.Bytes)
    }

    $writer.Flush()
    $fileStream.Flush($true)
}
finally {
    $writer.Dispose()
    $fileStream.Dispose()
}

Move-Item -LiteralPath $temporaryPath -Destination $DestinationIco -Force
Write-Host "Windows icon built: $DestinationIco"
Write-Host "Sizes: $($sizes -join ', ')"
