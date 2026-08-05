[CmdletBinding()]
param(
    [Parameter(Mandatory)][AllowEmptyString()][string]$ValidateResult,
    [Parameter(Mandatory)][AllowEmptyString()][string]$QualityResult,
    [Parameter(Mandatory)][AllowEmptyString()][string]$ContractResult,
    [Parameter(Mandatory)][AllowEmptyString()][string]$InstallerResult,
    [Parameter(Mandatory)][ValidateSet('true', 'false')][string]$RunInstaller
)

Set-StrictMode -Version 3.0
$ErrorActionPreference = 'Stop'

$requiredLanes = @(
    [pscustomobject]@{ Name = 'Validate build request'; Result = $ValidateResult }
    [pscustomobject]@{ Name = 'Quality and tests'; Result = $QualityResult }
    [pscustomobject]@{ Name = 'Build contracts'; Result = $ContractResult }
)
foreach ($lane in $requiredLanes) {
    if ($lane.Result -ne 'success') {
        throw "$($lane.Name) finished with '$($lane.Result)'; expected 'success'."
    }
}

$expectedInstallerResult = if ($RunInstaller -eq 'true') {
    'success'
}
else {
    'skipped'
}
if ($InstallerResult -ne $expectedInstallerResult) {
    throw "Installer validation finished with '$InstallerResult'; expected '$expectedInstallerResult'."
}

Write-Host 'All required CI validation lanes passed.'
