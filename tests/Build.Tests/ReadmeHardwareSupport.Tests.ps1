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
        [Parameter(Mandatory)][string]$Expected,
        [ValidateSet("Validated", "Experimental")][string]$Tier = "Validated"
    )

    $row = @($readme | Where-Object { $_ -match "^\| .*``$Model``.*\|$" })
    if ($row.Count -ne 1) {
        throw "Expected exactly one README hardware-support row for $Model."
    }

    $cells = $row[0].Split('|')
    if ($cells.Length -ne 4) {
        throw "README hardware-support row for $Model must contain only laptop and model number."
    }

    $experimental = $cells[1] -match '\bexperimental\b'
    if ($experimental -ne ($Tier -eq 'Experimental')) {
        throw "README must label $Model as $Tier support."
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
Assert-ModelNumber -Model 'RZ09-0483' -Expected '`RZ09-0483`' -Tier Experimental
Assert-ModelNumber -Model 'RZ09-0482' -Expected '`RZ09-0482`' -Tier Experimental
Assert-ModelNumber -Model 'RZ09-0530' -Expected '`RZ09-0530`' -Tier Experimental
Assert-ModelNumber -Model 'RZ09-0328' -Expected '`RZ09-0328`' -Tier Experimental
Assert-ModelNumber -Model 'RZ09-0484' -Expected '`RZ09-0484`' -Tier Experimental

if (($readme -join "`n") -notmatch '\[Experimental support\]\(docs/experimental-blade-support\.md\)') {
    throw 'README must link the experimental model, recovery, and tester guide.'
}

$previousYear = [int]::MaxValue
foreach ($row in @($readme | Where-Object { $_ -match '^\| .*`RZ09-\d{4}`.*\|$' })) {
    $yearMatch = [regex]::Match($row.Split('|')[1], '20\d{2}')
    if (-not $yearMatch.Success) {
        throw 'README laptops must include their model year.'
    }
    $year = [int]$yearMatch.Value
    if ($year -gt $previousYear) {
        throw 'README laptops must be ordered by model year, newest first.'
    }
    $previousYear = $year
}

# The README is written for laptop owners, so USB identifiers stay out of it and
# are pinned where the admitted targets are declared instead.
$identifiers = @($readme | Where-Object { $_ -match '1532:[0-9A-Fa-f]{4}' })
if ($identifiers.Count -ne 0) {
    throw 'README must stay free of USB identifiers; they belong in AGENTS.md and the developer docs.'
}

Assert-UsbIdentity -Model 'RZ09-0581' -Expected '1532:02E0'
Assert-UsbIdentity -Model 'RZ09-0528' -Expected '1532:02C6'
Assert-UsbIdentity -Model 'RZ09-0483' -Expected '1532:029F'
Assert-UsbIdentity -Model 'RZ09-0482' -Expected '1532:029D'
Assert-UsbIdentity -Model 'RZ09-0530' -Expected '1532:02C5'
Assert-UsbIdentity -Model 'RZ09-0328' -Expected '1532:0255'
Assert-UsbIdentity -Model 'RZ09-0484' -Expected '1532:02A0'

Write-Host 'README hardware-support tests passed.'
