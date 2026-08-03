[CmdletBinding()]
param()

Set-StrictMode -Version 3.0
$ErrorActionPreference = 'Stop'
$repository = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$installerProject = Join-Path $repository 'build\installer\OpenBlade.Installer.wixproj'
$bundleSource = Join-Path $repository 'build\installer\Bundle.wxs'

function Assert-True {
    param(
        [Parameter(Mandatory)][bool]$Condition,
        [Parameter(Mandatory)][string]$Message
    )

    if (-not $Condition) {
        throw $Message
    }
}

[xml]$project = Get-Content -LiteralPath $installerProject
Assert-True ($project.Project.Sdk -eq 'WixToolset.Sdk/7.0.0') `
    'The graphical installer must pin WiX SDK 7.0.0.'
Assert-True ($project.Project.PropertyGroup.OutputType -eq 'Bundle') `
    'The graphical installer must build a Burn bundle.'
Assert-True ($project.Project.PropertyGroup.AcceptEula -eq 'wix7') `
    'WiX 7 OSMF acceptance must remain explicit in the installer project.'

$packageReferences = @($project.SelectNodes(
        "/*[local-name()='Project']/*[local-name()='ItemGroup']/*[local-name()='PackageReference']") |
    ForEach-Object { [string]$_.GetAttribute('Include') })
Assert-True ($packageReferences -contains 'WixToolset.BootstrapperApplications.wixext') `
    'The installer must reference the standard bootstrapper application extension.'
Assert-True ($packageReferences -contains 'WixToolset.Util.wixext') `
    'The installer must reference the WiX utility extension.'

[xml]$packages = Get-Content -LiteralPath (Join-Path $repository 'Directory.Packages.props')
$packageVersions = @{}
foreach ($package in $packages.SelectNodes(
        "/*[local-name()='Project']/*[local-name()='ItemGroup']/*[local-name()='PackageVersion']")) {
    $packageVersions[[string]$package.GetAttribute('Include')] =
        [string]$package.GetAttribute('Version')
}
Assert-True (
    $packageVersions['WixToolset.BootstrapperApplications.wixext'] -eq '7.0.0') `
    'The bootstrapper application extension version must match the WiX SDK.'
Assert-True ($packageVersions['WixToolset.Util.wixext'] -eq '7.0.0') `
    'The utility extension version must match the WiX SDK.'

[xml]$bundleDocument = Get-Content -LiteralPath $bundleSource
$bundle = $bundleDocument.SelectSingleNode(
    "/*[local-name()='Wix']/*[local-name()='Bundle']")
Assert-True ($null -ne $bundle) 'Bundle.wxs must contain a Bundle element.'
Assert-True ($bundle.GetAttribute('Id') -eq 'OSSBlade.OpenBlade') `
    'The Burn bundle identity changed unexpectedly.'
Assert-True (
    $bundle.GetAttribute('UpgradeCode') -eq
        '{FF859CA9-D69F-4995-A2AD-69E540E45724}') `
    'The pinned Burn upgrade-family code changed unexpectedly.'
Assert-True ($bundle.GetAttribute('ProviderKey') -eq 'OSSBlade.OpenBlade') `
    'The Burn dependency provider key must remain stable across upgrades.'
Assert-True ($bundle.GetAttribute('Version') -eq '$(OpenBladeVersion)') `
    'The Burn bundle must use the product version source of truth.'
Assert-True (
    $bundle.GetAttribute('Condition') -eq
        'VersionNT64 >= v10.0 AND WindowsBuildNumber >= 26100') `
    'The Burn bundle must require Windows 11 version 24H2 x64.'

$standardBa = $bundle.SelectSingleNode(
    "*[local-name()='BootstrapperApplication']/*[local-name()='WixStandardBootstrapperApplication']")
Assert-True ($null -ne $standardBa) `
    'The Burn bundle must use WixStandardBootstrapperApplication.'
Assert-True ($standardBa.GetAttribute('Theme') -eq 'hyperlinkLargeLicense') `
    'The installer must retain the branded standard large-license theme.'
Assert-True ([string]::IsNullOrEmpty($standardBa.GetAttribute('LicenseUrl'))) `
    'The free OpenBlade installer must not present a separate license link.'

$search = $bundle.SelectSingleNode(
    "*[local-name()='RegistrySearch'][@Id='OpenBladeBundleOwnershipSearch']")
Assert-True ($null -ne $search) 'The bundle ownership registry search is missing.'
Assert-True ($search.GetAttribute('Id') -eq 'OpenBladeBundleOwnershipSearch') `
    'The bundle ownership registry search identity changed unexpectedly.'
