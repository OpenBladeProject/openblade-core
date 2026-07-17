$ErrorActionPreference = 'Stop'

function Get-UniqueManifestPackageProperty {
    param(
        [Parameter(Mandatory)][psobject]$Object,
        [Parameter(Mandatory)][string]$PackageName,
        [Parameter(Mandatory)][string]$Context
    )

    if ($null -eq $Object) {
        throw "OpenBlade service dependency manifest is missing $Context."
    }

    $prefix = $PackageName + '/'
    $properties = @($Object.PSObject.Properties | Where-Object {
        $_.Name.StartsWith($prefix, [StringComparison]::Ordinal)
    })
    if ($properties.Count -ne 1) {
        throw "OpenBlade service dependency manifest must contain exactly one $Context for $PackageName."
    }

    return $properties[0]
}

function Get-ManifestPackageVersion {
    param(
        [Parameter(Mandatory)][string]$PropertyName,
        [Parameter(Mandatory)][string]$PackageName
    )

    $prefix = $PackageName + '/'
    if (-not $PropertyName.StartsWith($prefix, [StringComparison]::Ordinal)) {
        throw "Invalid package entry $PropertyName for $PackageName."
    }

    $version = $PropertyName.Substring($prefix.Length)
    if ([string]::IsNullOrWhiteSpace($version)) {
        throw "OpenBlade service dependency manifest has an empty version for $PackageName."
    }

    return $version
}

function Assert-ManagedAssemblyIdentity {
    param(
        [Parameter(Mandatory)][string]$Path,
        [Parameter(Mandatory)][string]$ExpectedName
    )

    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) {
        throw "OpenBlade service payload is missing required assembly: $Path"
    }

    try {
        $identity = [Reflection.AssemblyName]::GetAssemblyName($Path)
    }
    catch {
        throw "OpenBlade service payload assembly is not a loadable managed assembly: $Path. $($_.Exception.Message)"
    }

    if (-not [string]::Equals($identity.Name, $ExpectedName, [StringComparison]::Ordinal)) {
        throw "OpenBlade service payload '$Path' has managed assembly name '$($identity.Name)' instead of '$ExpectedName'."
    }
}

