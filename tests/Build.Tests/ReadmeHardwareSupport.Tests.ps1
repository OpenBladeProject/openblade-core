[CmdletBinding()]
param()

Set-StrictMode -Version 3.0
$ErrorActionPreference = 'Stop'
$repository = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$readme = Get-Content -LiteralPath (Join-Path $repository 'README.md')

function Assert-McuLabels {
    param(
        [Parameter(Mandatory)][string]$Model,
        [Parameter(Mandatory)][string]$Expected
    )

    $row = @($readme | Where-Object { $_ -match "^\| .*``$Model`` .+\|$" })
    if ($row.Count -ne 1) {
        throw "Expected exactly one README hardware-support row for $Model."
    }

    $cells = $row[0].Split('|')
    $actual = $cells[$cells.Length - 2].Trim()
    if ($actual -cne $Expected) {
        throw "README MCU labels for $Model must be '$Expected'; received '$actual'."
    }
}

Assert-McuLabels -Model 'RZ09-0581' -Expected '`1.05`, `1.06`'
Assert-McuLabels -Model 'RZ09-0528' -Expected '`1.09`'

Write-Host 'README hardware-support tests passed.'
