[CmdletBinding()]
param()

Set-StrictMode -Version 3.0
$ErrorActionPreference = 'Stop'

foreach ($script in @(
        'Extract-WinUiLocalization.ps1',
        'Extract-InstallerLocalization.ps1',
        'Update-PseudoLocalization.ps1')) {
    & (Join-Path $PSScriptRoot $script)
}

Write-Host 'Updated authoritative en-US and qps-ploc resource catalogs.'