function Assert-ServicePublishPayload {
    param([Parameter(Mandatory)][string]$Payload)

    $forbiddenCaptureTool = Get-ChildItem -LiteralPath $Payload -File -Recurse -ErrorAction Stop |
        Where-Object { $_.Name.StartsWith('OpenBlade.Capture', [StringComparison]::OrdinalIgnoreCase) } |
        Select-Object -First 1
    if ($null -ne $forbiddenCaptureTool) {
        throw "OpenBlade production payload must not include developer tool '$($forbiddenCaptureTool.FullName)'."
    }

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

    $runtimeTargetName = [string]$manifest.runtimeTarget.name
    if (-not [string]::Equals(
            $runtimeTargetName,
            '.NETCoreApp,Version=v10.0/win-x64',
            [StringComparison]::Ordinal)) {
        throw "OpenBlade service dependency manifest has an unexpected runtime target '$runtimeTargetName'."
    }

    $runtimeTargetProperty = $manifest.targets.PSObject.Properties[$runtimeTargetName]
    if ($null -eq $runtimeTargetProperty -or $null -eq $runtimeTargetProperty.Value) {
        throw "OpenBlade service dependency manifest is missing runtime target $runtimeTargetName."
    }
    $runtimeTarget = $runtimeTargetProperty.Value

    $serviceEntry = Get-UniqueManifestPackageProperty `
        -Object $runtimeTarget `
        -PackageName 'OpenBlade.Service' `
        -Context "service entry in $runtimeTargetName"
    $serviceDependencies = $serviceEntry.Value.dependencies
    if ($null -eq $serviceDependencies) {
        throw 'OpenBlade service dependency manifest is missing service dependencies.'
    }

    $requiredDependencies = [ordered]@{
        'Microsoft.Extensions.Hosting' = 'Microsoft.Extensions.Hosting.dll'
        'Microsoft.Extensions.Hosting.WindowsServices' = 'Microsoft.Extensions.Hosting.WindowsServices.dll'
    }

    foreach ($dependency in $requiredDependencies.GetEnumerator()) {
        $libraryProperty = Get-UniqueManifestPackageProperty `
            -Object $manifest.libraries `
            -PackageName $dependency.Key `
            -Context 'library entry'
        $targetProperty = Get-UniqueManifestPackageProperty `
            -Object $runtimeTarget `
            -PackageName $dependency.Key `
            -Context "runtime target entry in $runtimeTargetName"
        $libraryVersion = Get-ManifestPackageVersion `
            -PropertyName $libraryProperty.Name `
            -PackageName $dependency.Key
        $targetVersion = Get-ManifestPackageVersion `
            -PropertyName $targetProperty.Name `
            -PackageName $dependency.Key
        $declaredVersion = [string]$serviceDependencies.PSObject.Properties[$dependency.Key].Value
        if ([string]::IsNullOrWhiteSpace($declaredVersion) `
            -or -not [string]::Equals($libraryVersion, $targetVersion, [StringComparison]::Ordinal) `
            -or -not [string]::Equals($libraryVersion, $declaredVersion, [StringComparison]::Ordinal)) {
            throw "OpenBlade service dependency manifest has incoherent versions for $($dependency.Key)."
        }

        if (-not [string]::Equals([string]$libraryProperty.Value.type, 'package', [StringComparison]::Ordinal)) {
            throw "OpenBlade service dependency manifest library $($dependency.Key) is not a package."
        }

        $runtimeObject = $targetProperty.Value.runtime
        if ($null -eq $runtimeObject) {
            throw "OpenBlade service dependency manifest is missing runtime entry for $($dependency.Key)."
        }
        $runtimeAssets = @($runtimeObject.PSObject.Properties.Name)
        $expectedAssetSuffix = '/' + $dependency.Value
        $matchingAssets = @($runtimeAssets | Where-Object {
            $null -ne $_ -and $_.Replace('\', '/').EndsWith(
                $expectedAssetSuffix,
                [StringComparison]::OrdinalIgnoreCase)
        })
        if ($matchingAssets.Count -ne 1) {
            throw "OpenBlade service dependency manifest must contain exactly one runtime asset $($dependency.Value) for $($dependency.Key)."
        }

        if ([string]::Equals(
                $dependency.Key,
                'Microsoft.Extensions.Hosting.WindowsServices',
                [StringComparison]::Ordinal)) {
            $hostingDependencyVersion = [string]$targetProperty.Value.dependencies.PSObject.Properties[
                'Microsoft.Extensions.Hosting'].Value
            $declaredHostingVersion = [string]$serviceDependencies.PSObject.Properties[
                'Microsoft.Extensions.Hosting'].Value
            if ([string]::IsNullOrWhiteSpace($hostingDependencyVersion) `
                -or -not [string]::Equals(
                    $hostingDependencyVersion,
                    $declaredHostingVersion,
                    [StringComparison]::Ordinal)) {
                throw 'OpenBlade service dependency manifest Hosting dependency version does not match.'
            }
        }

        Assert-ManagedAssemblyIdentity `
            -Path (Join-Path $Payload $dependency.Value) `
            -ExpectedName $dependency.Key
    }
}

function Get-CanonicalPublishPath {
    param(
        [Parameter(Mandatory)][string]$Path,
        [switch]$MustExist
    )

    $fullPath = [IO.Path]::GetFullPath($Path).TrimEnd([IO.Path]::DirectorySeparatorChar)
    if ($MustExist) {
        if (-not (Test-Path -LiteralPath $fullPath)) {
            throw "Required publish path does not exist: $fullPath"
        }
        return (Resolve-Path -LiteralPath $fullPath).ProviderPath.TrimEnd([IO.Path]::DirectorySeparatorChar)
    }

    if (Test-Path -LiteralPath $fullPath) {
        return (Resolve-Path -LiteralPath $fullPath).ProviderPath.TrimEnd([IO.Path]::DirectorySeparatorChar)
    }
    return $fullPath
}

