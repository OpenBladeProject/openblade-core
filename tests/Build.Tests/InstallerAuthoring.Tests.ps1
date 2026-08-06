[CmdletBinding()]
param()

Set-StrictMode -Version 3.0
$ErrorActionPreference = 'Stop'
$repository = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$installerProject = Join-Path $repository 'build\installer\OpenBlade.Installer.wixproj'
$installerUiProject = Join-Path $repository (
    'build\installer\OpenBlade.Installer.UI\OpenBlade.Installer.UI.csproj')
$installerUiTestProject = Join-Path $repository (
    'build\installer\OpenBlade.Installer.UI.Tests\OpenBlade.Installer.UI.Tests.csproj')
$bundleSource = Join-Path $repository 'build\installer\Bundle.wxs'
$setupPathsSource = Join-Path $repository 'src\OpenBlade.Setup\SetupPaths.cs'

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
Assert-True ($packageReferences -contains 'WixToolset.Util.wixext') `
    'The installer must reference the WiX utility extension.'

[xml]$installerUi = Get-Content -LiteralPath $installerUiProject
Assert-True ($installerUi.Project.PropertyGroup.TargetFramework -eq 'net472') `
    'The installer UI must use the Windows-inbox .NET Framework runtime.'
Assert-True ($installerUi.Project.PropertyGroup.UseWPF -eq 'true') `
    'The managed installer UI must remain a WPF application.'
$installerUiReferences = @($installerUi.SelectNodes(
        "/*[local-name()='Project']/*[local-name()='ItemGroup']/*[local-name()='PackageReference']") |
    ForEach-Object { [string]$_.GetAttribute('Include') })
Assert-True ($installerUiReferences -contains 'WixToolset.BootstrapperApplicationApi') `
    'The installer UI must use the supported WiX managed bootstrapper API.'

[xml]$installerUiTests = Get-Content -LiteralPath $installerUiTestProject
Assert-True ($installerUiTests.Project.PropertyGroup.TargetFramework -eq 'net472') `
    'The installer UI tests must exercise the same .NET Framework target as the bootstrapper.'
Assert-True ($installerUiTests.Project.PropertyGroup.UseWPF -eq 'true') `
    'The installer UI tests must load the real WPF resources.'
Assert-True ($installerUiTests.Project.PropertyGroup.IsTestProject -eq 'true') `
    'The installer UI validation project must remain discoverable by dotnet test.'
$installerUiTestReferences = @($installerUiTests.SelectNodes(
        "/*[local-name()='Project']/*[local-name()='ItemGroup']/*[local-name()='ProjectReference']") |
    ForEach-Object { [string]$_.GetAttribute('Include') })
Assert-True ($installerUiTestReferences -contains `
        '..\OpenBlade.Installer.UI\OpenBlade.Installer.UI.csproj') `
    'The installer UI tests must load the production bootstrapper window.'

[xml]$packages = Get-Content -LiteralPath (Join-Path $repository 'Directory.Packages.props')
$packageVersions = @{}
foreach ($package in $packages.SelectNodes(
        "/*[local-name()='Project']/*[local-name()='ItemGroup']/*[local-name()='PackageVersion']")) {
    $packageVersions[[string]$package.GetAttribute('Include')] =
        [string]$package.GetAttribute('Version')
}
Assert-True (
    $packageVersions['WixToolset.BootstrapperApplicationApi'] -eq '7.0.0') `
    'The managed bootstrapper API version must match the WiX SDK.'
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

$bootstrapper = $bundle.SelectSingleNode("*[local-name()='BootstrapperApplication']")
Assert-True ($null -ne $bootstrapper) 'The Burn bootstrapper application is missing.'
Assert-True ($bootstrapper.GetAttribute('SourceFile').EndsWith(
        '\OpenBlade.Installer.UI.exe',
        [StringComparison]::Ordinal)) `
    'The Burn bundle must host the OpenBlade managed installer UI.'
$bootstrapperPayloads = $bootstrapper.SelectSingleNode("*[local-name()='Payloads']")
Assert-True ($null -ne $bootstrapperPayloads) `
    'The installer UI runtime payloads must be embedded with the bootstrapper.'

$setupPaths = Get-Content -LiteralPath $setupPathsSource -Raw
$installDirectoryMatch = [regex]::Match(
    $setupPaths,
    'InstallDirectory\s*=\s*Path\.Combine\(programFilesRoot,\s*"([^"]+)"\);')
