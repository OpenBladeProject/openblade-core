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

function Assert-DynamicLightingIdentityPayload {
    param([Parameter(Mandatory)][string]$Payload)

    $packagePath = Join-Path $Payload 'OpenBlade.DynamicLighting.msix'
    $descriptorPath = Join-Path $Payload 'OpenBlade.DynamicLighting.identity.json'
    $packageExists = Test-Path -LiteralPath $packagePath -PathType Leaf
    $descriptorExists = Test-Path -LiteralPath $descriptorPath -PathType Leaf
    if (-not $packageExists -and -not $descriptorExists) {
        return
    }
    if ($packageExists -ne $descriptorExists) {
        throw 'The Dynamic Lighting identity package and descriptor must either both be present or both be absent.'
    }

    try {
        $descriptor = Get-Content -Raw -LiteralPath $descriptorPath |
            ConvertFrom-Json
    }
    catch {
        throw "The Dynamic Lighting identity descriptor is invalid: $($_.Exception.Message)"
    }
    if ($descriptor.schemaVersion -ne 1 -or
        $descriptor.packageName -ne 'OSSBlade.OpenBlade.DynamicLighting' -or
        $descriptor.applicationId -ne 'SessionAgent' -or
        $descriptor.packageFile -ne 'OpenBlade.DynamicLighting.msix' -or
        $descriptor.externalLocation -ne 'OpenBlade install directory' -or
        [string]::IsNullOrWhiteSpace([string]$descriptor.publisher)) {
        throw 'The Dynamic Lighting identity descriptor does not match the reviewed schema.'
    }

    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $archive = $null
    $reader = $null
    try {
        $archive = [IO.Compression.ZipFile]::OpenRead($packagePath)
        $manifestEntry = @($archive.Entries | Where-Object {
            $_.FullName -eq 'AppxManifest.xml'
        })
        if ($manifestEntry.Count -ne 1) {
            throw 'The Dynamic Lighting identity package must contain exactly one AppxManifest.xml.'
        }
        $reader = [IO.StreamReader]::new(
            $manifestEntry[0].Open(),
            [Text.Encoding]::UTF8,
            $true)
        [xml]$manifest = $reader.ReadToEnd()
    }
    catch {
        throw "The Dynamic Lighting identity package is invalid: $($_.Exception.Message)"
    }
    finally {
        if ($null -ne $reader) {
            $reader.Dispose()
        }
        if ($null -ne $archive) {
            $archive.Dispose()
        }
    }

    $namespaces = [Xml.XmlNamespaceManager]::new($manifest.NameTable)
    $namespaces.AddNamespace(
        'f',
        'http://schemas.microsoft.com/appx/manifest/foundation/windows10')
    $namespaces.AddNamespace(
        'uap3',
        'http://schemas.microsoft.com/appx/manifest/uap/windows10/3')
    $namespaces.AddNamespace(
        'uap10',
        'http://schemas.microsoft.com/appx/manifest/uap/windows10/10')
    $identity = $manifest.SelectSingleNode(
        '/f:Package/f:Identity',
        $namespaces)
    $application = $manifest.SelectSingleNode(
        '/f:Package/f:Applications/f:Application',
        $namespaces)
    $allowExternal = $manifest.SelectSingleNode(
        '/f:Package/f:Properties/uap10:AllowExternalContent',
        $namespaces)
    $extension = $manifest.SelectSingleNode(
        '/f:Package/f:Applications/f:Application/f:Extensions/uap3:Extension/uap3:AppExtension',
        $namespaces)
    if ($null -eq $identity -or
        $identity.GetAttribute('Name') -ne [string]$descriptor.packageName -or
        $identity.GetAttribute('Publisher') -ne [string]$descriptor.publisher -or
        $null -eq $application -or
        $application.GetAttribute('Id') -ne [string]$descriptor.applicationId -or
        $application.GetAttribute('Executable') -ne 'OpenBlade.SessionAgent.exe' -or
        $null -eq $allowExternal -or
        $allowExternal.InnerText -ne 'true' -or
        $null -eq $extension -or
        $extension.GetAttribute('Name') -ne 'com.microsoft.windows.lighting' -or
        $extension.GetAttribute('PublicFolder') -ne 'public') {
        throw 'The Dynamic Lighting identity package manifest differs from its reviewed descriptor.'
    }
}

