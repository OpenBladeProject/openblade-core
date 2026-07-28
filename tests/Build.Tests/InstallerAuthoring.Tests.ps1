[CmdletBinding()]
param()

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

$packageReferences = @($project.Project.ItemGroup.PackageReference |
    ForEach-Object { [string]$_.Include })
Assert-True ($packageReferences -contains 'WixToolset.BootstrapperApplications.wixext') `
    'The installer must reference the standard bootstrapper application extension.'
Assert-True ($packageReferences -contains 'WixToolset.Util.wixext') `
    'The installer must reference the WiX utility extension.'

[xml]$packages = Get-Content -LiteralPath (Join-Path $repository 'Directory.Packages.props')
$packageVersions = @{}
foreach ($package in $packages.Project.ItemGroup.PackageVersion) {
    $packageVersions[[string]$package.Include] = [string]$package.Version
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
Assert-True ($bundle.GetAttribute('ProviderKey') -eq 'OSSBlade.OpenBlade') `
    'The Burn dependency provider key must remain stable across upgrades.'
Assert-True ($bundle.GetAttribute('Version') -eq '$(OpenBladeVersion)') `
    'The Burn bundle must use the product version source of truth.'

$standardBa = $bundle.SelectSingleNode(
    "*[local-name()='BootstrapperApplication']/*[local-name()='WixStandardBootstrapperApplication']")
Assert-True ($null -ne $standardBa) `
    'The Burn bundle must use WixStandardBootstrapperApplication.'
Assert-True ($standardBa.GetAttribute('Theme') -eq 'hyperlinkLargeLicense') `
    'The installer must retain the branded standard large-license theme.'
Assert-True ([string]::IsNullOrEmpty($standardBa.GetAttribute('LicenseUrl'))) `
    'The free OpenBlade installer must not present a separate license link.'

$search = $bundle.SelectSingleNode("*[local-name()='RegistrySearch']")
Assert-True ($null -ne $search) 'The bundle ownership registry search is missing.'
Assert-True ($search.GetAttribute('Id') -eq 'OpenBladeBundleOwnershipSearch') `
    'The bundle ownership registry search identity changed unexpectedly.'
Assert-True ($search.GetAttribute('Variable') -eq 'OpenBladeBundleManaged') `
    'The bundle must detect its dedicated ownership marker.'
Assert-True ($search.GetAttribute('Key') -eq 'SOFTWARE\OSSBlade\OpenBlade\Installer') `
    'The bundle ownership registry path changed unexpectedly.'
Assert-True ($search.GetAttribute('Bitness') -eq 'always64') `
    'The bundle ownership search must use the 64-bit registry view.'

$package = $bundle.SelectSingleNode(
    "*[local-name()='Chain']/*[local-name()='ExePackage']")
Assert-True ($null -ne $package) 'The OpenBlade setup executable package is missing.'
$expectedPackageAttributes = [ordered]@{
    InstallArguments = 'install --bundle-managed'
    RepairArguments = 'install --bundle-managed'
    UninstallArguments = 'uninstall --bundle-managed'
    DetectCondition = 'OpenBladeBundleManaged'
    PerMachine = 'yes'
    Vital = 'yes'
}
foreach ($entry in $expectedPackageAttributes.GetEnumerator()) {
    Assert-True ($package.GetAttribute($entry.Key) -eq $entry.Value) `
        "The executable package attribute $($entry.Key) changed unexpectedly."
}

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

$solution = Get-Content -LiteralPath (Join-Path $repository 'OpenBlade.slnx') -Raw
Assert-True ($solution.IndexOf(
        'OpenBlade.Installer.wixproj',
        [StringComparison]::OrdinalIgnoreCase) -lt 0) `
    'The release-only installer project must remain outside the normal solution build.'

$publish = Get-Content -LiteralPath (Join-Path $repository 'build\Publish.ps1') -Raw
foreach ($requiredText in @(
        'InstallerValidation.ps1',
        'StoreSubmissionValidation.ps1',
        'build\installer\OpenBlade.Installer.wixproj',
        'OpenBlade-Setup.exe',
        'OpenBlade-Store-Readiness.json',
        'Assert-OpenBladeInstallerBundle',
        'Write-OpenBladeStoreReadinessReport')) {
    Assert-True ($publish.IndexOf($requiredText, [StringComparison]::Ordinal) -ge 0) `
        "Publish.ps1 is missing required installer integration '$requiredText'."
}

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
Assert-True ($storeContract.installerParameters -eq '/quiet /norestart') `
    'The Store must invoke Burn without installer UI or automatic restart.'
Assert-True ($storeContract.offlineInstaller -eq $true) `
    'The Store submission must remain a self-contained offline installer.'

Write-Host 'Installer authoring tests passed.'
