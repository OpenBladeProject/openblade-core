function Get-WixCommandAssembly {
    param([Parameter(Mandatory)][string]$InstallerProject)

    [xml]$project = Get-Content -LiteralPath $InstallerProject
    $sdk = [string]$project.Project.Sdk
    $prefix = 'WixToolset.Sdk/'
    if (-not $sdk.StartsWith($prefix, [StringComparison]::Ordinal)) {
        throw "The installer project does not pin a WiX SDK version: $InstallerProject"
    }

    $version = $sdk.Substring($prefix.Length)
    $globalPackagesOutput = @(& dotnet nuget locals global-packages --list)
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet could not locate the NuGet global-packages folder."
    }

    $match = [regex]::Match(
        ($globalPackagesOutput -join [Environment]::NewLine),
        '(?m)^global-packages:\s*(.+?)\s*$')
    if (-not $match.Success) {
        throw "Unexpected dotnet global-packages output: $($globalPackagesOutput -join ' ')"
    }

    $wix = Join-Path $match.Groups[1].Value.Trim() (
        "wixtoolset.sdk\$version\tools\net8.0\wix.dll")
    if (-not (Test-Path -LiteralPath $wix -PathType Leaf)) {
        throw "The restored WiX command assembly is missing: $wix"
    }

    return $wix
}

