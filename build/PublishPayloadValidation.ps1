$ErrorActionPreference = 'Stop'

function Get-ManifestPropertyByPackageName {
    param(
        [Parameter(Mandatory)][psobject]$Object,
        [Parameter(Mandatory)][string]$PackageName,
        [Parameter(Mandatory)][string]$Context
    )

    if ($null -eq $Object) {
        throw "OpenBlade service dependency manifest is missing $Context."
    }

    $prefix = $PackageName + '/'
    $property = $Object.PSObject.Properties |
        Where-Object { $_.Name.StartsWith($prefix, [StringComparison]::Ordinal) } |
        Select-Object -First 1

    if ($null -eq $property) {
        throw "OpenBlade service dependency manifest is missing $Context for $PackageName."
    }

    return $property.Value
}

function Assert-ServicePublishPayload {
    param([Parameter(Mandatory)][string]$Payload)

    $manifestPath = Join-Path $Payload 'OpenBlade.Service.deps.json'
    if (-not (Test-Path -LiteralPath $manifestPath -PathType Leaf)) {
        throw "OpenBlade service dependency manifest is missing: $manifestPath"
    }

    try {
        $manifest = Get-Content -Raw -LiteralPath $manifestPath | ConvertFrom-Json
    }
    catch {
        throw "OpenBlade service dependency manifest is invalid: $manifestPath. $($_.Exception.Message)"
    }

    $runtimeTargetName = $manifest.runtimeTarget.name
    if ([string]::IsNullOrWhiteSpace($runtimeTargetName)) {
        throw 'OpenBlade service dependency manifest is missing its runtime target name.'
    }

    $runtimeTarget = $manifest.targets.PSObject.Properties[$runtimeTargetName].Value
    if ($null -eq $runtimeTarget) {
        throw "OpenBlade service dependency manifest is missing runtime target $runtimeTargetName."
    }

    $requiredDependencies = [ordered]@{
        'Microsoft.Extensions.Hosting' = 'Microsoft.Extensions.Hosting.dll'
        'Microsoft.Extensions.Hosting.WindowsServices' = 'Microsoft.Extensions.Hosting.WindowsServices.dll'
    }

    foreach ($dependency in $requiredDependencies.GetEnumerator()) {
        $null = Get-ManifestPropertyByPackageName -Object $manifest.libraries -PackageName $dependency.Key -Context 'library entry'
        $targetEntry = Get-ManifestPropertyByPackageName -Object $runtimeTarget -PackageName $dependency.Key -Context "runtime target entry in $runtimeTargetName"
        $runtimeAssets = $targetEntry.runtime.PSObject.Properties.Name
        $expectedAssetSuffix = '/' + $dependency.Value
        $hasRuntimeAsset = @($runtimeAssets | Where-Object {
            $_.Replace('\', '/').EndsWith($expectedAssetSuffix, [StringComparison]::OrdinalIgnoreCase)
        }).Count -gt 0

        if (-not $hasRuntimeAsset) {
            throw "OpenBlade service dependency manifest is missing runtime asset $($dependency.Value) for $($dependency.Key)."
        }

        $assemblyPath = Join-Path $Payload $dependency.Value
        if (-not (Test-Path -LiteralPath $assemblyPath -PathType Leaf)) {
            throw "OpenBlade service payload is missing required assembly: $assemblyPath"
        }
    }
}

function Complete-ValidatedBundleReplacement {
    param(
        [Parameter(Mandatory)][string]$StagingBundle,
        [Parameter(Mandatory)][string]$Bundle
    )

    Assert-ServicePublishPayload -Payload (Join-Path $StagingBundle 'payload')

    if (Test-Path -LiteralPath $Bundle) {
        Remove-Item -LiteralPath $Bundle -Recurse -Force
    }

    New-Item -ItemType Directory -Path (Split-Path -Parent $Bundle) -Force | Out-Null
    Move-Item -LiteralPath $StagingBundle -Destination $Bundle
}
