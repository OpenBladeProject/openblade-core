[CmdletBinding()]
param()

Set-StrictMode -Version 3.0
$ErrorActionPreference = 'Stop'
$repository = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$readme = Get-Content -LiteralPath (Join-Path $repository 'README.md')

function Assert-UsbIdentity {
    param(
        [Parameter(Mandatory)][string]$Model,
        [Parameter(Mandatory)][string]$Expected
    )

    $row = @($readme | Where-Object { $_ -match "^\| .*``$Model`` .+\|$" })
    if ($row.Count -ne 1) {
        throw "Expected exactly one README hardware-support row for $Model."
    }

    $cells = $row[0].Split('|')
    if ($cells.Length -ne 4) {
        throw "README hardware-support row for $Model must contain only laptop and USB identity."
    }

    $actual = $cells[2].Trim()
    if ($actual -cne $Expected) {
        throw "README USB identity for $Model must be '$Expected'; received '$actual'."
    }
}

$header = @($readme | Where-Object { $_ -eq '| Laptop | USB identity |' })
if ($header.Count -ne 1) {
    throw 'README hardware support must use the brief laptop and USB identity table.'
}

Assert-UsbIdentity -Model 'RZ09-0581' -Expected '`1532:02E0`'
Assert-UsbIdentity -Model 'RZ09-0528' -Expected '`1532:02C6`'

Write-Host 'README hardware-support tests passed.'