function Assert-OpenBladeInstallerBundle {
    param(
        [Parameter(Mandatory)][string]$Installer,
        [Parameter(Mandatory)][string]$PublishedBundle,
        [Parameter(Mandatory)][string]$InstallerProject,
        [Parameter(Mandatory)][string]$ExpectedVersion,
        [Parameter(Mandatory)][string]$ValidationRoot,
        [Parameter(Mandatory)][string]$PermittedRoot
    )

    if (-not (Test-Path -LiteralPath $Installer -PathType Leaf)) {
        throw "OpenBlade installer artifact is missing: $Installer"
    }
    if ((Get-Item -LiteralPath $Installer).Length -lt 1MB) {
        throw "OpenBlade installer artifact is unexpectedly small: $Installer"
    }

    $versionInfo = [Diagnostics.FileVersionInfo]::GetVersionInfo($Installer)
    if (-not [string]::Equals(
            $versionInfo.ProductName,
            'OpenBlade',
            [StringComparison]::Ordinal)) {
        throw "OpenBlade installer has unexpected product name '$($versionInfo.ProductName)'."
    }
    if (-not [string]::Equals(
            $versionInfo.ProductVersion,
            $ExpectedVersion,
            [StringComparison]::Ordinal)) {
        throw "OpenBlade installer version '$($versionInfo.ProductVersion)' does not match '$ExpectedVersion'."
    }

    $canonicalRoot = Get-CanonicalPublishPath -Path $PermittedRoot -MustExist
    $canonicalValidation = Get-CanonicalPublishPath -Path $ValidationRoot
    Assert-StrictPublishDescendant `
        -PermittedRoot $canonicalRoot `
        -Candidate $canonicalValidation `
        -Context 'installer validation directory'
    if (Test-Path -LiteralPath $canonicalValidation) {
        throw "Installer validation directory must not already exist: $canonicalValidation"
    }

    $payloadOutput = Join-Path $canonicalValidation 'payload'
    $bootstrapperOutput = Join-Path $canonicalValidation 'bootstrapper'
    try {
        New-Item -ItemType Directory -Path $payloadOutput,$bootstrapperOutput -Force | Out-Null
        $wix = Get-WixCommandAssembly -InstallerProject $InstallerProject
        & dotnet $wix -acceptEula wix7 burn extract $Installer `
            -out $payloadOutput `
            -outba $bootstrapperOutput
        if ($LASTEXITCODE -ne 0) {
            throw "WiX failed to extract the OpenBlade installer with exit code $LASTEXITCODE."
        }

        $manifestPath = Join-Path $bootstrapperOutput 'manifest.xml'
        if (-not (Test-Path -LiteralPath $manifestPath -PathType Leaf)) {
            throw "The extracted Burn manifest is missing: $manifestPath"
        }

        [xml]$manifest = Get-Content -LiteralPath $manifestPath
        $namespaces = [Xml.XmlNamespaceManager]::new($manifest.NameTable)
        $namespaces.AddNamespace('burn', 'http://wixtoolset.org/schemas/v4/2008/Burn')

        $registration = $manifest.SelectSingleNode(
            '/burn:BurnManifest/burn:Registration',
            $namespaces)
        if (($null -eq $registration) -or
            ($registration.GetAttribute('BundleId') -ne 'OSSBlade.OpenBlade') -or
            ($registration.GetAttribute('ProviderKey') -ne 'OSSBlade.OpenBlade') -or
            ($registration.GetAttribute('Scope') -ne 'perMachine') -or
            ($registration.GetAttribute('Version') -ne $ExpectedVersion)) {
            throw "The Burn manifest has an unexpected bundle registration."
        }

        $primaryUpgradeCode = $registration.GetAttribute('PrimaryUpgradeCode')
        $parsedUpgradeCode = [Guid]::Empty
        if (-not [Guid]::TryParse($primaryUpgradeCode, [ref]$parsedUpgradeCode)) {
            throw "The Burn manifest does not contain a valid stable upgrade-family code."
        }

        $relatedBundles = @($manifest.SelectNodes(
                '/burn:BurnManifest/burn:RelatedBundle',
                $namespaces))
        if (($relatedBundles.Count -ne 1) -or
            ($relatedBundles[0].GetAttribute('Code') -ne $primaryUpgradeCode) -or
            ($relatedBundles[0].GetAttribute('Action') -ne 'Upgrade')) {
            throw "The Burn manifest does not upgrade exactly its stable OpenBlade bundle family."
        }

        $arp = $registration.SelectSingleNode('burn:Arp', $namespaces)
        if (($null -eq $arp) -or
            ($arp.GetAttribute('DisplayName') -ne 'OpenBlade') -or
            ($arp.GetAttribute('DisplayVersion') -ne $ExpectedVersion) -or
            ($arp.GetAttribute('Publisher') -ne 'OSSBlade')) {
            throw "The Burn manifest has unexpected Installed apps metadata."
        }

        $package = $manifest.SelectSingleNode(
            '/burn:BurnManifest/burn:Chain/burn:ExePackage[@Id="OpenBladeSetup"]',
            $namespaces)
        if ($null -eq $package) {
            throw "The Burn manifest is missing the OpenBladeSetup executable package."
        }

        $expectedAttributes = [ordered]@{
            InstallArguments = 'install --bundle-managed'
            RepairArguments = 'install --bundle-managed'
            UninstallArguments = 'uninstall --bundle-managed'
            DetectCondition = 'OpenBladeBundleManaged'
            Scope = 'perMachine'
            Vital = 'yes'
        }
        foreach ($entry in $expectedAttributes.GetEnumerator()) {
            if (-not [string]::Equals(
                    [string]$package.GetAttribute($entry.Key),
                    [string]$entry.Value,
                    [StringComparison]::Ordinal)) {
                throw "OpenBladeSetup manifest attribute $($entry.Key) is not '$($entry.Value)'."
            }
        }

        $search = $manifest.SelectSingleNode(
            '/burn:BurnManifest/burn:RegistrySearch[@Id="OpenBladeBundleOwnershipSearch"]',
            $namespaces)
        if (($null -eq $search) -or
            ($search.GetAttribute('Variable') -ne 'OpenBladeBundleManaged') -or
            ($search.GetAttribute('Root') -ne 'HKLM') -or
            ($search.GetAttribute('Key') -ne 'SOFTWARE\OSSBlade\OpenBlade\Installer') -or
            ($search.GetAttribute('Type') -ne 'exists') -or
            ($search.GetAttribute('Win64') -ne 'yes')) {
            throw "The Burn manifest has an unexpected OpenBlade ownership detection search."
        }

        $payloadsById = @{}
        foreach ($payload in $manifest.SelectNodes(
                '/burn:BurnManifest/burn:Payload',
                $namespaces)) {
            $payloadsById[$payload.GetAttribute('Id')] = $payload.GetAttribute('FilePath')
        }

        $actualPaths = [Collections.Generic.HashSet[string]]::new(
            [StringComparer]::Ordinal)
        foreach ($reference in $package.SelectNodes('burn:PayloadRef', $namespaces)) {
            $id = $reference.GetAttribute('Id')
            if (-not $payloadsById.ContainsKey($id)) {
                throw "OpenBladeSetup references missing Burn payload '$id'."
            }
            if (-not $actualPaths.Add([string]$payloadsById[$id])) {
                throw "OpenBladeSetup contains a duplicate payload path '$($payloadsById[$id])'."
            }
        }

        $expectedPaths = [Collections.Generic.HashSet[string]]::new(
            [StringComparer]::Ordinal)
        [void]$expectedPaths.Add('OpenBlade.Setup.exe')
        $publishedPayload = [IO.Path]::GetFullPath(
            (Join-Path $PublishedBundle 'payload')).TrimEnd(
                [IO.Path]::DirectorySeparatorChar)
        $publishedPayloadPrefix = $publishedPayload + [IO.Path]::DirectorySeparatorChar
        foreach ($file in Get-ChildItem -LiteralPath $publishedPayload -Recurse -File) {
            if (-not $file.FullName.StartsWith(
                    $publishedPayloadPrefix,
                    [StringComparison]::OrdinalIgnoreCase)) {
                throw "Published payload enumeration escaped its expected root: $($file.FullName)"
            }
            $relative = $file.FullName.Substring($publishedPayloadPrefix.Length)
            [void]$expectedPaths.Add("payload\$relative")
        }

        if (-not $actualPaths.SetEquals($expectedPaths)) {
            $missing = @($expectedPaths | Where-Object { -not $actualPaths.Contains($_) })
            $unexpected = @($actualPaths | Where-Object { -not $expectedPaths.Contains($_) })
            throw "The Burn payload layout differs from the validated publish bundle. Missing: " `
                + "$($missing -join ', '). Unexpected: $($unexpected -join ', ')."
        }
    }
    finally {
        if (Test-Path -LiteralPath $canonicalValidation) {
            Remove-ValidatedPublishPath `
                -Path $canonicalValidation `
                -PermittedRoot $canonicalRoot
        }
    }
}
