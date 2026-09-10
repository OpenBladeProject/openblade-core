[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$repository = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
. (Join-Path $repository 'build\PublishPayloadValidation.ps1')

function Assert-True {
    param(
        [Parameter(Mandatory)][bool]$Condition,
        [Parameter(Mandatory)][string]$Message
    )

    if (-not $Condition) {
        throw $Message
    }
}

function Assert-ThrowsLike {
    param(
        [Parameter(Mandatory)][scriptblock]$Action,
        [Parameter(Mandatory)][string]$Pattern,
        [Parameter(Mandatory)][string]$Message
    )

    $caught = $null
    try {
        & $Action
    }
    catch {
        $caught = $_
    }
    Assert-True ($null -ne $caught) "$Message did not fail."
    Assert-True ($caught.Exception.Message -match $Pattern) `
        "$Message failed unexpectedly: $($caught.Exception.Message)"
}

$publish = Get-Content -Raw -LiteralPath (
    Join-Path $repository 'build\Publish.ps1')
$publishTokens = $null
$publishErrors = $null
[void][Management.Automation.Language.Parser]::ParseFile(
    (Join-Path $repository 'build\Publish.ps1'),
    [ref]$publishTokens,
    [ref]$publishErrors)
Assert-True ($publishErrors.Count -eq 0) `
    'Publish.ps1 contains a PowerShell parser error.'
Assert-True (
    $publish.IndexOf('SparsePackageIdentity.ps1', [StringComparison]::Ordinal) -ge 0) `
    'Publish.ps1 no longer loads the retained developer identity tooling.'
foreach ($retiredProductionIntegration in @(
        'Get-OpenBladeSigningCertificate',
        'New-OpenBladeSessionAgentIdentityManifest',
        '--property:OpenBladeSessionAgentManifestSource=',
        'New-OpenBladeDynamicLightingPackage',
        'dynamicLightingIdentity.PackagePath')) {
    Assert-True (
        $publish.IndexOf(
            $retiredProductionIntegration,
            [StringComparison]::Ordinal) -lt 0) `
        "Publish.ps1 still ships retired Dynamic Lighting identity integration '$retiredProductionIntegration'."
}

$testRoot = Join-Path ([IO.Path]::GetTempPath()) (
    'OpenBlade.SparseIdentityBoundary.Tests.' + [Guid]::NewGuid().ToString('N'))
try {
    New-Item -ItemType Directory -Path $testRoot -Force | Out-Null
    Assert-DynamicLightingIdentityPayload -Payload $testRoot
    [IO.File]::WriteAllBytes(
        (Join-Path $testRoot 'OpenBlade.DynamicLighting.msix'),
        [byte[]](1))
    Assert-ThrowsLike {
        Assert-DynamicLightingIdentityPayload -Payload $testRoot
    } 'must not be present in a production payload' `
        'Production payload validation accepted a retired identity package.'
}
finally {
    if (Test-Path -LiteralPath $testRoot) {
        Remove-Item -LiteralPath $testRoot -Recurse -Force
    }
}

Write-Host 'Sparse package identity production-boundary tests passed.'
