param([Parameter(Mandatory)][string]$PublishPath, [string]$ExpectedVersion = '1.0.0')

# Read icon files and executable resources only; never start the application.
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing
$root = (Resolve-Path -LiteralPath $PublishPath).Path
$iconPath = Join-Path $root 'Assets/AppIcon/RoomSwitcher.ico'
$data = [IO.File]::ReadAllBytes($iconPath)
$sizes = @(16, 20, 24, 32, 40, 48, 64, 96, 128, 256)
if ([BitConverter]::ToUInt16($data, 0) -ne 0 -or
    [BitConverter]::ToUInt16($data, 2) -ne 1 -or
    [BitConverter]::ToUInt16($data, 4) -ne $sizes.Count) { throw 'Invalid ICO header.' }
for ($i = 0; $i -lt $sizes.Count; $i++) {
    $entry = 6 + 16 * $i
    $size = $sizes[$i]
    $storedSize = if ($size -eq 256) { 0 } else { $size }
    if ($data[$entry] -ne $storedSize -or $data[$entry + 1] -ne $storedSize) { throw "Incorrect ICO size $size" }
    $length = [BitConverter]::ToUInt32($data, $entry + 8)
    $offset = [BitConverter]::ToUInt32($data, $entry + 12)
    if ($offset + $length -gt $data.Length) { throw 'ICO frame exceeds file length.' }
    $stream = [IO.MemoryStream]::new($data, [int]$offset, [int]$length, $false)
    $bitmap = [Drawing.Bitmap]::new($stream)
    try {
        if ($bitmap.Width -ne $size -or $bitmap.Height -ne $size) { throw 'Incorrect PNG dimensions.' }
        foreach ($x in @(0, ($size - 1))) { foreach ($y in @(0, ($size - 1))) {
            if ($bitmap.GetPixel($x, $y).A -ne 0) { throw "Opaque background in $size icon." }
        } }
        $pink = 0; $white = 0
        # Below 32px the white strokes are subpixel and blend with the pink tile.
        # Check their light pixels inside the letter region, not pure white globally.
        $whiteThreshold = if ($size -lt 32) { 170 } else { 235 }
        for ($y = 0; $y -lt $size; $y++) { for ($x = 0; $x -lt $size; $x++) {
            $pixel = $bitmap.GetPixel($x, $y)
            if ($pixel.A -gt 200 -and $pixel.R -gt ($pixel.G + 20)) { $pink++ }
            if ($x -ge $size * 0.18 -and $x -le $size * 0.42 -and
                $y -ge $size * 0.32 -and $y -le $size * 0.68 -and
                $pixel.A -gt 200 -and $pixel.R -gt $whiteThreshold -and
                $pixel.G -gt $whiteThreshold -and $pixel.B -gt $whiteThreshold) { $white++ }
        } }
        if ($pink -lt $size * $size * 0.15 -or $white -eq 0) { throw "Pink artwork or white R missing at $size." }
    } finally { $bitmap.Dispose(); $stream.Dispose() }
}
$exe = Join-Path $root 'CozyRoomswitch.exe'
$version = [Diagnostics.FileVersionInfo]::GetVersionInfo($exe).ProductVersion
if ($version -notlike "$ExpectedVersion*") { throw "Unexpected executable version $version" }
$extracted = [Drawing.Icon]::ExtractAssociatedIcon($exe)
if ($null -eq $extracted) { throw 'Executable has no application icon.' }
$embedded = $extracted.ToBitmap()
try {
    $pink = 0
    for ($y = 0; $y -lt $embedded.Height; $y++) { for ($x = 0; $x -lt $embedded.Width; $x++) {
        $pixel = $embedded.GetPixel($x, $y)
        if ($pixel.A -gt 200 -and $pixel.R -gt ($pixel.G + 20)) { $pink++ }
    } }
    if ($pink -lt $embedded.Width * $embedded.Height * 0.15) { throw 'Executable does not contain the pink icon.' }
} finally { $embedded.Dispose(); $extracted.Dispose() }
"PASS: $($sizes.Count) transparent ICO frames, preserved white R, pink executable icon, version $version; application not started."