Assert-True ($search.GetAttribute('Variable') -eq 'OpenBladeInstalledVersion') `
    'The bundle must read its installed version marker.'
Assert-True ($search.GetAttribute('Key') -eq 'SOFTWARE\OSSBlade\OpenBlade\Installer') `
    'The bundle ownership registry path changed unexpectedly.'
Assert-True ($search.GetAttribute('Bitness') -eq 'always64') `
    'The bundle ownership search must use the 64-bit registry view.'
Assert-True ($search.GetAttribute('Value') -eq 'InstalledVersion') `
    'The bundle ownership search must read InstalledVersion.'
Assert-True ($search.GetAttribute('Result') -eq 'value') `
    'The bundle ownership search must return its version value.'

$package = $bundle.SelectSingleNode(
    "*[local-name()='Chain']/*[local-name()='ExePackage'][@Id='OpenBladeSetup']")
Assert-True ($null -ne $package) 'The OpenBlade setup executable package is missing.'
$expectedPackageAttributes = [ordered]@{
    InstallArguments = 'install --bundle-managed'
    RepairArguments = 'install --bundle-managed'
    UninstallArguments = 'uninstall --bundle-managed'
    DetectCondition = 'OpenBladeInstalledVersion AND OpenBladeInstalledVersion >= v$(OpenBladeVersion)'
    PerMachine = 'yes'
    Cache = 'keep'
    Vital = 'yes'
}
foreach ($entry in $expectedPackageAttributes.GetEnumerator()) {
    Assert-True ($package.GetAttribute($entry.Key) -eq $entry.Value) `
        "The executable package attribute $($entry.Key) changed unexpectedly."
}
Assert-True ($package.GetAttribute('DetectCondition').Contains('>=')) `
    'The executable package must detect the same installed version so Burn runs its uninstall command.'

$payloads = $package.SelectSingleNode("*[local-name()='Payloads']")
Assert-True ($null -ne $payloads) 'The OpenBlade payload group is missing.'
Assert-True ($payloads.GetAttribute('Include').EndsWith('\**', [StringComparison]::Ordinal)) `
    'The payload include must preserve the published bundle root.'
$exclude = $payloads.SelectSingleNode("*[local-name()='Exclude']")
Assert-True (
    ($null -ne $exclude) -and
    $exclude.GetAttribute('Files').EndsWith(
        '\OpenBlade.Setup.exe',
        [StringComparison]::Ordinal)) `
    'OpenBlade.Setup.exe must not be duplicated as a child payload.'

$vcPackage = $bundle.SelectSingleNode(
    "*[local-name()='Chain']/*[local-name()='ExePackage'][@Id='OpenBladeVcRuntime']")
Assert-True ($null -ne $vcPackage) `
    'The offline installer must chain the Visual C++ runtime prerequisite.'
