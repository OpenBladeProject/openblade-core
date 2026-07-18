[CmdletBinding()]
param(
    [Parameter(Mandatory, ValueFromPipeline)]
    [ValidateScript({ Test-Path -LiteralPath $_ -PathType Leaf })]
    [string[]]$CapturePath,

    [ValidateNotNullOrEmpty()]
    [string]$TsharkPath = 'C:\Program Files\Wireshark\tshark.exe',

    [switch]$IncludeTimeline
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

foreach ($path in $CapturePath) {
    $resolved = (Resolve-Path -LiteralPath $path).Path
    $rows = @(& $TsharkPath `
        -r $resolved `
        -Y 'usb.device_address == 2 && (usb.data_len == 98 || usb.data_len == 382)' `
        -T fields `
        -e frame.number `
        -e frame.time_relative `
        -e usb.data_len `
        -e usb.data_fragment)
    if ($LASTEXITCODE -ne 0) {
        throw "tshark failed while reading $resolved."
    }

    $commands = foreach ($row in $rows) {
        $fields = $row -split "`t"
        if ($fields.Count -ne 4) {
            continue
        }

        [byte[]]$report = ConvertFrom-HexString $fields[3]
        if ($report.Length -notin 90, 374) {
            continue
        }

        $payloadLength = if ($report.Length -eq 90) {
            $report[5]
        }
        else {
            ([int]$report[4] * 256) + [int]$report[5]
        }
        $prefixLength = [Math]::Min($payloadLength, 8)
        $payloadPrefix = if ($prefixLength -eq 0) {
            ''
        }
        else {
            ($report[8..(7 + $prefixLength)] | ForEach-Object { '{0:X2}' -f $_ }) -join ''
        }

        [pscustomobject]@{
            Capture = [IO.Path]::GetFileName($resolved)
            Frame = [int]$fields[0]
            RelativeSeconds = [double]$fields[1]
            ReportLength = $report.Length
            TransactionIdHex = '{0:X2}' -f $report[1]
            Command = '{0:X2}/{1:X2}' -f $report[6], $report[7]
            PayloadLength = $payloadLength
            PayloadPrefixHex = $payloadPrefix
        }
    }

    if ($IncludeTimeline) {
        $commands | Where-Object {
            $_.Command -in '00/84', '00/86', '03/03', '03/0A'
        }
        continue
    }

    $commands |
        Group-Object Command, ReportLength, PayloadLength, PayloadPrefixHex |
        Sort-Object Count -Descending |
        ForEach-Object {
            $first = $_.Group | Select-Object -First 1
            [pscustomobject]@{
                Capture = $first.Capture
                Count = $_.Count
                Command = $first.Command
                ReportLength = $first.ReportLength
                PayloadLength = $first.PayloadLength
                PayloadPrefixHex = $first.PayloadPrefixHex
                FirstFrame = $first.Frame
                FirstAtSeconds = $first.RelativeSeconds
            }
        }
}
