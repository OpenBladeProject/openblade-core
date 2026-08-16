[CmdletBinding()]
param(
    [Parameter(Mandatory)][AllowEmptyString()][string]$ValidateResult,
    [Parameter(Mandatory)][AllowEmptyString()][string]$QualityResult,
    [Parameter(Mandatory)][AllowEmptyString()][string]$ContractResult,
    [Parameter(Mandatory)][AllowEmptyString()][string]$ComponentPublishResult,
    [Parameter(Mandatory)][AllowEmptyString()][string]$InstallerResult,
    [Parameter(Mandatory)][ValidateSet('true', 'false')][string]$RunQuality,
    [Parameter(Mandatory)][ValidateSet('true', 'false')][string]$RunContracts,
    [Parameter(Mandatory)][ValidateSet('true', 'false')][string]$RunComponentPublish,
    [Parameter(Mandatory)][ValidateSet('true', 'false')][string]$RunInstaller
)

Set-StrictMode -Version 3.0
$ErrorActionPreference = 'Stop'

$lanes = @(
    [pscustomobject]@{
        Name = 'Validate build request'
        Result = $ValidateResult
        Enabled = 'true'
    }
    [pscustomobject]@{
        Name = 'Quality and tests'
        Result = $QualityResult
        Enabled = $RunQuality
    }
    [pscustomobject]@{
        Name = 'Build contracts'
        Result = $ContractResult
        Enabled = $RunContracts
    }
    [pscustomobject]@{
        Name = 'Component publish'
        Result = $ComponentPublishResult
        Enabled = $RunComponentPublish
    }
    [pscustomobject]@{
        Name = 'Installer validation'
        Result = $InstallerResult
        Enabled = $RunInstaller
    }
)
foreach ($lane in $lanes) {
    $expectedResult = if ($lane.Enabled -eq 'true') { 'success' } else { 'skipped' }
    if ($lane.Result -ne $expectedResult) {
        throw "$($lane.Name) finished with '$($lane.Result)'; expected '$expectedResult'."
    }
}

Write-Host 'All required CI validation lanes passed.'