Assert-True $installDirectoryMatch.Success `
    'SetupPaths.InstallDirectory must combine Program Files with one directory name.'
$installDirectoryName = $installDirectoryMatch.Groups[1].Value
$expectedLaunchWorkingFolder = "[ProgramFiles64Folder]$installDirectoryName"
$expectedLaunchTarget = "$expectedLaunchWorkingFolder\OpenBlade.Tray.exe"
Assert-True (
    $bundle.SelectSingleNode("*[local-name()='Variable'][@Name='LaunchTarget']").GetAttribute('Value') -eq
        $expectedLaunchTarget) `
    'Successful interactive setup must launch the Tray from SetupPaths.InstallDirectory.'
Assert-True (
    $bundle.SelectSingleNode("*[local-name()='Variable'][@Name='LaunchWorkingFolder']").GetAttribute('Value') -eq
        $expectedLaunchWorkingFolder) `
    'The post-setup working directory must match SetupPaths.InstallDirectory.'

$purgeVariable = $bundle.SelectSingleNode(
    "*[local-name()='Variable'][@Name='OpenBladePurgeProfiles']")
Assert-True ($null -ne $purgeVariable) `
    'The bundle must define the profile-removal choice.'
Assert-True ($purgeVariable.GetAttribute('Type') -eq 'numeric') `
    'The profile-removal choice must be a numeric checkbox variable.'
Assert-True ($purgeVariable.GetAttribute('Value') -eq '0') `
    'Uninstall must keep profiles unless the user explicitly opts out.'

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

$purgeCommandLine = $package.SelectSingleNode(
    "*[local-name()='CommandLine'][@UninstallArgument='--purge']")
Assert-True ($null -ne $purgeCommandLine) `
    'The setup package must conditionally append the purge argument.'
Assert-True ($purgeCommandLine.GetAttribute('Condition') -eq 'OpenBladePurgeProfiles') `
    'The purge argument must be controlled by the maintenance checkbox.'

$progressCommandLine = $package.SelectSingleNode(
    "*[local-name()='CommandLine'][@Condition='OpenBladeSetupProgressPipe']")
Assert-True ($null -ne $progressCommandLine) `
    'The setup package must receive the private progress-pipe name.'
foreach ($argument in @('InstallArgument', 'RepairArgument', 'UninstallArgument')) {
    Assert-True ($progressCommandLine.GetAttribute($argument).Contains(
            '--progress-pipe "[OpenBladeSetupProgressPipe]"')) `
        "The setup package $argument must append the private progress pipe."
}

[xml]$window = Get-Content -LiteralPath (
    Join-Path $repository 'build\installer\OpenBlade.Installer.UI\InstallerWindow.xaml')
$purgeCheckbox = $window.SelectSingleNode(
    "/*[local-name()='Window']//*[local-name()='CheckBox'][@*[local-name()='Name']='PurgeProfilesCheckBox']")
Assert-True ($null -ne $purgeCheckbox) `
    'The maintenance page must offer a profile-removal checkbox.'
$launchButton = $window.SelectSingleNode(
    "/*[local-name()='Window']//*[local-name()='Button'][@*[local-name()='Name']='LaunchButton']")
Assert-True ($null -ne $launchButton) `
    'The success page must expose the configured OpenBlade launch action.'
$detailsToggle = $window.SelectSingleNode(
    "/*[local-name()='Window']//*[local-name()='ToggleButton'][@*[local-name()='Name']='DetailsToggle']")
Assert-True ($null -ne $detailsToggle) `
    'The progress page must expose a Show details toggle.'
Assert-True ($detailsToggle.GetAttribute('Content') -eq 'Show details') `
    'The collapsed progress action must be labeled Show details.'
$detailsText = $window.SelectSingleNode(
    "/*[local-name()='Window']//*[local-name()='TextBox'][@*[local-name()='Name']='DetailsText']")
Assert-True (($null -ne $detailsText) -and ($detailsText.GetAttribute('IsReadOnly') -eq 'True')) `
    'Expanded progress details must be read-only and scrollable.'

$themeSource = Get-Content -LiteralPath (
    Join-Path $repository 'build\installer\OpenBlade.Installer.UI\InstallerTheme.cs') -Raw
Assert-True ($themeSource.Contains('AppsUseLightTheme')) `
    'The installer theme must follow the Windows app-mode preference.'
Assert-True ($themeSource.Contains('DwmUseImmersiveDarkMode')) `
    'The installer must apply dark mode to its native title bar.'
$windowSource = Get-Content -LiteralPath (
    Join-Path $repository (
        'build\installer\OpenBlade.Installer.UI\InstallerWindow.xaml.cs')) -Raw
Assert-True ($windowSource.Contains(
        'action is LaunchAction.Install or LaunchAction.Repair')) `
    'Launch OpenBlade must remain limited to successful install and repair.'
