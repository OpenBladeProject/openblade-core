[CmdletBinding()]
param()

Set-StrictMode -Version 3.0
$ErrorActionPreference = 'Stop'
$repository = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$readme = Get-Content -LiteralPath (Join-Path $repository 'README.md')
$agents = Get-Content -LiteralPath (Join-Path $repository 'AGENTS.md')

function Assert-ModelNumber {
    param(
        [Parameter(Mandatory)][string]$Model,
        [Parameter(Mandatory)][string]$Expected
    )

    $row = @($readme | Where-Object { $_ -match "^\| .*``$Model``.*\|$" })
    if ($row.Count -ne 1) {
        throw "Expected exactly one README hardware-support row for $Model."
    }

    $cells = $row[0].Split('|')
    if ($cells.Length -ne 4) {
        throw "README hardware-support row for $Model must contain only laptop and model number."
    }

    $actual = $cells[2].Trim()
    if ($actual -cne $Expected) {
        throw "README model number for $Model must be '$Expected'; received '$actual'."
    }
}

function Assert-UsbIdentity {
    param(
        [Parameter(Mandatory)][string]$Model,
        [Parameter(Mandatory)][string]$Expected
    )

    $line = @($agents | Where-Object { $_ -cmatch "$Model \(``$Expected``\)" })
    if ($line.Count -lt 1) {
        throw "AGENTS.md must record the admitted USB identity '$Expected' for $Model."
    }
}

$header = @($readme | Where-Object { $_ -eq '| Laptop | Model number |' })
if ($header.Count -ne 1) {
    throw 'README hardware support must use the brief laptop and model number table.'
}

Assert-ModelNumber -Model 'RZ09-0581' -Expected '`RZ09-0581`'
Assert-ModelNumber -Model 'RZ09-0528' -Expected '`RZ09-0528`'

# The README is written for laptop owners, so USB identifiers stay out of it and
# are pinned where the admitted targets are declared instead.
$identifiers = @($readme | Where-Object { $_ -match '1532:[0-9A-Fa-f]{4}' })
if ($identifiers.Count -ne 0) {
    throw 'README must stay free of USB identifiers; they belong in AGENTS.md and the developer docs.'
}

Assert-UsbIdentity -Model 'RZ09-0581' -Expected '1532:02E0'
Assert-UsbIdentity -Model 'RZ09-0528' -Expected '1532:02C6'

Write-Host 'README hardware-support tests passed.'
