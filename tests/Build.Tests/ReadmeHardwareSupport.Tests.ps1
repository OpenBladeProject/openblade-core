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

    if ($cells[1] -match '\bexperimental\b') {
        throw "README must not classify supported model $Model as experimental."
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
Assert-ModelNumber -Model 'RZ09-0529' -Expected '`RZ09-0529`'
Assert-ModelNumber -Model 'RZ09-0508' -Expected '`RZ09-0508`'
Assert-ModelNumber -Model 'RZ09-0510' -Expected '`RZ09-0510`'
Assert-ModelNumber -Model 'RZ09-0509' -Expected '`RZ09-0509`'
Assert-ModelNumber -Model 'RZ09-0483' -Expected '`RZ09-0483`'
Assert-ModelNumber -Model 'RZ09-0482' -Expected '`RZ09-0482`'
Assert-ModelNumber -Model 'RZ09-0530' -Expected '`RZ09-0530`'
Assert-ModelNumber -Model 'RZ09-0300' -Expected '`RZ09-0300`'
Assert-ModelNumber -Model 'RZ09-0330' -Expected '`RZ09-0330`'
Assert-ModelNumber -Model 'RZ09-0351' -Expected '`RZ09-0351`'
Assert-ModelNumber -Model 'RZ09-0328' -Expected '`RZ09-0328`'
Assert-ModelNumber -Model 'RZ09-0370' -Expected '`RZ09-0370`'
Assert-ModelNumber -Model 'RZ09-0427' -Expected '`RZ09-0427`'
Assert-ModelNumber -Model 'RZ09-0421' -Expected '`RZ09-0421`'
Assert-ModelNumber -Model 'RZ09-0423' -Expected '`RZ09-0423`'
Assert-ModelNumber -Model 'RZ09-0484' -Expected '`RZ09-0484`'

$supportedRows = @($readme | Where-Object { $_ -match '^\| .*`RZ09-\d{4}`.*\|$' })
if ($supportedRows.Count -ne 18) {
    throw 'README must list all eighteen reviewed supported models.'
}
$supportText = $readme -join "`n"
if ($supportText -notmatch 'All listed laptops are officially supported\.') {
    throw 'README must distinguish official model support from physical validation.'
}
if ($supportText -notmatch 'RZ09-0581 and RZ09-0528 have\s+physically validated support;' -or
    $supportText -notmatch 'the other sixteen families are supported on reviewed\s+exact-model sources, with physical validation tracked separately\.') {
    throw 'README must retain the two physically validated models and separate source-reviewed support for the other sixteen.'
}
if ($supportText -notmatch '\[Source-reviewed Blade support\]\(docs/experimental-blade-support\.md\)') {
    throw 'README must link the source-reviewed model, recovery, and tester guide.'
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
Assert-UsbIdentity -Model 'RZ09-0529' -Expected '1532:02C7'
Assert-UsbIdentity -Model 'RZ09-0508' -Expected '1532:02B6'
Assert-UsbIdentity -Model 'RZ09-0510' -Expected '1532:02B7'
Assert-UsbIdentity -Model 'RZ09-0509' -Expected '1532:02B8'
Assert-UsbIdentity -Model 'RZ09-0483' -Expected '1532:029F'
Assert-UsbIdentity -Model 'RZ09-0482' -Expected '1532:029D'
Assert-UsbIdentity -Model 'RZ09-0530' -Expected '1532:02C5'
Assert-UsbIdentity -Model 'RZ09-0300' -Expected '1532:0246'
Assert-UsbIdentity -Model 'RZ09-0330' -Expected '1532:0253'
Assert-UsbIdentity -Model 'RZ09-0351' -Expected '1532:0268'
Assert-UsbIdentity -Model 'RZ09-0328' -Expected '1532:0255'
Assert-UsbIdentity -Model 'RZ09-0370' -Expected '1532:0270'
Assert-UsbIdentity -Model 'RZ09-0427' -Expected '1532:028C'
Assert-UsbIdentity -Model 'RZ09-0421' -Expected '1532:028A'
Assert-UsbIdentity -Model 'RZ09-0423' -Expected '1532:028B'
Assert-UsbIdentity -Model 'RZ09-0484' -Expected '1532:02A0'

Write-Host 'README hardware-support tests passed.'