Assert-True (-not $windowSource.Contains('MessageBox.Show')) `
    'Interactive cancellation must use the theme-aware in-window confirmation.'
$bootstrapperSource = Get-Content -LiteralPath (
    Join-Path $repository (
        'build\installer\OpenBlade.Installer.UI\OpenBladeBootstrapperApplication.cs')) -Raw
Assert-True ($bootstrapperSource.Contains('OpenBladeSetupProgressPipe')) `
    'The managed bootstrapper must create the private setup progress channel.'
Assert-True ($bootstrapperSource.Contains('QuitAfterRunFailure(exception)')) `
    'The managed bootstrapper must convert unhandled UI failures into a Burn exit code.'
Assert-True ($bootstrapperSource.Contains('window?.WindowHandle ?? IntPtr.Zero')) `
    'Burn apply must use the UI-thread-captured owner handle.'
Assert-True ($bootstrapperSource.Contains('ShowNewerVersionConfirmation')) `
    'Interactive setup must ask before replacing a newer OpenBlade version.'
Assert-True ($bootstrapperSource.Contains('PlanRelatedBundleType += OnPlanRelatedBundleType')) `
    'A confirmed overwrite must convert Burn downgrade planning into replacement planning.'
Assert-True ($bootstrapperSource.Contains('RequestState.ForcePresent')) `
    'A confirmed overwrite must execute the selected OpenBlade setup package.'
Assert-True ($bootstrapperSource.Contains('ApplyDowngrade += OnApplyDowngrade')) `
    'A confirmed overwrite must clear Burn''s Apply-stage downgrade veto.'
Assert-True (-not $bootstrapperSource.Contains('new WindowInteropHelper(window)')) `
    'Burn callbacks must not read the WPF window handle from a worker thread.'
$programSource = Get-Content -LiteralPath (
    Join-Path $repository 'build\installer\OpenBlade.Installer.UI\Program.cs') -Raw
Assert-True (-not $programSource.Contains('[STAThread]')) `
    'The managed BA entry point must let the WiX native host initialize COM.'
$progressPipeSource = Get-Content -LiteralPath (
    Join-Path $repository (
        'build\installer\OpenBlade.Installer.UI\InstallerProgressPipe.cs')) -Raw
Assert-True ($progressPipeSource.Contains('BuiltinAdministratorsSid')) `
    'The elevated setup package must be able to write to the private progress channel.'
Assert-True ($progressPipeSource.Contains('PipeAccessRights.ReadWrite')) `
    'Over-the-shoulder elevated setup must receive sufficient progress-pipe access.'
Assert-True ($progressPipeSource.Contains('MaximumMessageLength = 512')) `
    'Progress-pipe messages must remain bounded before reaching the UI.'

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
Assert-True ($solution.IndexOf(
        'OpenBlade.Installer.UI.csproj',
        [StringComparison]::OrdinalIgnoreCase) -lt 0) `
    'The release-only installer UI must remain outside the normal solution build.'
Assert-True ($solution.IndexOf(
        'OpenBlade.Installer.UI.Tests.csproj',
        [StringComparison]::OrdinalIgnoreCase) -lt 0) `
    'The release-only installer UI tests must remain outside the normal solution build.'

$installerValidation = Get-Content -LiteralPath (
    Join-Path $repository 'build\InstallerValidation.ps1') -Raw
Assert-True ($installerValidation.IndexOf(
        '-intermediateFolder $intermediateOutput',
        [StringComparison]::Ordinal) -ge 0) `
    'Installer extraction must use its isolated validation workspace.'
Assert-True (
    $installerValidation.Contains("@('-quiet', '-help')") -and
    $installerValidation.Contains('WaitForExit(30000)')) `
    'Installer validation must launch the managed BA through a bounded quiet smoke test.'

$publish = Get-Content -LiteralPath (Join-Path $repository 'build\Publish.ps1') -Raw
foreach ($requiredText in @(
        'BuildIdentity.ps1',
        'InstallerValidation.ps1',
        'InstallerPrerequisites.ps1',
        'StoreSubmissionValidation.ps1',
        'build\installer\OpenBlade.Installer.wixproj',
        'OpenBlade.Installer.UI\OpenBlade.Installer.UI.csproj',
        'OpenBlade.Installer.UI.Tests\OpenBlade.Installer.UI.Tests.csproj',
        'dotnet test $installerUiTestProject',
        'OpenBladeInstallerUi',
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