function Assert-StrictPublishDescendant {
    param(
        [Parameter(Mandatory)][string]$PermittedRoot,
        [Parameter(Mandatory)][string]$Candidate,
        [Parameter(Mandatory)][string]$Context
    )

    $rootPrefix = $PermittedRoot + [IO.Path]::DirectorySeparatorChar
    if (-not $Candidate.StartsWith($rootPrefix, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Refusing to use $Context outside the permitted root: $Candidate"
    }

    $rootItem = Get-Item -LiteralPath $PermittedRoot -Force
    if (($rootItem.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
        throw "Refusing to use reparse-point permitted root: $PermittedRoot"
    }

    $relative = $Candidate.Substring($rootPrefix.Length)
    $current = $PermittedRoot
    foreach ($segment in $relative.Split(
            [IO.Path]::DirectorySeparatorChar,
            [StringSplitOptions]::RemoveEmptyEntries)) {
        $current = Join-Path $current $segment
        if (-not (Test-Path -LiteralPath $current)) {
            break
        }
        $item = Get-Item -LiteralPath $current -Force
        if (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
            throw "Refusing to use $Context through reparse point: $current"
        }
    }
}

function Remove-ValidatedPublishPath {
    param(
        [Parameter(Mandatory)][string]$Path,
        [Parameter(Mandatory)][string]$PermittedRoot
    )

    if (-not (Test-Path -LiteralPath $Path)) {
        return
    }

    $canonicalRoot = Get-CanonicalPublishPath -Path $PermittedRoot -MustExist
    $canonicalPath = Get-CanonicalPublishPath -Path $Path -MustExist
    Assert-StrictPublishDescendant `
        -PermittedRoot $canonicalRoot `
        -Candidate $canonicalPath `
        -Context 'publish cleanup path'
    Remove-Item -LiteralPath $canonicalPath -Recurse -Force
}

function Complete-ValidatedBundleReplacement {
    param(
        [Parameter(Mandatory)][string]$StagingBundle,
        [Parameter(Mandatory)][string]$Bundle,
        [Parameter(Mandatory)][string]$PermittedRoot
    )

    $canonicalRoot = Get-CanonicalPublishPath -Path $PermittedRoot -MustExist
    $canonicalStaging = Get-CanonicalPublishPath -Path $StagingBundle -MustExist
    $canonicalBundle = Get-CanonicalPublishPath -Path $Bundle
    Assert-StrictPublishDescendant -PermittedRoot $canonicalRoot -Candidate $canonicalStaging -Context 'staging bundle'
    Assert-StrictPublishDescendant -PermittedRoot $canonicalRoot -Candidate $canonicalBundle -Context 'bundle destination'
    if ([string]::Equals($canonicalStaging, $canonicalBundle, [StringComparison]::OrdinalIgnoreCase)) {
        throw 'The staging bundle and bundle destination must be different paths.'
    }

    Assert-ServicePublishPayload -Payload (Join-Path $canonicalStaging 'payload')

    # Re-resolve immediately before mutation so a replaced path cannot bypass the boundary.
    $canonicalStaging = Get-CanonicalPublishPath -Path $canonicalStaging -MustExist
    $canonicalBundle = Get-CanonicalPublishPath -Path $canonicalBundle
    Assert-StrictPublishDescendant -PermittedRoot $canonicalRoot -Candidate $canonicalStaging -Context 'staging bundle'
    Assert-StrictPublishDescendant -PermittedRoot $canonicalRoot -Candidate $canonicalBundle -Context 'bundle destination'

    if (Test-Path -LiteralPath $canonicalBundle) {
        Remove-Item -LiteralPath $canonicalBundle -Recurse -Force
    }

    New-Item -ItemType Directory -Path (Split-Path -Parent $canonicalBundle) -Force | Out-Null
    Move-Item -LiteralPath $canonicalStaging -Destination $canonicalBundle
}
