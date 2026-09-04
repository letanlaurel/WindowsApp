# Convert PNG to multi-size ICO (16/32/48 as BMP DIB entries, 256 as PNG entry)
param(
    [string]$Png = "D:\WindowsApp\SnipPin\logo.png",
    [string]$Out = "D:\WindowsApp\SnipPin\Assets\logo.ico"
)
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

New-Item -ItemType Directory -Force -Path (Split-Path $Out) | Out-Null
$src = [System.Drawing.Bitmap]::FromFile($Png)

# Resize keeping aspect ratio, centered on transparent square canvas
function New-Sized([int]$s) {
    $b = New-Object System.Drawing.Bitmap $s, $s
    $g = [System.Drawing.Graphics]::FromImage($b)
    $g.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
    $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::HighQuality
    $g.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
    $scale = [Math]::Min($s / $src.Width, $s / $src.Height)
    $w = [int]($src.Width * $scale)
    $h = [int]($src.Height * $scale)
    $x = [int](($s - $w) / 2)
    $y = [int](($s - $h) / 2)
    $g.DrawImage($src, $x, $y, $w, $h)
    $g.Dispose()
    return $b
}

# Build BMP DIB bytes (BITMAPINFOHEADER + bottom-up BGRA + empty AND mask)
function Get-DibBytes([System.Drawing.Bitmap]$b) {
    $w = $b.Width; $h = $b.Height
    $rect = New-Object System.Drawing.Rectangle 0, 0, $w, $h
    $bd = $b.LockBits($rect, [System.Drawing.Imaging.ImageLockMode]::ReadOnly, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $stride = $bd.Stride
    $px = New-Object byte[] ($stride * $h)
    [System.Runtime.InteropServices.Marshal]::Copy($bd.Scan0, $px, 0, $px.Length)
    $b.UnlockBits($bd)

    # bottom-up rows
    $pixels = New-Object byte[] ($w * 4 * $h)
    for ($y = 0; $y -lt $h; $y++) {
        [Array]::Copy($px, ($h - 1 - $y) * $stride, $pixels, $y * $w * 4, $w * 4)
    }
    # AND mask: rows padded to 32 bits, all zeros (alpha channel carries transparency)
    $maskRow = (($w + 31) -shr 5) -shl 2
    $mask = New-Object byte[] ($maskRow * $h)

    $ms = New-Object System.IO.MemoryStream
    $bw = [System.IO.BinaryWriter]::new($ms)
    $bw.Write([UInt32]40)          # biSize
    $bw.Write([Int32]$w)           # biWidth
    $bw.Write([Int32]($h * 2))     # biHeight doubled (XOR+AND)
    $bw.Write([UInt16]1)           # biPlanes
    $bw.Write([UInt16]32)          # biBitCount
    $bw.Write([UInt32]0)           # biCompression BI_RGB
    $bw.Write([UInt32]($w * 4 * $h)) # biSizeImage
    $bw.Write([Int32]0)            # biXPelsPerMeter
    $bw.Write([Int32]0)            # biYPelsPerMeter
    $bw.Write([UInt32]0)           # biClrUsed
    $bw.Write([UInt32]0)           # biClrImportant
    $bw.Write($pixels)
    $bw.Write($mask)
    $bw.Flush()
    return $ms.ToArray()
}

# 256px entry as raw PNG bytes
$b256 = New-Sized 256
$pngMs = New-Object System.IO.MemoryStream
$b256.Save($pngMs, [System.Drawing.Imaging.ImageFormat]::Png)
$pngBytes = $pngMs.ToArray()

# Small BMP entries
$sizes = @(16, 32, 48)
$dibs = @()
foreach ($s in $sizes) {
    $bmp = New-Sized $s
    $dibs += ,@($s, (Get-DibBytes $bmp))
    $bmp.Dispose()
}

# Compose ICO: ICONDIR + ICONDIRENTRY[] + data blobs
$icoStream = New-Object System.IO.MemoryStream
$bw = [System.IO.BinaryWriter]::new($icoStream)
$count = $sizes.Count + 1
$bw.Write([UInt16]0)      # reserved
$bw.Write([UInt16]1)      # type = icon
$bw.Write([UInt16]$count)

$offset = 6 + 16 * $count
foreach ($d in $dibs) {
    $s = $d[0]; $bytes = $d[1]
    $bw.Write([byte]$s)          # width
    $bw.Write([byte]$s)          # height
    $bw.Write([byte]0)           # colors
    $bw.Write([byte]0)           # reserved
    $bw.Write([UInt16]1)         # planes
    $bw.Write([UInt16]32)        # bitcount
    $bw.Write([UInt32]$bytes.Length)
    $bw.Write([UInt32]$offset)
    $offset += $bytes.Length
}
# 256 PNG entry
$bw.Write([byte]0)              # 0 = 256
$bw.Write([byte]0)
$bw.Write([byte]0)
$bw.Write([byte]0)
$bw.Write([UInt16]1)
$bw.Write([UInt16]32)
$bw.Write([UInt32]$pngBytes.Length)
$bw.Write([UInt32]$offset)

foreach ($d in $dibs) { $bw.Write([byte[]]$d[1]) }
$bw.Write($pngBytes)
$bw.Flush()
[System.IO.File]::WriteAllBytes($Out, $icoStream.ToArray())

$src.Dispose(); $b256.Dispose()
$fi = Get-Item $Out
Write-Output ("ICO written: {0} ({1:N0} bytes)" -f $Out, $fi.Length)