function Assert-ServicePublishPayload {
    param([Parameter(Mandatory)][string]$Payload)

    Assert-DynamicLightingIdentityPayload -Payload $Payload

    $requiredSessionAgentFiles = @(
        'OpenBlade.SessionAgent.exe',
        'OpenBlade.SessionAgent.dll',
        'OpenBlade.SessionAgent.deps.json',
        'OpenBlade.SessionAgent.runtimeconfig.json',
        'Microsoft.WindowsAppRuntime.Insights.Resource.dll'
    )
    foreach ($requiredSessionAgentFile in $requiredSessionAgentFiles) {
        $requiredPath = Join-Path $Payload $requiredSessionAgentFile
        if (-not (Test-Path -LiteralPath $requiredPath -PathType Leaf)) {
            throw "OpenBlade production payload is missing required session agent file: $requiredPath"
        }
    }

    $removedInputAgentFiles = @(
        'OpenBlade.InputAgent.exe',
        'OpenBlade.InputAgent.dll',
        'OpenBlade.InputAgent.deps.json',
        'OpenBlade.InputAgent.runtimeconfig.json'
    )
    $presentRemovedInputAgentFiles = @($removedInputAgentFiles | Where-Object {
        Test-Path -LiteralPath (Join-Path $Payload $_) -PathType Leaf
    })
    if ($presentRemovedInputAgentFiles.Count -ne 0) {
        throw 'OpenBlade production payload must not contain the abandoned UIAccess input agent.'
    }

    $requiredTrayUiFiles = @(
        'OpenBlade.Tray.pri',
        'App.xbf',
        'MainWindow.xbf'
    )
    foreach ($requiredTrayUiFile in $requiredTrayUiFiles) {
        $requiredPath = Join-Path $Payload $requiredTrayUiFile
        if (-not (Test-Path -LiteralPath $requiredPath -PathType Leaf)) {
            throw "OpenBlade production payload is missing required tray UI file: $requiredPath"
        }
    }

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

function Assert-LightingValidationProtocolPair {
    param(
        [Parameter(Mandatory)][string]$ServicePayload,
        [Parameter(Mandatory)][string]$DeveloperTools
    )

    $serviceProtocol = Join-Path $ServicePayload 'OpenBlade.Protocol.dll'
    $validationProtocol = Join-Path $DeveloperTools 'OpenBlade.Protocol.dll'
    foreach ($path in @($serviceProtocol, $validationProtocol)) {
        if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
            throw "OpenBlade lighting validation requires protocol assembly: $path"
        }
    }

    $serviceHash = (Get-FileHash -LiteralPath $serviceProtocol -Algorithm SHA256).Hash
    $validationHash = (Get-FileHash -LiteralPath $validationProtocol -Algorithm SHA256).Hash
    if (-not [string]::Equals($serviceHash, $validationHash, [StringComparison]::Ordinal)) {
        throw 'OpenBlade developer tools and service payload contain different Protocol binaries; lighting validation could not admit the installed service.'
    }
}

function Assert-ProductionBundleFootprint {
    param([Parameter(Mandatory)][string]$Bundle)

    $setup = Join-Path $Bundle 'OpenBlade.Setup.exe'
    if (-not (Test-Path -LiteralPath $setup -PathType Leaf)) {
        throw "OpenBlade production bundle is missing the setup executable: $setup"
    }

    $unexpectedRootFiles = @(Get-ChildItem -LiteralPath $Bundle -File -ErrorAction Stop |
        Where-Object { -not [string]::Equals(
            $_.Name,
            'OpenBlade.Setup.exe',
            [StringComparison]::OrdinalIgnoreCase) })
    if ($unexpectedRootFiles.Count -ne 0) {
        throw "OpenBlade setup must publish as one compressed executable; unexpected root file '$($unexpectedRootFiles[0].Name)'."
    }

    $payload = Join-Path $Bundle 'payload'
    $installedSetup = Join-Path $payload 'OpenBlade.Setup.exe'
    if (Test-Path -LiteralPath $installedSetup -PathType Leaf) {
        throw 'OpenBlade bundle must not pre-copy Setup into the payload; elevated setup installs its verified executable transactionally.'
    }

    $productionFiles = @(Get-ChildItem -LiteralPath $payload -File -Recurse -ErrorAction Stop)
    $forbiddenFileNames = @(
        'appsettings.Development.json',
        'DirectML.dll',
        'onnxruntime.dll',
        'Microsoft.ML.OnnxRuntime.dll'
    )
    $forbidden = @($productionFiles | Where-Object {
        $_.Extension.Equals('.pdb', [StringComparison]::OrdinalIgnoreCase) `
            -or ($_.Name.EndsWith('.resources.dll', [StringComparison]::OrdinalIgnoreCase) `
                -and -not [string]::Equals(
                    $_.DirectoryName,
                    $payload,
                    [StringComparison]::OrdinalIgnoreCase)) `
            -or [Linq.Enumerable]::Contains(
                [string[]]$forbiddenFileNames,
                [string]$_.Name,
                [StringComparer]::OrdinalIgnoreCase) `
            -or $_.Name.StartsWith('Microsoft.Windows.AI.', [StringComparison]::OrdinalIgnoreCase) `
            -or $_.Name.StartsWith('Microsoft.Windows.Widgets', [StringComparison]::OrdinalIgnoreCase)
    })
    if ($forbidden.Count -ne 0) {
        throw "OpenBlade production payload contains development or unused component file '$($forbidden[0].Name)'."
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
