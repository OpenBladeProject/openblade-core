Set-StrictMode -Version 3.0
$ErrorActionPreference = 'Stop'

if (-not (Get-Command Get-CanonicalPublishPath -ErrorAction SilentlyContinue)) {
    . (Join-Path $PSScriptRoot 'PublishPayloadValidation.ps1')
}

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
    $intermediateOutput = Join-Path $canonicalValidation 'intermediate'
    try {
        New-Item -ItemType Directory `
            -Path $payloadOutput,$bootstrapperOutput,$intermediateOutput `
            -Force | Out-Null
        $wix = Get-WixCommandAssembly -InstallerProject $InstallerProject
        & dotnet $wix -acceptEula wix7 burn extract $Installer `
            -out $payloadOutput `
            -outba $bootstrapperOutput `
            -intermediateFolder $intermediateOutput
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
        $condition = $manifest.SelectSingleNode(
            '/burn:BurnManifest/burn:Condition',
            $namespaces)
        if (($manifest.DocumentElement.GetAttribute('Win64') -ne 'yes') -or
            ($null -eq $condition) -or
            ($condition.InnerText -ne (
                'VersionNT64 >= v10.0 AND WindowsBuildNumber >= 26100'))) {
            throw "The Burn manifest does not enforce the Windows 11 24H2 x64 platform gate."
        }

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

        $expectedUpgradeCode = [Guid]'FF859CA9-D69F-4995-A2AD-69E540E45724'
        $primaryUpgradeCode = $registration.GetAttribute('PrimaryUpgradeCode')
        $parsedUpgradeCode = [Guid]::Empty
        if ((-not [Guid]::TryParse($primaryUpgradeCode, [ref]$parsedUpgradeCode)) -or
            ($parsedUpgradeCode -ne $expectedUpgradeCode)) {
            throw "The Burn manifest does not contain the pinned OpenBlade upgrade-family code."
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
            DetectCondition = "OpenBladeInstalledVersion AND OpenBladeInstalledVersion >= v$ExpectedVersion"
            Scope = 'perMachine'
            Cache = 'keep'
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

        $purgeVariable = $manifest.SelectSingleNode(
            '/burn:BurnManifest/burn:Variable[@Id="OpenBladePurgeProfiles"]',
            $namespaces)
        if (($null -eq $purgeVariable) -or
            ($purgeVariable.GetAttribute('Type') -ne 'numeric') -or
            ($purgeVariable.GetAttribute('Value') -ne '0')) {
            throw "The Burn manifest does not default profile removal to off."
        }

        $purgeCommandLine = $package.SelectSingleNode(
            'burn:CommandLine[@UninstallArgument="--purge"]',
            $namespaces)
        if (($null -eq $purgeCommandLine) -or
            ($purgeCommandLine.GetAttribute('Condition') -ne
                'OpenBladePurgeProfiles')) {
            throw "The Burn manifest does not conditionally append the purge argument."
        }

        $launchTarget = $manifest.SelectSingleNode(
            '/burn:BurnManifest/burn:Variable[@Id="LaunchTarget"]',
            $namespaces)
        $launchWorkingFolder = $manifest.SelectSingleNode(
            '/burn:BurnManifest/burn:Variable[@Id="LaunchWorkingFolder"]',
            $namespaces)
        if (($null -eq $launchTarget) -or
            ($launchTarget.GetAttribute('Value') -ne
                '[ProgramFiles64Folder]OpenBlade\OpenBlade.Tray.exe') -or
            ($launchTarget.GetAttribute('Type') -ne 'formatted') -or
            ($launchTarget.GetAttribute('Hidden') -ne 'no') -or
            ($launchTarget.GetAttribute('Persisted') -ne 'no') -or
            ($null -eq $launchWorkingFolder) -or
            ($launchWorkingFolder.GetAttribute('Value') -ne
                '[ProgramFiles64Folder]OpenBlade') -or
            ($launchWorkingFolder.GetAttribute('Type') -ne 'formatted') -or
            ($launchWorkingFolder.GetAttribute('Hidden') -ne 'no') -or
            ($launchWorkingFolder.GetAttribute('Persisted') -ne 'no')) {
            throw "The extracted installer does not launch the installed OpenBlade Tray."
        }

        $themePath = Join-Path $bootstrapperOutput 'thm.xml'
        [xml]$theme = Get-Content -LiteralPath $themePath
        $purgeCheckbox = $theme.SelectSingleNode(
            "/*[local-name()='Theme']/*[local-name()='Window']/*[local-name()='Page'][@Name='Modify']/*[local-name()='Checkbox'][@Name='OpenBladePurgeProfiles']")
        if ($null -eq $purgeCheckbox) {
            throw "The extracted maintenance UI does not contain the profile-removal checkbox."
        }

        $launchButton = $theme.SelectSingleNode(
            "/*[local-name()='Theme']/*[local-name()='Window']/*[local-name()='Page'][@Name='Success']/*[local-name()='Button'][@Name='LaunchButton']")
        if (($null -eq $launchButton) -or
            ($launchButton.GetAttribute('VisibleCondition') -ne
                'WixBundleAction = 5 OR WixBundleAction = 7')) {
            throw "The extracted installer does not limit Launch to install and repair."
        }

        $search = $manifest.SelectSingleNode(
            '/burn:BurnManifest/burn:RegistrySearch[@Id="OpenBladeBundleOwnershipSearch"]',
            $namespaces)
        if (($null -eq $search) -or
            ($search.GetAttribute('Variable') -ne 'OpenBladeInstalledVersion') -or
            ($search.GetAttribute('Root') -ne 'HKLM') -or
            ($search.GetAttribute('Key') -ne 'SOFTWARE\OSSBlade\OpenBlade\Installer') -or
            ($search.GetAttribute('Value') -ne 'InstalledVersion') -or
            ($search.GetAttribute('Type') -ne 'value') -or
            ($search.GetAttribute('Win64') -ne 'yes')) {
            throw "The Burn manifest has an unexpected OpenBlade ownership detection search."
        }

        $vcPackage = $manifest.SelectSingleNode(
            '/burn:BurnManifest/burn:Chain/burn:ExePackage[@Id="OpenBladeVcRuntime"]',
            $namespaces)
        $expectedVcDetectCondition = @(
            'OpenBladeVcRuntimeInstalled AND OpenBladeVcRuntimeMajor AND '
            'OpenBladeVcRuntimeMinor AND OpenBladeVcRuntimeBuild AND '
            'OpenBladeVcRuntimeInstalled = 1 AND '
            '(OpenBladeVcRuntimeMajor > 14 OR '
            '(OpenBladeVcRuntimeMajor = 14 AND '
            '(OpenBladeVcRuntimeMinor > 50 OR '
            '(OpenBladeVcRuntimeMinor = 50 AND '
            'OpenBladeVcRuntimeBuild >= 35719))))'
        ) -join ''
        if (($null -eq $vcPackage) -or
            ($vcPackage.GetAttribute('InstallArguments') -ne '/install /quiet /norestart') -or
            ($vcPackage.GetAttribute('RepairArguments') -ne '/repair /quiet /norestart') -or
            ($vcPackage.GetAttribute('DetectCondition') -ne
                $expectedVcDetectCondition) -or
            ($vcPackage.GetAttribute('Permanent') -ne 'yes') -or
            ($vcPackage.GetAttribute('Cache') -ne 'remove') -or
            ($vcPackage.GetAttribute('Scope') -ne 'perMachine') -or
            ($vcPackage.GetAttribute('Vital') -ne 'yes')) {
            throw "The Burn manifest has an unexpected Visual C++ runtime package."
        }

        $vcSearch = $manifest.SelectSingleNode(
            '/burn:BurnManifest/burn:RegistrySearch[@Id="OpenBladeVcRuntimeSearch"]',
            $namespaces)
        if (($null -eq $vcSearch) -or
            ($vcSearch.GetAttribute('Variable') -ne 'OpenBladeVcRuntimeInstalled') -or
            ($vcSearch.GetAttribute('Root') -ne 'HKLM') -or
            ($vcSearch.GetAttribute('Key') -ne 'SOFTWARE\Microsoft\VisualStudio\14.0\VC\Runtimes\x64') -or
            ($vcSearch.GetAttribute('Value') -ne 'Installed') -or
            ($vcSearch.GetAttribute('Type') -ne 'value') -or
            ($vcSearch.GetAttribute('Win64') -ne 'yes')) {
            throw "The Burn manifest has an unexpected Visual C++ runtime detection search."
        }

        foreach ($expectedSearch in @(
                @{
                    Id = 'OpenBladeVcRuntimeMajorSearch'
                    Variable = 'OpenBladeVcRuntimeMajor'
                    Value = 'Major'
                },
                @{
                    Id = 'OpenBladeVcRuntimeMinorSearch'
                    Variable = 'OpenBladeVcRuntimeMinor'
                    Value = 'Minor'
                },
                @{
                    Id = 'OpenBladeVcRuntimeBuildSearch'
                    Variable = 'OpenBladeVcRuntimeBuild'
                    Value = 'Bld'
                })) {
            $versionSearch = $manifest.SelectSingleNode(
                "/burn:BurnManifest/burn:RegistrySearch[@Id='$($expectedSearch.Id)']",
                $namespaces)
            if (($null -eq $versionSearch) -or
                ($versionSearch.GetAttribute('Variable') -ne $expectedSearch.Variable) -or
                ($versionSearch.GetAttribute('Root') -ne 'HKLM') -or
                ($versionSearch.GetAttribute('Key') -ne
                    'SOFTWARE\Microsoft\VisualStudio\14.0\VC\Runtimes\x64') -or
                ($versionSearch.GetAttribute('Value') -ne $expectedSearch.Value) -or
                ($versionSearch.GetAttribute('Type') -ne 'value') -or
                ($versionSearch.GetAttribute('Win64') -ne 'yes')) {
                throw "The Burn manifest has an unexpected Visual C++ runtime version search."
            }
        }

        $extractedVcRuntime = Join-Path (
            Join-Path $payloadOutput 'WixAttachedContainer') 'VC_redist.x64.exe'
        if (-not (Test-Path -LiteralPath $extractedVcRuntime -PathType Leaf)) {
            throw "The Burn installer does not embed the Visual C++ runtime."
        }
        $vcHash = (Get-FileHash -LiteralPath $extractedVcRuntime -Algorithm SHA256).Hash
        if ($vcHash -ne
            '8995548DFFFCDE7C49987029C764355612BA6850EE09A7B6F0FDDC85BDC5C280') {
            throw "The embedded Visual C++ runtime is not the pinned release."
        }
        $vcSignature = Get-AuthenticodeSignature -LiteralPath $extractedVcRuntime
        if (($vcSignature.Status -ne [Management.Automation.SignatureStatus]::Valid) -or
            ($null -eq $vcSignature.SignerCertificate) -or
            (-not $vcSignature.SignerCertificate.Subject.StartsWith(
                'CN=Microsoft Corporation,',
                [StringComparison]::Ordinal))) {
            throw "The embedded Visual C++ runtime does not have a valid Microsoft signature."
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
