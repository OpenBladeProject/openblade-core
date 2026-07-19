[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [ValidateScript({ Test-Path -LiteralPath $_ -PathType Leaf })]
    [string]$CapturePath,

    [Parameter(Mandatory)]
    [string]$OutputPath,

    [ValidateNotNullOrEmpty()]
    [string]$TsharkPath = 'C:\Program Files\Wireshark\tshark.exe',

    [double]$WaveRightwardStartSeconds = 677.014,
    [double]$WaveLeftwardStartSeconds = 684.044,
    [double]$WheelClockwiseStartSeconds = 693.006,
    [double]$WheelCounterclockwiseStartSeconds = 704.006
)

$ErrorActionPreference = 'Stop'
$frameCount = 25
$slotCount = 102
$cycleSeconds = 1.0
$frameIntervalSeconds = $cycleSeconds / $frameCount
$holeSlots = @(0, 17, 31, 34, 48, 51, 64, 65, 68, 70, 81, 82, 85, 89, 92, 93)

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

function Get-CircularPhaseDistance {
    param([double]$Left, [double]$Right)

    $distance = [Math]::Abs($Left - $Right)
    return [Math]::Min($distance, $cycleSeconds - $distance)
}

function Export-Template {
    param(
        [Parameter(Mandatory)][double]$StartSeconds,
        [Parameter(Mandatory)][string]$Effect,
        [Parameter(Mandatory)][string]$Direction
    )

    $endSeconds = $StartSeconds + 3.1
    $filter = 'usb.device_address == 2 && usb.data_len == 382 && ' +
        'usb.data_fragment && ' +
        "frame.time_relative >= $StartSeconds && frame.time_relative < $endSeconds"
    $rows = @(& $TsharkPath `
        -r $resolvedCapture `
        -Y $filter `
        -T fields `
        -e frame.number `
        -e frame.time_relative `
        -e usb.data_fragment)
    if ($LASTEXITCODE -ne 0 -or $rows.Count -lt 50) {
        throw "The $Effect $Direction window did not contain enough matrix frames."
    }

    $frames = foreach ($row in $rows) {
        $fields = $row -split "`t"
        [byte[]]$report = ConvertFrom-HexString $fields[2]
        if ($fields.Count -ne 3 -or
            $report.Length -ne 374 -or
            $report[4] -ne 0x01 -or
            $report[5] -ne 0x36 -or
            $report[6] -ne 0x03 -or
            $report[7] -ne 0x0B -or
            $report[11] -ne 0x65) {
            throw "Frame $($fields[0]) is not the expected PID 02E0 matrix report."
        }

        $seconds = [double]::Parse(
            $fields[1],
            [Globalization.CultureInfo]::InvariantCulture)
        $phase = ($seconds - $StartSeconds) % $cycleSeconds
        if ($phase -lt 0) { $phase += $cycleSeconds }
        [pscustomobject]@{
            Frame = [int]$fields[0]
            Seconds = $seconds
            Phase = $phase
            Report = $report
        }
    }

    [byte[]]$template = [byte[]]::new($frameCount * $slotCount * 3)
    for ($sample = 0; $sample -lt $frameCount; $sample++) {
        $targetPhase = $sample * $frameIntervalSeconds
        $nearest = $frames |
            Sort-Object { Get-CircularPhaseDistance $_.Phase $targetPhase } |
            Select-Object -First 1
        if ((Get-CircularPhaseDistance $nearest.Phase $targetPhase) -gt 0.025) {
            throw "The $Effect $Direction phase $sample has no sufficiently close source frame."
        }

        for ($slot = 0; $slot -lt $slotCount; $slot++) {
            $source = 12 + ($slot * 3)
            $destination = (($sample * $slotCount) + $slot) * 3
            $template[$destination] = $nearest.Report[$source]
            $template[$destination + 1] = $nearest.Report[$source + 1]
            $template[$destination + 2] = $nearest.Report[$source + 2]
            $isBlack = $template[$destination] -eq 0 -and
                $template[$destination + 1] -eq 0 -and
                $template[$destination + 2] -eq 0
            if (($holeSlots -contains $slot) -ne $isBlack) {
                throw "The $Effect $Direction frame $($nearest.Frame) has an invalid slot mask."
            }
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
        effect = $Effect
        direction = $Direction
        startSeconds = $StartSeconds
        firstSourceFrame = $frames[0].Frame
        lastSourceFrame = $frames[-1].Frame
        encoding = 'deflate-base64-rgb24'
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
    evidenceRole = 'SanitizedWaveWheelMatrixOracle'
    target = [ordered]@{
        modelNumber = 'RZ09-0581'
        vendorIdHex = '1532'
        productIdHex = '02E0'
        bios = '3.01'
    }
    sourceCaptureSha256 = -join ($captureHash | ForEach-Object { $_.ToString('X2') })
    cycleMilliseconds = 1000
    frameIntervalMilliseconds = 40
    frameCount = $frameCount
    slotCount = $slotCount
    templates = @(
        Export-Template $WaveRightwardStartSeconds 'Wave' 'Rightward'
        Export-Template $WaveLeftwardStartSeconds 'Wave' 'Leftward'
        Export-Template $WheelClockwiseStartSeconds 'Wheel' 'Clockwise'
        Export-Template $WheelCounterclockwiseStartSeconds 'Wheel' 'Counterclockwise'
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
    TemplateCount = $fixture.templates.Count
    FrameCountPerTemplate = $fixture.frameCount
    SlotCount = $fixture.slotCount
}
