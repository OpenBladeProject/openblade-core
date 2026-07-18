[CmdletBinding()]
param(
    [Parameter(Mandatory, ValueFromPipeline)]
    [ValidateScript({ Test-Path -LiteralPath $_ -PathType Leaf })]
    [string[]]$CapturePath,

    [ValidateNotNullOrEmpty()]
    [string]$TsharkPath = 'C:\Program Files\Wireshark\tshark.exe'
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

function Get-ChannelAverage {
    param(
        [Parameter(Mandatory)][byte[]]$Rgb,
        [Parameter(Mandatory)][ValidateRange(0, 2)][int]$Channel
    )

    [long]$sum = 0
    for ($index = $Channel; $index -lt $Rgb.Length; $index += 3) {
        $sum += $Rgb[$index]
    }

    return [Math]::Round($sum / ($Rgb.Length / 3), 1)
}

function Get-MatrixFrameSummary {
    param([Parameter(Mandatory)][string]$Row)

    $fields = $Row -split "`t"
    if ($fields.Count -ne 3) {
        throw "Expected frame, timestamp, and data fields; received: $Row"
    }

    [byte[]]$report = ConvertFrom-HexString $fields[2]
    if ($report.Length -ne 374 -or
        $report[4] -ne 0x01 -or
        $report[5] -ne 0x36 -or
        $report[6] -ne 0x03 -or
        $report[7] -ne 0x0B -or
        $report[8] -ne 0x00 -or
        $report[9] -ne 0x00 -or
        $report[10] -ne 0x00 -or
        $report[11] -ne 0x65) {
        throw "Frame $($fields[0]) is not the expected PID 02E0 102-key matrix report."
    }

    [byte[]]$rgb = $report[12..317]
    $colors = for ($index = 0; $index -lt $rgb.Length; $index += 3) {
        '{0:X2}{1:X2}{2:X2}' -f $rgb[$index], $rgb[$index + 1], $rgb[$index + 2]
    }

    [pscustomobject]@{
        Frame = [int]$fields[0]
        Epoch = [double]$fields[1]
        TransactionId = $report[1]
        RedAverage = Get-ChannelAverage $rgb 0
        GreenAverage = Get-ChannelAverage $rgb 1
        BlueAverage = Get-ChannelAverage $rgb 2
        UniqueColorCount = @($colors | Sort-Object -Unique).Count
        Report = $report
    }
}

foreach ($path in $CapturePath) {
    $resolved = (Resolve-Path -LiteralPath $path).Path
    $rows = @(& $TsharkPath `
        -r $resolved `
        -Y 'usb.device_address == 2 && usb.data_len == 382' `
        -T fields `
        -e frame.number `
        -e frame.time_epoch `
        -e usb.data_fragment)
    if ($LASTEXITCODE -ne 0) {
        throw "tshark failed while reading $resolved."
    }
    if ($rows.Count -eq 0) {
        throw "No 374-byte PID 02E0 matrix reports were found in $resolved."
    }

    $first = Get-MatrixFrameSummary $rows[0]
    $last = Get-MatrixFrameSummary $rows[-1]
    [pscustomobject]@{
        Capture = [IO.Path]::GetFileName($resolved)
        MatrixFrames = $rows.Count
        FirstFrame = $first.Frame
        LastFrame = $last.Frame
        DurationSeconds = [Math]::Round($last.Epoch - $first.Epoch, 3)
        FirstAverageRgb = "$($first.RedAverage),$($first.GreenAverage),$($first.BlueAverage)"
        LastAverageRgb = "$($last.RedAverage),$($last.GreenAverage),$($last.BlueAverage)"
        LastUniqueColors = $last.UniqueColorCount
        FirstTransactionIdHex = '{0:X2}' -f $first.TransactionId
        LastTransactionIdHex = '{0:X2}' -f $last.TransactionId
    }
}
