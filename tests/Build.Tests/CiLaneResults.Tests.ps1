[CmdletBinding()]
param()

Set-StrictMode -Version 3.0
$ErrorActionPreference = 'Stop'
$repository = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$gateScript = Join-Path $repository 'build\Assert-CiLaneResults.ps1'

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
        if ($_.Exception.Message -like $Pattern) { return }
        throw "$Message Unexpected error: $($_.Exception.Message)"
    }

    throw $Message
}

$full = @{
    ValidateResult = 'success'
    QualityResult = 'success'
    ContractResult = 'success'
    ComponentPublishResult = 'skipped'
    InstallerResult = 'success'
    RunQuality = 'true'
    RunContracts = 'true'
    RunComponentPublish = 'false'
    RunInstaller = 'true'
}
& $gateScript @full

$affected = @{
    ValidateResult = 'success'
    QualityResult = 'success'
    ContractResult = 'skipped'
    ComponentPublishResult = 'success'
    InstallerResult = 'skipped'
    RunQuality = 'true'
    RunContracts = 'false'
    RunComponentPublish = 'true'
    RunInstaller = 'false'
}
& $gateScript @affected

$none = @{
    ValidateResult = 'success'
    QualityResult = 'skipped'
    ContractResult = 'skipped'
    ComponentPublishResult = 'skipped'
    InstallerResult = 'skipped'
    RunQuality = 'false'
    RunContracts = 'false'
    RunComponentPublish = 'false'
    RunInstaller = 'false'
}
& $gateScript @none

Assert-ThrowsLike -Pattern "Quality and tests finished with 'cancelled'*" -Message `
    'A cancelled selected lane must fail the final gate.' -Action {
    $failure = $affected.Clone()
    $failure.QualityResult = 'cancelled'
    & $gateScript @failure
}

Assert-ThrowsLike -Pattern "Build contracts finished with 'success'*expected 'skipped'*" -Message `
    'An unexpected unselected lane must fail the final gate.' -Action {
    $failure = $affected.Clone()
    $failure.ContractResult = 'success'
    & $gateScript @failure
}

Assert-ThrowsLike -Pattern "Component publish finished with 'skipped'*expected 'success'*" -Message `
    'A skipped selected component publish must fail the final gate.' -Action {
    $failure = $affected.Clone()
    $failure.ComponentPublishResult = 'skipped'
    & $gateScript @failure
}

Assert-ThrowsLike -Pattern "Installer validation finished with 'skipped'*expected 'success'*" -Message `
    'A skipped required installer lane must fail the final gate.' -Action {
    $failure = $full.Clone()
    $failure.InstallerResult = 'skipped'
    & $gateScript @failure
}

Write-Host 'CI lane result tests passed.'