foreach ($entry in ([ordered]@{
        InstallArguments = '/install /quiet /norestart'
        RepairArguments = '/repair /quiet /norestart'
        DetectCondition = 'OpenBladeVcRuntimeInstalled AND OpenBladeVcRuntimeMajor AND OpenBladeVcRuntimeMinor AND OpenBladeVcRuntimeBuild AND OpenBladeVcRuntimeInstalled = 1 AND (OpenBladeVcRuntimeMajor > 14 OR (OpenBladeVcRuntimeMajor = 14 AND (OpenBladeVcRuntimeMinor > 50 OR (OpenBladeVcRuntimeMinor = 50 AND OpenBladeVcRuntimeBuild >= 35719))))'
        PerMachine = 'yes'
        Permanent = 'yes'
        Cache = 'remove'
        Vital = 'yes'
    }).GetEnumerator()) {
    Assert-True ($vcPackage.GetAttribute($entry.Key) -eq $entry.Value) `
        "The Visual C++ package attribute $($entry.Key) changed unexpectedly."
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
    $versionSearch = $bundle.SelectSingleNode(
        "*[local-name()='RegistrySearch'][@Id='$($expectedSearch.Id)']")
    Assert-True ($null -ne $versionSearch) `
        "The Visual C++ $($expectedSearch.Value) registry search is missing."
    Assert-True (
        $versionSearch.GetAttribute('Variable') -eq $expectedSearch.Variable) `
        "The Visual C++ $($expectedSearch.Value) variable changed unexpectedly."
    Assert-True ($versionSearch.GetAttribute('Value') -eq $expectedSearch.Value) `
        "The Visual C++ $($expectedSearch.Value) value changed unexpectedly."
    Assert-True ($versionSearch.GetAttribute('Bitness') -eq 'always64') `
        "The Visual C++ $($expectedSearch.Value) search must use the 64-bit view."
}

$signEngineTarget = $project.SelectSingleNode(
    "/*[local-name()='Project']/*[local-name()='Target'][@Name='SignBundleEngine']")
$signBundleTarget = $project.SelectSingleNode(
    "/*[local-name()='Project']/*[local-name()='Target'][@Name='SignBundle']")
Assert-True ($null -ne $signEngineTarget) `
    'The WiX detached bundle-engine signing hook is missing.'
Assert-True ($null -ne $signBundleTarget) `
    'The WiX final bundle signing hook is missing.'
Assert-True (
    ([string]$signEngineTarget.Exec.Command).Contains('@(SignBundleEngine)')) `
    'The detached engine signing target must sign the WiX engine item.'
Assert-True (([string]$signBundleTarget.Exec.Command).Contains('@(SignBundle)')) `
    'The final bundle signing target must sign the reattached WiX bundle item.'

$solution = Get-Content -LiteralPath (Join-Path $repository 'OpenBlade.slnx') -Raw
Assert-True ($solution.IndexOf(
        'OpenBlade.Installer.wixproj',
        [StringComparison]::OrdinalIgnoreCase) -lt 0) `
    'The release-only installer project must remain outside the normal solution build.'

$publish = Get-Content -LiteralPath (Join-Path $repository 'build\Publish.ps1') -Raw
foreach ($requiredText in @(
        'BuildIdentity.ps1',
        'InstallerValidation.ps1',
        'InstallerPrerequisites.ps1',
        'StoreSubmissionValidation.ps1',
        'build\installer\OpenBlade.Installer.wixproj',
        'OpenBlade-Setup.exe',
        'OpenBlade-Store-Readiness.json',
        'Assert-OpenBladeInstallerBundle',
        'Write-OpenBladeStoreReadinessReport',
        'Invoke-OpenBladeSigning',
        '--property:SignOutput=true',
        'OpenBladeSigningThumbprint',
        'VerifyPrivacyPolicy',
        'BuildChannel',
        'BuildRevision',
        'Write-OpenBladeNightlyArtifactMetadata',
        'upgradeProbeVersion',
        'upgrade-probe-validation')) {
    Assert-True ($publish.IndexOf($requiredText, [StringComparison]::Ordinal) -ge 0) `
        "Publish.ps1 is missing required installer integration '$requiredText'."
}
Assert-True (
    $publish -match (
        '(?s)if \(\$signingEnabled\)\s*\{\s*' +
        '\$installerBuildArguments \+= @\(\s*' +
        '''--property:SignOutput=true''')) `
    'Signed publishing must opt into the WiX bundle-signing pipeline.'

$storeContractPath = Join-Path $repository 'build\store\OpenBlade.StoreSubmission.json'
Assert-True (Test-Path -LiteralPath $storeContractPath -PathType Leaf) `
    'The Microsoft Store submission contract is missing.'
$storeContract = Get-Content -LiteralPath $storeContractPath -Raw | ConvertFrom-Json
Assert-True ($storeContract.schemaVersion -eq 1) `
    'The Microsoft Store submission contract has an unsupported schema.'
Assert-True ($storeContract.appType -eq 'EXE') `
    'The Store submission must use the existing EXE route.'
Assert-True ($storeContract.architecture -eq 'x64') `
    'The Store submission architecture must remain x64.'
Assert-True ($storeContract.language -eq 'en-US') `
    'The Store submission language must match the shipped UI resources.'
Assert-True ($storeContract.installerParameters -eq '/install /quiet /norestart') `
    'The Store must invoke Burn without installer UI or automatic restart.'
Assert-True ($storeContract.offlineInstaller -eq $true) `
    'The Store submission must remain a self-contained offline installer.'
Assert-True (
    $storeContract.privacyPolicyUrl -eq
        'https://openblade.sh/privacy.html') `
    'The Store submission must pin the absolute public privacy-policy URL.'
Assert-True (
    $storeContract.minimumOperatingSystem -eq
        'Windows 11 version 24H2 x64 (build 26100 or later)') `
    'The Store submission must match the tray and installer OS floor.'

Write-Host 'Installer authoring tests passed.'
