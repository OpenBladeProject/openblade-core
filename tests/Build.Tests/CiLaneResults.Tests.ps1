[CmdletBinding()]
param()

Set-StrictMode -Version 3.0
$ErrorActionPreference = 'Stop'
$repository = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$gateScript = Join-Path $repository 'build\Assert-CiLaneResults.ps1'

# Smoke marker: this test-only diff must skip installer validation in Actions.
function Assert-ThrowsLike {
    param(
        [Parameter(Mandatory)][scriptblock]$Action,
        [Parameter(Mandatory)][string]$Pattern,
        [Parameter(Mandatory)][string]$Message
    )

    try {
        & $Action
    }
    catch {
        if ($_.Exception.Message -like $Pattern) {
            return
        }

        throw "$Message Unexpected error: $($_.Exception.Message)"
    }

    throw $Message
}

& $gateScript `
    -ValidateResult success `
    -QualityResult success `
    -ContractResult success `
    -InstallerResult success `
    -RunInstaller true

& $gateScript `
    -ValidateResult success `
    -QualityResult success `
    -ContractResult success `
    -InstallerResult skipped `
    -RunInstaller false

Assert-ThrowsLike -Pattern "Quality and tests finished with 'cancelled'*" -Message `
    'A cancelled required lane must fail the final gate.' -Action {
    & $gateScript `
        -ValidateResult success `
        -QualityResult cancelled `
        -ContractResult success `
        -InstallerResult success `
        -RunInstaller true
}

Assert-ThrowsLike -Pattern "Installer validation finished with 'skipped'*" -Message `
    'A skipped installer lane must fail when installer validation is required.' -Action {
    & $gateScript `
        -ValidateResult success `
        -QualityResult success `
        -ContractResult success `
        -InstallerResult skipped `
        -RunInstaller true
}

Assert-ThrowsLike -Pattern "Installer validation finished with 'success'*" -Message `
    'An unexpected installer run must fail when installer validation is disabled.' -Action {
    & $gateScript `
        -ValidateResult success `
        -QualityResult success `
        -ContractResult success `
        -InstallerResult success `
        -RunInstaller false
}

Write-Host 'CI lane result tests passed.'
