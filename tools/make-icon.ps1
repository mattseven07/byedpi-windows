# Converts the Android launcher icon (WebP) into a multi-size Windows .ico
# using the WIC WebP codec that ships with Windows 10 1809+ / Windows 11.
param(
    [string]$Source = "$PSScriptRoot\..\src\ByeDPI\icon.webp",
    [string]$Target = "$PSScriptRoot\..\src\ByeDPI\app.ico"
)
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName PresentationCore, WindowsBase, System.Drawing

$stream = [IO.File]::OpenRead((Resolve-Path $Source))
try {
    $decoder = [Windows.Media.Imaging.BitmapDecoder]::Create($stream,
        [Windows.Media.Imaging.BitmapCreateOptions]::None,
        [Windows.Media.Imaging.BitmapCacheOption]::OnLoad)
    $frame = $decoder.Frames[0]
} finally { $stream.Close() }

$pngEncoder = New-Object Windows.Media.Imaging.PngBitmapEncoder
$pngEncoder.Frames.Add([Windows.Media.Imaging.BitmapFrame]::Create($frame))
$ms = New-Object IO.MemoryStream
$pngEncoder.Save($ms)
$ms.Position = 0
$master = [Drawing.Bitmap]::FromStream($ms)

$sizes = 16, 20, 24, 32, 40, 48, 64, 128, 256
$images = foreach ($s in $sizes) {
    $bmp = New-Object Drawing.Bitmap $s, $s, ([Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [Drawing.Graphics]::FromImage($bmp)
    $g.InterpolationMode = [Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
    $g.PixelOffsetMode = [Drawing.Drawing2D.PixelOffsetMode]::HighQuality
    $g.DrawImage($master, 0, 0, $s, $s)
    $g.Dispose()
    $out = New-Object IO.MemoryStream
    $bmp.Save($out, [Drawing.Imaging.ImageFormat]::Png)
    $bmp.Dispose()
    ,$out.ToArray()
}

# ICO container with PNG-compressed entries (supported since Windows Vista)
$ico = New-Object IO.MemoryStream
$w = New-Object IO.BinaryWriter $ico
$w.Write([UInt16]0); $w.Write([UInt16]1); $w.Write([UInt16]$sizes.Count)
$offset = 6 + 16 * $sizes.Count
for ($i = 0; $i -lt $sizes.Count; $i++) {
    $s = $sizes[$i]; $len = $images[$i].Length
    $w.Write([byte]($s % 256)); $w.Write([byte]($s % 256))
    $w.Write([byte]0); $w.Write([byte]0)
    $w.Write([UInt16]1); $w.Write([UInt16]32)
    $w.Write([UInt32]$len); $w.Write([UInt32]$offset)
    $offset += $len
}
foreach ($img in $images) { $w.Write($img) }
$w.Flush()
[IO.File]::WriteAllBytes($Target, $ico.ToArray())
"Wrote $Target ($($ico.Length) bytes)"
