[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [ValidateScript({ Test-Path -LiteralPath $_ -PathType Leaf })]
    [string]$CapturePath,

    [Parameter(Mandatory)]
    [string]$OutputPath,

    [ValidateNotNullOrEmpty()]
    [string]$TsharkPath = 'C:\Program Files\Wireshark\tshark.exe',

    [double]$LeftwardStartSeconds = 167,

    [double]$RightwardStartSeconds = 180
)

$ErrorActionPreference = 'Stop'

function ConvertFrom-HexString {
    param([Parameter(Mandatory)][string]$Value)

    if (($Value.Length % 2) -ne 0) {
        throw 'A capture field contained an odd-length hexadecimal value.'
    }

    $bytes = [byte[]]::new($Value.Length / 2)
    for ($index = 0; $index -lt $bytes.Length; $index++) {
        $bytes[$index] = [Convert]::ToByte($Value.Substring($index * 2, 2), 16)
    }

    return ,$bytes
}

function Export-DirectionTemplate {
    param(
        [Parameter(Mandatory)][double]$StartSeconds,
        [Parameter(Mandatory)][string]$Direction
    )

    $endSeconds = $StartSeconds + 5.1
    $filter = 'usb.data_len == 382 && usb.data_fragment && ' +
        "frame.time_relative >= $StartSeconds && frame.time_relative < $endSeconds"
    $rows = @(& $TsharkPath `
        -r $resolvedCapture `
        -Y $filter `
        -T fields `
        -e frame.number `
        -e frame.time_relative `
        -e usb.data_fragment)
    if ($LASTEXITCODE -ne 0 -or $rows.Count -lt 100) {
        throw "The $Direction Tidal window did not contain enough matrix frames."
    }

    $frames = foreach ($row in $rows) {
        $fields = $row -split "`t"
        if ($fields.Count -ne 3) {
            throw "Unexpected tshark row: $row"
        }

        [byte[]]$report = ConvertFrom-HexString $fields[2]
        if ($report.Length -ne 374 -or
            $report[4] -ne 0x01 -or
            $report[5] -ne 0x36 -or
            $report[6] -ne 0x03 -or
            $report[7] -ne 0x0B -or
            $report[11] -ne 0x65) {
            throw "Frame $($fields[0]) is not the expected PID 02E0 matrix report."
        }

        [pscustomobject]@{
            Frame = [int]$fields[0]
            Seconds = [double]::Parse(
                $fields[1],
                [Globalization.CultureInfo]::InvariantCulture)
            Report = $report
        }
    }

    # Store one exact nearest-neighbor oracle frame every 50 ms. Each slot is a
    # signed Int16: positive intensity selects Color 1, negative selects Color 2.
    # The focused capture used pure green and pure blue, so this representation
    # is lossless while removing all user-selected RGB values from the fixture.
    [byte[]]$template = [byte[]]::new(100 * 102 * 2)
    for ($sample = 0; $sample -lt 100; $sample++) {
        $targetSeconds = $StartSeconds + ($sample * 0.05)
        $nearest = $frames |
            Sort-Object { [Math]::Abs($_.Seconds - $targetSeconds) } |
            Select-Object -First 1

        for ($slot = 0; $slot -lt 102; $slot++) {
            $rgbOffset = 12 + ($slot * 3)
            $red = $nearest.Report[$rgbOffset]
            $green = $nearest.Report[$rgbOffset + 1]
            $blue = $nearest.Report[$rgbOffset + 2]
            if ($red -ne 0 -or ($green -ne 0 -and $blue -ne 0)) {
                throw "Frame $($nearest.Frame), slot $slot is not a pure captured Tidal channel."
            }

            [int16]$value = if ($green -ne 0) { $green } else { -[int]$blue }
            [byte[]]$encoded = [BitConverter]::GetBytes($value)
            $destination = (($sample * 102) + $slot) * 2
            $template[$destination] = $encoded[0]
            $template[$destination + 1] = $encoded[1]
        }
    }

    $compressed = [IO.MemoryStream]::new()
    $deflate = [IO.Compression.DeflateStream]::new(
        $compressed,
        [IO.Compression.CompressionLevel]::Optimal,
        $true)
    try {
        $deflate.Write($template, 0, $template.Length)
    }
    finally {
        $deflate.Dispose()
    }

    [pscustomobject]@{
        direction = $Direction
        startSeconds = $StartSeconds
        firstSourceFrame = $frames[0].Frame
        lastSourceFrame = $frames[-1].Frame
        encoding = 'deflate-base64-signed-int16-color1-positive-color2-negative'
        uncompressedBytes = $template.Length
        data = [Convert]::ToBase64String($compressed.ToArray())
    }
}

$resolvedCapture = (Resolve-Path -LiteralPath $CapturePath).Path
$captureBytes = [IO.File]::ReadAllBytes($resolvedCapture)
$sha256 = [Security.Cryptography.SHA256]::Create()
try {
    $captureHash = $sha256.ComputeHash($captureBytes)
}
finally {
    $sha256.Dispose()
}
$fixture = [ordered]@{
    schemaVersion = 1
    evidenceRole = 'SanitizedTidalMatrixOracle'
    target = [ordered]@{
        modelNumber = 'RZ09-0581'
        vendorIdHex = '1532'
        productIdHex = '02E0'
        bios = '3.01'
    }
    sourceCaptureSha256 = -join ($captureHash | ForEach-Object { $_.ToString('X2') })
    cycleMilliseconds = 5000
    frameIntervalMilliseconds = 50
    frameCount = 100
    slotCount = 102
    capturedColor1RgbHex = '00FF00'
    capturedColor2RgbHex = '0000FF'
    templates = @(
        Export-DirectionTemplate $LeftwardStartSeconds 'Leftward'
        Export-DirectionTemplate $RightwardStartSeconds 'Rightward'
    )
}

$json = $fixture | ConvertTo-Json -Depth 8
$fullOutputPath = [IO.Path]::GetFullPath($OutputPath)
[IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($fullOutputPath)) | Out-Null
$temporaryPath = "$fullOutputPath.tmp"
[IO.File]::WriteAllText($temporaryPath, $json, [Text.UTF8Encoding]::new($false))
Move-Item -LiteralPath $temporaryPath -Destination $fullOutputPath -Force

[pscustomobject]@{
    OutputPath = $fullOutputPath
    SourceCaptureSha256 = $fixture.sourceCaptureSha256
    FrameCountPerDirection = $fixture.frameCount
    SlotCount = $fixture.slotCount
}
