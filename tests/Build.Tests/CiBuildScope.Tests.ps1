[CmdletBinding()]
param()

Set-StrictMode -Version 3.0
$ErrorActionPreference = 'Stop'
$repository = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
. (Join-Path $repository 'build\CiBuildScope.ps1')

function Assert-Equal {
    param(
        [Parameter(Mandatory)]$Expected,
        [Parameter(Mandatory)]$Actual,
        [Parameter(Mandatory)][string]$Message
    )

    if ($Expected -ne $Actual) {
        throw "$Message Expected '$Expected', received '$Actual'."
    }
}

function Assert-Contains {
    param(
        [Parameter(Mandatory)][string[]]$Collection,
        [Parameter(Mandatory)][string]$Expected,
        [Parameter(Mandatory)][string]$Message
    )

    if ($Collection -notcontains $Expected) {
        throw "$Message Missing '$Expected'."
    }
}

function Assert-NotContains {
    param(
        [Parameter(Mandatory)][string[]]$Collection,
        [Parameter(Mandatory)][string]$Unexpected,
        [Parameter(Mandatory)][string]$Message
    )

    if ($Collection -contains $Unexpected) {
        throw "$Message Unexpected '$Unexpected'."
    }
}

$tray = Get-OpenBladeCiPlan -Repository $repository -ChangedPath @(
    'src/OpenBlade.Tray/MainWindow.xaml')
Assert-OpenBladeCiSelectorHealthy -Repository $repository
Assert-Equal 'affected' $tray.Mode 'Tray UI changes must use affected validation.'
Assert-Contains $tray.QualityProjects `
    'src/OpenBlade.Tray/OpenBlade.Tray.csproj' `
    'Tray UI changes must build the Tray.'
Assert-Contains $tray.TestProjects `
    'tests/OpenBlade.Tray.Tests/OpenBlade.Tray.Tests.csproj' `
    'Tray UI changes must run Tray tests.'
Assert-Contains $tray.PublishProjects `
    'src/OpenBlade.Tray/OpenBlade.Tray.csproj' `
    'Tray UI changes must publish the Tray component.'
Assert-NotContains $tray.QualityProjects `
    'src/OpenBlade.Service/OpenBlade.Service.csproj' `
    'Tray UI changes must not build the Service.'
Assert-Equal $false $tray.RunInstaller `
    'Tray UI changes must not publish the complete installer.'

$service = Get-OpenBladeCiPlan -Repository $repository -ChangedPath @(
    'src/OpenBlade.Service/DeviceWorker.cs')
Assert-Contains $service.QualityProjects `
    'src/OpenBlade.Service/OpenBlade.Service.csproj' `
    'Service changes must build the Service.'
Assert-Contains $service.TestProjects `
    'tests/OpenBlade.Service.Tests/OpenBlade.Service.Tests.csproj' `
    'Service changes must run Service tests.'
Assert-Contains $service.TestProjects `
    'tests/OpenBlade.Architecture.Tests/OpenBlade.Architecture.Tests.csproj' `
    'Production C# changes must run architecture contracts.'
Assert-Contains $service.PublishProjects `
    'src/OpenBlade.Service/OpenBlade.Service.csproj' `
    'Service changes must publish the Service component.'
Assert-NotContains $service.QualityProjects `
    'src/OpenBlade.Tray/OpenBlade.Tray.csproj' `
    'Service changes must not build the Tray.'

$contracts = Get-OpenBladeCiPlan -Repository $repository -ChangedPath @(
    'src/OpenBlade.Contracts/ServiceContracts.cs')
Assert-Contains $contracts.QualityProjects `
    'src/OpenBlade.Service/OpenBlade.Service.csproj' `
    'Contracts changes must include Service consumers.'
Assert-Contains $contracts.QualityProjects `
    'src/OpenBlade.Tray/OpenBlade.Tray.csproj' `
    'Contracts changes must include Tray consumers.'
Assert-Contains $contracts.TestProjects `
    'tests/OpenBlade.Core.Native.Tests/OpenBlade.Core.Native.Tests.csproj' `
    'Contracts changes must select native interop tests through Core.Native.'
foreach ($consumer in @(
        'src/OpenBlade.Display/OpenBlade.Display.csproj',
        'src/OpenBlade.Platform/OpenBlade.Platform.csproj',
        'src/OpenBlade.DeploymentBroker/OpenBlade.DeploymentBroker.csproj')) {
    Assert-Contains $contracts.AffectedProjects $consumer `
        'Shared diagnostic recorder consumers must remain in the Contracts reverse closure.'
}
Assert-NotContains $contracts.AffectedProjects `
    'src/OpenBlade.Deployment/OpenBlade.Deployment.csproj' `
    'Unrelated production projects must remain outside the reverse closure.'
Assert-Equal $true $contracts.RunInstaller `
    'Contracts changes affect Setup and require complete installer validation.'

$linkedSetup = Get-OpenBladeCiPlan -Repository $repository -ChangedPath @(
    'src/OpenBlade.Setup/StartupShortcutWriter.cs')
Assert-Contains $linkedSetup.QualityProjects `
    'src/OpenBlade.Setup/OpenBlade.Setup.csproj' `
    'Linked Setup source must keep its owning project.'
Assert-Contains $linkedSetup.QualityProjects `
    'src/OpenBlade.Tray/OpenBlade.Tray.csproj' `
    'Linked Setup source must also build the Tray.'
Assert-Contains $linkedSetup.TestProjects `
    'tests/OpenBlade.Tray.Tests/OpenBlade.Tray.Tests.csproj' `
    'Linked Setup source must run Tray tests.'

$linkedSignInTask = Get-OpenBladeCiPlan -Repository $repository -ChangedPath @(
    'src/OpenBlade.Setup/SignInTaskScheduler.cs')
Assert-Contains $linkedSignInTask.QualityProjects `
    'src/OpenBlade.Setup/OpenBlade.Setup.csproj' `
    'Linked Setup source must keep its owning project.'
Assert-Contains $linkedSignInTask.QualityProjects `
    'src/OpenBlade.Tray/OpenBlade.Tray.csproj' `
    'Linked Setup source must also build the Tray.'
Assert-Contains $linkedSignInTask.TestProjects `
    'tests/OpenBlade.Tray.Tests/OpenBlade.Tray.Tests.csproj' `
    'Linked Setup source must run Tray tests.'

$linkedTray = Get-OpenBladeCiPlan -Repository $repository -ChangedPath @(
    'src/OpenBlade.Tray/WindowsRegistryReader.cs')
Assert-Contains $linkedTray.TestProjects `
    'tests/OpenBlade.SessionAgent.Tests/OpenBlade.SessionAgent.Tests.csproj' `
    'Linked Tray source must run Session Agent tests.'

foreach ($trayContractInput in @(
        'src/OpenBlade.SessionAgent/Program.cs',
        'src/OpenBlade.SessionAgent/WindowsReactiveKeyboardSource.cs',
        'build/Publish.ps1',
        'build/PublishPayloadValidation.ps1')) {
    $trayContract = Get-OpenBladeCiPlan -Repository $repository -ChangedPath @(
        $trayContractInput)
    Assert-Contains $trayContract.TestProjects `
        'tests/OpenBlade.Tray.Tests/OpenBlade.Tray.Tests.csproj' `
        "Tray source-reading contracts must run for '$trayContractInput'."
}

$sharedTest = Get-OpenBladeCiPlan -Repository $repository -ChangedPath @(
    'tests/OpenBlade.Testing/AsyncTest.cs')
foreach ($project in @(
        'tests/OpenBlade.Protocol.Tests/OpenBlade.Protocol.Tests.csproj',
        'tests/OpenBlade.SessionAgent.Tests/OpenBlade.SessionAgent.Tests.csproj',
        'tests/OpenBlade.Tray.Tests/OpenBlade.Tray.Tests.csproj')) {
    Assert-Contains $sharedTest.TestProjects $project `
        'Shared test helpers must select every linked consumer.'
}
$trayTestOnly = Get-OpenBladeCiPlan -Repository $repository -ChangedPath @(
    'tests/OpenBlade.Tray.Tests/CapabilityPresentationTests.cs')
Assert-Contains $trayTestOnly.QualityProjects `
    'src/OpenBlade.Tray/OpenBlade.Tray.csproj' `
    'Tray test changes must restore the hidden WinUI build input.'
Assert-Equal 0 @($trayTestOnly.PublishProjects).Count `
    'Test-only changes must not publish production components.'

$readme = Get-OpenBladeCiPlan -Repository $repository -ChangedPath @('README.md')
Assert-Equal 'affected' $readme.Mode `
    'README changes have a source-reading build contract.'
Assert-Contains $readme.ContractTests `
    'tests/Build.Tests/ReadmeHardwareSupport.Tests.ps1' `
    'README changes must run the hardware-support contract.'
Assert-Equal 0 @($readme.QualityProjects).Count `
    'README changes must not run .NET quality projects.'

$docs = Get-OpenBladeCiPlan -Repository $repository -ChangedPath @(
    'docs/images/openblade-dashboard.png')
Assert-Equal 'none' $docs.Mode `
    'Documentation without an executable contract must skip expensive validation.'

$evidence = Get-OpenBladeCiPlan -Repository $repository -ChangedPath @(
    'evidence/legacy-captures/decoded/example.json')
Assert-Contains $evidence.TestProjects `
    'tests/OpenBlade.Protocol.Tests/OpenBlade.Protocol.Tests.csproj' `
    'Legacy evidence changes must run Protocol evidence tests.'
Assert-Contains $evidence.TestProjects `
    'tests/OpenBlade.Capture.Tests/OpenBlade.Capture.Tests.csproj' `
    'Legacy evidence changes must run Capture evidence tests.'
Assert-Contains $evidence.ContractTests `
    'tests/Build.Tests/LegacyEvidenceProvenance.Tests.ps1' `
    'Legacy evidence changes must verify the provenance map.'

$sparseContract = Get-OpenBladeCiPlan -Repository $repository -ChangedPath @(
    'tests/Build.Tests/SparsePackageIdentity.Tests.ps1')
Assert-Contains $sparseContract.ContractRestoreProjects `
    'src/OpenBlade.SessionAgent/OpenBlade.SessionAgent.csproj' `
    'A changed sparse-identity suite must restore its publish input.'
$undeclaredContract = Get-OpenBladeCiPlan -Repository $repository -ChangedPath @(
    'tests/Build.Tests/NewUndeclared.Tests.ps1')
Assert-Equal 'full' $undeclaredContract.Mode `
    'A new or deleted build-contract suite without a complete declaration must fail closed.'

$installerUi = Get-OpenBladeCiPlan -Repository $repository -ChangedPath @(
    'build/installer/OpenBlade.Installer.UI/MainWindow.xaml')
Assert-Contains $installerUi.QualityProjects `
    'build/installer/OpenBlade.Installer.UI/OpenBlade.Installer.UI.csproj' `
    'Installer UI changes must build their project.'
Assert-Contains $installerUi.TestProjects `
    'build/installer/OpenBlade.Installer.UI.Tests/OpenBlade.Installer.UI.Tests.csproj' `
    'Installer UI changes must run their non-solution tests.'
Assert-Equal $true $installerUi.RunInstaller `
    'Installer UI changes must validate the complete bundle.'
Assert-Contains $installerUi.ContractTests `
    'tests/Build.Tests/LocalizationResources.Tests.ps1' `
    'Installer UI changes must validate localization resources.'

$trayLocalization = Get-OpenBladeCiPlan -Repository $repository -ChangedPath @(
    'src/OpenBlade.Tray/Strings/en-US/Resources.resw')
Assert-Contains $trayLocalization.ContractTests `
    'tests/Build.Tests/LocalizationResources.Tests.ps1' `
    'Tray resource changes must validate localization contracts.'
foreach ($localizedPath in @(
        'src/OpenBlade.Tray.Core/Properties/Resources.resx',
        'src/OpenBlade.Setup/Properties/Resources.resx',
        'build/installer/Bundle.en-US.wxl',
        'build/Capture-LocalizationSnapshots.ps1')) {
    $localization = Get-OpenBladeCiPlan -Repository $repository -ChangedPath @($localizedPath)
    Assert-Contains $localization.ContractTests `
        'tests/Build.Tests/LocalizationResources.Tests.ps1' `
        "Localization path '$localizedPath' must validate resource contracts."
}

foreach ($translatorPath in @(
        'build/Export-LocalizationTranslatorPackage.ps1',
        'build/Import-LocalizationTranslatorWorkbook.ps1',
        'build/LocalizationTranslatorWorkflow.psm1',
        'build/Validate-LocalizationResources.ps1',
        'build/localization/LocalizationSnapshotFailureEvidence.cs',
        'build/localization/TranslatorScreenshotMap.json',
        'build/installer/OpenBlade.Installer.UI.Tests/InstallerWindowTests.cs',
        'build/installer/OpenBlade.Installer.UI/Properties/Resources.resx',
        'build/installer/OpenBlade.Installer.UI/Properties/Resources.ru-RU.resx',
        'docs/localization-translators.md',
        'src/OpenBlade.Setup/Properties/Resources.resx',
        'src/OpenBlade.Setup/Properties/Resources.ru-RU.resx',
        'src/OpenBlade.Tray/App.xaml.cs',
        'src/OpenBlade.Tray/App.DeveloperHarness.cs',
        'src/OpenBlade.Tray/LocalizationSnapshotWindowInput.cs',
        'src/OpenBlade.Tray/MainWindow.xaml',
        'src/OpenBlade.Tray/MainWindow.xaml.cs',
        'src/OpenBlade.Tray/Strings/en-US/Resources.resw',
        'src/OpenBlade.Tray/Strings/ru-RU/Resources.resw',
        'src/OpenBlade.Tray.Core/Properties/Resources.resx',
        'src/OpenBlade.Tray.Core/Properties/Resources.ru-RU.resx',
        'tests/Build.Tests/LocalizationTranslatorWorkflow.Tests.ps1')) {
    $translator = Get-OpenBladeCiPlan -Repository $repository -ChangedPath @($translatorPath)
    Assert-Contains $translator.ContractTests `
        'tests/Build.Tests/LocalizationTranslatorWorkflow.Tests.ps1' `
        "Translator workflow path '$translatorPath' must run its adversarial contracts."
}
$snapshotEvidencePlan = Get-OpenBladeCiPlan -Repository $repository -ChangedPath @(
    'build/localization/LocalizationSnapshotFailureEvidence.cs')
foreach ($linkedTestProject in @(
        'tests/OpenBlade.Tray.Tests/OpenBlade.Tray.Tests.csproj',
        'build/installer/OpenBlade.Installer.UI.Tests/OpenBlade.Installer.UI.Tests.csproj')) {
    Assert-Contains $snapshotEvidencePlan.TestProjects $linkedTestProject `
        'A shared snapshot evidence change must run both linking test projects.'
}

$translatorEvidencePlan = Get-OpenBladeCiPlan -Repository $repository -ChangedPath @(
    'tests/Build.Tests/LocalizationTranslatorWorkflow.Tests.ps1')
Assert-Contains $translatorEvidencePlan.ContractTests `
    'tests/Build.Tests/CiBuildScope.Tests.ps1' `
    'Translator producer changes must also validate the failure-artifact handoff.'

$translatorRestore = Get-OpenBladeCiPlan -Repository $repository -ChangedPath @(
    'build/LocalizationTranslatorWorkflow.psm1')
foreach ($restoreProject in @(
        'src/OpenBlade.Tray/OpenBlade.Tray.csproj',
        'build/installer/OpenBlade.Installer.UI.Tests/OpenBlade.Installer.UI.Tests.csproj')) {
    Assert-Contains $translatorRestore.ContractRestoreProjects $restoreProject `
        'Translator contracts must restore every disconnected snapshot renderer.'
}

foreach ($eventName in @('pull_request', 'push')) {
    Assert-Equal $false (Test-OpenBladeCiFullBoundary -EventName $eventName) `
        "Ordinary event '$eventName' must use impact routing regardless of draft state."
}
foreach ($eventName in @('schedule', 'workflow_dispatch', 'release', '', 'unknown')) {
    Assert-Equal $true (Test-OpenBladeCiFullBoundary -EventName $eventName) `
        "Integration or unknown event '$eventName' must force full validation."
}

$testMetadata = Get-OpenBladeCiPlan -Repository $repository -ChangedPath @(
    'tests/OpenBlade.DeploymentClient.Tests/OpenBlade.DeploymentClient.Tests.csproj',
    'tests/OpenBlade.DeploymentClient.Tests/IndependentInstalledProcessTests.cs')
Assert-Equal 'affected' $testMetadata.Mode 'Test project metadata must select affected validation.'
Assert-Equal 1 @($testMetadata.QualityProjects).Count 'Test-only changes must select one project.'
Assert-Contains $testMetadata.TestProjects `
    'tests/OpenBlade.DeploymentClient.Tests/OpenBlade.DeploymentClient.Tests.csproj' `
    'Test metadata changes must run their owning suite.'
Assert-Equal $false $testMetadata.RunInstaller 'Test metadata must not publish the installer.'
Assert-Equal 0 @($testMetadata.ContractTests).Count 'Test metadata must not select unrelated contracts.'
Assert-Equal 0 @($testMetadata.PublishProjects).Count 'Test metadata must not publish product binaries.'

foreach ($cliFixture in @('request.json', 'response.json')) {
    $fixturePlan = Get-OpenBladeCiPlan -Repository $repository -ChangedPath @(
        "rust/crates/openblade-status-cli/fixtures/$cliFixture")
    Assert-Equal 'affected' $fixturePlan.Mode 'Rust-only IPC fixture edits must use affected validation.'
    Assert-Contains $fixturePlan.QualityProjects `
        'tests/OpenBlade.Contracts.Tests/OpenBlade.Contracts.Tests.csproj' `
        'Rust IPC fixture edits must build the real C# contract comparison.'
    Assert-Contains $fixturePlan.TestProjects `
        'tests/OpenBlade.Contracts.Tests/OpenBlade.Contracts.Tests.csproj' `
        'Rust IPC fixture edits must run the real C# contract comparison.'
    Assert-Contains $fixturePlan.ContractTests 'tests/Build.Tests/NativePayload.Tests.ps1' `
        'Rust IPC fixture routing must retain the native payload contract checks.'
    Assert-Equal $true $fixturePlan.RunInstaller `
        'Rust IPC fixture routing must retain actual installer payload validation.'
}

foreach ($nativeInput in @(
        'src/OpenBlade.Core.Native/ProductNativeRuntime.cs',
        'src/OpenBlade.Core.Native/OpenBlade.Core.Native.csproj')) {
    $nativeInstaller = Get-OpenBladeCiPlan -Repository $repository -ChangedPath @($nativeInput)
    Assert-Equal $true $nativeInstaller.RunInstaller `
        'Native admission and manifest wiring changes require actual installer payload validation.'
}

$serviceMetadata = Get-OpenBladeCiPlan -Repository $repository -ChangedPath @(
    'src/OpenBlade.Service/OpenBlade.Service.csproj')
Assert-Equal 'full' $serviceMetadata.Mode 'Production metadata must keep exhaustive validation.'
$setupMetadata = Get-OpenBladeCiPlan -Repository $repository -ChangedPath @(
    'src/OpenBlade.Setup/OpenBlade.Setup.csproj')
Assert-Equal 'full' $setupMetadata.Mode 'Setup metadata must keep exhaustive validation.'
Assert-Equal $true $setupMetadata.RunInstaller 'Setup metadata must keep complete installer validation.'
$catalog = Get-OpenBladeCiProjectCatalog -Repository $repository
foreach ($project in @($catalog.Values | Where-Object { $_.Kind -eq 'Test' })) {
    $metadataPlan = Get-OpenBladeCiPlan -Repository $repository -ChangedPath @($project.Path)
    Assert-Equal 'affected' $metadataPlan.Mode "Known test metadata '$($project.Path)' must use affected validation."
    Assert-Contains $metadataPlan.QualityProjects $project.Path 'Test metadata must select its build.'
    Assert-Contains $metadataPlan.TestProjects $project.Path 'Test metadata must select its tests.'
}
$unknownMetadata = Get-OpenBladeCiPlan -Repository $repository -ChangedPath @('tools/Unknown.csproj')
Assert-Equal 'full' $unknownMetadata.Mode 'Unowned metadata must fail closed.'
$deletedMetadata = Get-OpenBladeCiPlan -Repository $repository -ChangedPath @('tests/OpenBlade.DeploymentClient.Tests/Deleted.csproj')
Assert-Equal 'full' $deletedMetadata.Mode 'Deleted project metadata must fail closed.'
$ownedFixtureFile = Get-OpenBladeCiPlan -Repository $repository -ChangedPath @(
    'tests/Build.Tests/WindowsNativeRoleBurnFixture/Program.cs')
Assert-Equal 'affected' $ownedFixtureFile.Mode 'A Build.Tests file owned by a test project must be classified by that project.'
Assert-Contains $ownedFixtureFile.TestProjects `
    'tests/Build.Tests/WindowsNativeRoleBurnFixture/InventoryWriter.csproj' `
    'A Build.Tests file owned by a test project must select that project.'

foreach ($globalPath in @(
        'Directory.Build.props',
        '.github/workflows/ci.yml',
        'tools/new-script.ps1',
        'tests/Build.Tests/unknown_helper.py')) {
    $full = Get-OpenBladeCiPlan -Repository $repository -ChangedPath @($globalPath)
    Assert-Equal 'full' $full.Mode `
        "Global or unknown path '$globalPath' must fail closed."
}
$forced = Get-OpenBladeCiPlan -Repository $repository -ChangedPath @('README.md') -ForceFull
Assert-Equal 'full' $forced.Mode 'Forced workflow boundaries must run full validation.'
$empty = Get-OpenBladeCiPlan -Repository $repository -ChangedPath @()
Assert-Equal 'full' $empty.Mode 'An empty change set must fail closed.'

# Test-only fixture infrastructure selects consumers without product publishing.
foreach ($fixtureInput in @(
        'build/Test-NativeWindowsFixtures.ps1',
        'build/NativeWindowsFixtures.ps1',
        'build/native_windows_fixtures.py',
        'build/NativeWindowsFixtureManifest.json',
        'tests/Build.Tests/NativeWindowsFixtures.Tests.ps1',
        'build/test_native_windows_fixtures.py',
        'tools/openblade-windows-host/src/windows/scm/installed/readiness_tests/mutation.rs',
        'tools/openblade-windows-host/src/windows/scm/installed/readiness_tests/temporary_charge_release.rs',
        'tools/openblade-windows-host/src/windows/scm/installed/battery_mutation.rs',
        'tools/openblade-windows-host/tests/fixtures/native-thermal-edge-fixtures.json',
        'tools/openblade-windows-host/tests/fixtures/native-thermal-edge/get-status.json')) {
    $fixture = Get-OpenBladeCiPlan -Repository $repository -ChangedPath @($fixtureInput)
    Assert-Equal 'affected' $fixture.Mode "Fixture input '$fixtureInput' must use affected validation."
    Assert-Equal 3 @($fixture.TestProjects).Count 'Fixture infrastructure selects exactly its three managed consumers.'
    foreach ($consumer in @('Contracts', 'Service', 'Deployment')) {
        Assert-Contains $fixture.TestProjects "tests/OpenBlade.$consumer.Tests/OpenBlade.$consumer.Tests.csproj" "Fixture input '$fixtureInput' must run the $consumer consumer."
    }
    foreach ($contract in @('NativeWindowsFixtures', 'CiBuildScope')) {
        Assert-Contains $fixture.ContractTests "tests/Build.Tests/$contract.Tests.ps1" 'Fixture changes must validate orchestration and selector contracts.'
    }
    Assert-NotContains $fixture.ContractTests 'tests/Build.Tests/NativePayload.Tests.ps1' 'Test-only fixture changes must not promote native product payload validation.'
    Assert-Equal $false $fixture.RunInstaller 'Test-only fixture changes must not promote installer validation.'
    Assert-Equal 0 @($fixture.PublishProjects).Count 'Test-only fixture changes must not publish product binaries.'
}
$fixtureDocumentation = Get-OpenBladeCiPlan -Repository $repository -ChangedPath @('docs/native-windows-fixtures.md')
Assert-Equal 'none' $fixtureDocumentation.Mode 'Unconsumed documentation must not provision native fixtures.'
Assert-Equal 0 @($fixtureDocumentation.TestProjects).Count 'Documentation alone must not select fixture consumers.'

$manifest = Get-OpenBladeCiManifest -Repository $repository
$declaredContracts = @($manifest.contractRules | ForEach-Object { [string]$_.test } | Sort-Object)
$actualContracts = @(Get-ChildItem -LiteralPath (Join-Path $repository 'tests\Build.Tests') `
        -Filter '*.Tests.ps1' | ForEach-Object {
        'tests/Build.Tests/' + $_.Name
    } | Sort-Object)
$contractDifference = @(Compare-Object $declaredContracts $actualContracts)
if ($contractDifference.Count -ne 0) {
    throw "Every build-contract suite must have an explicit input rule: $((@($contractDifference | ForEach-Object { "$($_.SideIndicator) $($_.InputObject)" })) -join ', ')"
}

foreach ($contractInput in @(
        @{ Path = 'build/PublishPayloadValidation.ps1'; Test = 'FanStopGuardianPayload' },
        @{ Path = 'build/Publish.ps1'; Test = 'FanStopGuardianPayload' },
        @{ Path = 'src/OpenBlade.FanStop.Guardian/OpenBlade.FanStop.Guardian.csproj'; Test = 'FanStopGuardianPayload' },
        @{ Path = 'build/PawnIoPrerequisite.ps1'; Test = 'PawnIoPrerequisite' },
        @{ Path = 'build/Publish.ps1'; Test = 'PawnIoPrerequisite' },
        @{ Path = 'build/third-party/PawnIO-NOTICE.txt'; Test = 'PawnIoPrerequisite' },
        @{ Path = 'src/OpenBlade.CpuTemperature/OpenBlade.CpuTemperature.csproj'; Test = 'PawnIoPrerequisite' },
        @{ Path = 'third_party/LibreHardwareMonitor/LICENSE'; Test = 'PawnIoPrerequisite' },
        @{ Path = 'build/So6120CompletedAttemptArchive.ps1'; Test = 'So6120CompletedAttemptArchive' },
        @{ Path = 'build/Archive-So6120CompletedDeveloperAttempt.ps1'; Test = 'So6120CompletedAttemptArchive' })) {
    $contractPlan = Get-OpenBladeCiPlan -Repository $repository -ChangedPath @($contractInput.Path)
    if ($contractInput.Path.EndsWith('.csproj', [StringComparison]::OrdinalIgnoreCase)) {
        Assert-Equal 'full' $contractPlan.Mode 'Production project metadata must retain exhaustive contract coverage.'
    } else {
        Assert-Equal 'affected' $contractPlan.Mode 'Payload and archive inputs must have explicit affected coverage.'
        Assert-Contains $contractPlan.ContractTests `
            "tests/Build.Tests/$($contractInput.Test).Tests.ps1" `
            "Input '$($contractInput.Path)' must select its payload or archive contract suite."
    }
}

foreach ($lowFanInput in @(
        'build/LowFanValidationInstalledState.ps1',
        'build/Invoke-BatterySaverLowFanValidation.ps1',
        'build/Invoke-SilentLowFanValidation.ps1',
        'tests/Build.Tests/LowFanValidationInstalledState.Tests.ps1',
        'src/OpenBlade.Contracts/Overrides.cs',
        'src/OpenBlade.Service/ServiceControlRuntime.cs',
        'src/OpenBlade.Tray.Core/ServiceClient.cs')) {
    $lowFan = Get-OpenBladeCiPlan -Repository $repository -ChangedPath @($lowFanInput)
    Assert-Contains $lowFan.ContractTests `
        'tests/Build.Tests/LowFanValidationInstalledState.Tests.ps1' `
        'Low-fan workflow and installed-state contracts must run their restoration safety checks.'
}

foreach ($cpuContractInput in @(
        @{ Path = 'build/development-deployment/Publish-CpuVoltageValidation.ps1'; Test = 'tests/Build.Tests/CpuVoltageDeployment.Tests.ps1' },
        @{ Path = 'build/development-deployment/Invoke-LocalDevDeployment.ps1'; Test = 'tests/Build.Tests/CpuVoltageDeployment.Tests.ps1' },
        @{ Path = 'build/Publish.ps1'; Test = 'tests/Build.Tests/CpuVoltageValidationBuild.Tests.ps1' },
        @{ Path = 'build/BuildIdentity.ps1'; Test = 'tests/Build.Tests/CpuVoltageValidationBuild.Tests.ps1' })) {
    $cpuPlan = Get-OpenBladeCiPlan -Repository $repository -ChangedPath @($cpuContractInput.Path)
    Assert-Equal 'affected' $cpuPlan.Mode 'CPU packaging inputs must have explicit affected coverage.'
    Assert-Contains $cpuPlan.ContractTests $cpuContractInput.Test 'CPU source changes must select their contract suite.'
}

foreach ($legacyThxInput in @(
        'build/Export-LegacyThxBindingEvidence.ps1',
        'tests/Build.Tests/LegacyThxBindingEvidence.Tests.ps1',
        'docs/thx-installation-investigation.md')) {
    $legacyThx = Get-OpenBladeCiPlan -Repository $repository -ChangedPath @($legacyThxInput)
    Assert-Equal 'affected' $legacyThx.Mode 'Legacy THX evidence inputs must have declared CI coverage.'
    Assert-Contains $legacyThx.ContractTests `
        'tests/Build.Tests/LegacyThxBindingEvidence.Tests.ps1' `
        'Legacy THX source evidence changes must run their observational collector contracts.'
    Assert-Equal 0 $legacyThx.QualityProjects.Count `
        'Developer-only evidence collection must not require a product rebuild.'
    Assert-Equal $false $legacyThx.RunInstaller `
        'Developer-only evidence collection must not require installer publication.'
}

$ci = Get-Content -LiteralPath (Join-Path $repository '.github\workflows\ci.yml') -Raw
foreach ($requiredContract in @(
        'Test-OpenBladeCiFullBoundary -EventName $env:OPENBLADE_EVENT_NAME',
        'cancel-in-progress: ${{ github.event_name != ''schedule'' }}',
        'git diff --name-only --no-renames "${baseSha}...${headSha}" --',
        'schedule:',
        'cron: ''0 0 * * 0''',
        'group: ci-${{ github.workflow }}-${{ github.event_name }}-${{ github.ref }}',
        'Assert-OpenBladeCiSelectorHealthy',
        'Get-OpenBladeCiPlan',
        'validation-plan-json:',
        'run-component-publish:',
        'Format-OpenBladeCiPlan',
        'uses: ./.github/workflows/build.yml')) {
    if (-not $ci.Contains($requiredContract)) {
        throw "CI workflow is missing '$requiredContract'."
    }
}
if ($ci.Contains('if: ${{ needs.classify-changes.outputs.requires-build')) {
    throw 'The stable aggregate build check must not disappear for an empty plan.'
}

$outerGate = [Regex]::Match($ci, '(?ms)^  build-and-test:\r?\n.*?\z').Value
foreach ($required in @('name: build-and-test', '- classify-changes', '- validation', 'if: ${{ always() }}')) {
    if (-not $outerGate.Contains($required)) { throw "Stable CI check is missing '$required'." }
}
$gateRun = [Regex]::Match($outerGate, '(?ms)^        run: \|\r?\n(.*)\z')
if (-not $gateRun.Success) { throw 'Stable CI gate must execute its result check.' }
$gateScript = [ScriptBlock]::Create(($gateRun.Groups[1].Value -replace '(?m)^          ', ''))
$oldClassify = $env:OPENBLADE_CLASSIFY_RESULT
$oldValidation = $env:OPENBLADE_VALIDATION_RESULT
try {
    $env:OPENBLADE_CLASSIFY_RESULT = 'success'
    $env:OPENBLADE_VALIDATION_RESULT = 'success'
    & $gateScript
    foreach ($dependency in @('OPENBLADE_CLASSIFY_RESULT', 'OPENBLADE_VALIDATION_RESULT')) {
        foreach ($result in @('failure', 'cancelled', 'skipped')) {
            $env:OPENBLADE_CLASSIFY_RESULT = 'success'
            $env:OPENBLADE_VALIDATION_RESULT = 'success'
            [Environment]::SetEnvironmentVariable($dependency, $result, 'Process')
            $rejected = $false
            try { & $gateScript } catch { $rejected = $true }
            Assert-Equal $true $rejected "Stable check must reject $dependency=$result."
        }
    }
}
finally {
    $env:OPENBLADE_CLASSIFY_RESULT = $oldClassify
    $env:OPENBLADE_VALIDATION_RESULT = $oldValidation
}

if ($ci.Contains('OPENBLADE_PR_DRAFT') -or $ci.Contains('      - ready_for_review')) {
    throw 'Draft state must not change CI impact coverage or rerun unchanged commits.'
}

$build = Get-Content -LiteralPath (Join-Path $repository '.github\workflows\build.yml') -Raw
foreach ($requiredBuildContract in @(
        'validation-mode:',
        'Run affected quality and tests',
        'Run affected build contracts',
        'component-publish:',
        '.\build\Run-AffectedCi.ps1',
        '$expectedInputs = [ordered]@{',
        'OPENBLADE_COMPONENT_PUBLISH_RESULT:',
        '-RunComponentPublish')) {
    if (-not $build.Contains($requiredBuildContract)) {
        throw "Shared build workflow is missing '$requiredBuildContract'."
    }
}

$quality = [Regex]::Match($build, '(?ms)^  quality-and-tests:\r?\n.*?(?=^  build-contracts:)').Value
# Both jobs execute PowerShell fixture wrappers that resolve Python from PATH.
# Provision the same supported minor before either full or affected invocation.
function Assert-FixturePythonProvisioning {
    param([string]$JobText, [string[]]$Invocation)
    $setups = [Regex]::Matches($JobText, '(?ms)^      - name: Set up fixture Python\r?\n.*?(?=^      - name:|\z)')
    if ($setups.Count -ne 1) { throw 'Each fixture job must provision Python exactly once.' }
    $setup = $setups[0]
    foreach ($required in @('uses: actions/setup-python@v7', "python-version: '3.12'", 'architecture: x64')) {
        if (-not $setup.Value.Contains($required)) { throw "Fixture Python setup is missing '$required'." }
    }
    if ($setup.Value -match '(?m)^        if:') { throw 'Python setup must cover full and affected execution.' }
    foreach ($command in $Invocation) {
        $position = $JobText.IndexOf($command, [StringComparison]::Ordinal)
        if ($position -lt 0 -or $position -le $setup.Index) {
            throw "Supported Python must be provisioned before '$command'."
        }
    }
}
$contractJob = [Regex]::Match($build, '(?ms)^  build-contracts:\r?\n.*?(?=^  component-publish:)').Value
Assert-FixturePythonProvisioning -JobText $quality -Invocation @(
    'Invoke-OpenBladeNativeFixtureScope',
    '.\build\Run-AffectedCi.ps1')
Assert-FixturePythonProvisioning -JobText $contractJob -Invocation @(
    'ForEach-Object { & $_.FullName }',
    '.\build\Run-AffectedCi.ps1')
foreach ($invalidJob in @(
        ('Invoke-OpenBladeNativeFixtureScope' + [Environment]::NewLine + $quality),
        $quality.Replace('uses: actions/setup-python@v7', 'uses: removed-python-setup'),
        $quality.Replace("python-version: '3.12'", "python-version: '3.10'"),
        $quality.Replace('      - name: Set up fixture Python', '      - name: Removed fixture Python'))) {
    $rejected = $false
    try { Assert-FixturePythonProvisioning -JobText $invalidJob -Invocation @('Invoke-OpenBladeNativeFixtureScope') }
    catch { $rejected = $true }
    Assert-Equal $true $rejected 'Missing or unsupported Python provisioning must fail the CI contract.'
}

$fullTest = [Regex]::Match($quality, '(?ms)^      - name: Test\r?\n.*?(?=^      - name:)').Value
foreach ($required in @(
        "if: matrix.shard == 'solution'",
        'shell: pwsh',
        '. ./build/NativeWindowsFixtures.ps1',
        'Invoke-OpenBladeNativeFixtureScope',
        '-Action {',
        'dotnet test OpenBlade.slnx',
        '--no-build --no-restore',
        'if ($LASTEXITCODE -ne 0)')) {
    if (-not $fullTest.Contains($required)) { throw "Full quality fixture scope is missing '$required'." }
}
$affectedStep = [Regex]::Match($quality, '(?ms)^      - name: Run affected quality and tests\r?\n.*?(?=^      - name:)').Value
if (-not $affectedStep.Contains('shell: pwsh')) {
    throw 'Affected native fixtures require the same PowerShell 7 runtime as full quality.'
}
foreach ($required in @(
        'uses: actions/cache@v4',
        '~/.cargo/registry',
        '~/.cargo/git',
        'tools/openblade-windows-host/Cargo.lock',
        'fetch --locked --manifest-path tools/openblade-windows-host/Cargo.toml',
        'Run native interop tests')) {
    if (-not $quality.Contains($required)) { throw "Quality fixture preparation is missing '$required'." }
}
if ($fullTest.Contains('GITHUB_ENV')) {
    throw 'Native fixture environment must be owned by the scoped managed invocation.'
}
$affectedSource = Get-Content (Join-Path $repository 'build/Run-AffectedCi.ps1') -Raw
foreach ($required in @(
        '$fixtureConsumers = @(',
        "'tests/OpenBlade.Contracts.Tests/OpenBlade.Contracts.Tests.csproj'",
        "'tests/OpenBlade.Service.Tests/OpenBlade.Service.Tests.csproj'",
        "'tests/OpenBlade.Deployment.Tests/OpenBlade.Deployment.Tests.csproj'",
        '$testProjects | Where-Object { $_ -in $fixtureConsumers }',
        'Get-OpenBladeNativeFixturePrerequisiteProjects -Repository $repository',
        'Action = $runSelectedTests',
        'Invoke-OpenBladeNativeFixtureScope @fixtureScope',
        '& $runSelectedTests')) {
    if (-not $affectedSource.Contains($required)) { throw "Affected fixture routing is missing '$required'." }
}

# Full quality restores the solution once, which already owns every managed
# fixture prerequisite. Affected quality must provision these independently.
. (Join-Path $repository 'build/NativeWindowsFixtures.ps1')
$fixturePrerequisites = @(Get-OpenBladeNativeFixturePrerequisiteProjects -Repository $repository)
Assert-Equal 3 $fixturePrerequisites.Count 'Fixture preparation must declare its exact three managed projects.'
[xml]$solution = Get-Content (Join-Path $repository 'OpenBlade.slnx') -Raw
$solutionProjects = @($solution.SelectNodes('//Project') | ForEach-Object { [string]$_.Path })
foreach ($prerequisite in $fixturePrerequisites) {
    Assert-Contains $solutionProjects $prerequisite 'Full solution restore must include every fixture prerequisite.'
}
$fullRestore = [Regex]::Match($quality, '(?ms)^      - name: Restore\r?\n.*?(?=^      - name:)').Value
if (-not $fullRestore.Contains('dotnet restore OpenBlade.slnx -p:NuGetAudit=false')) {
    throw 'Full quality must retain its normal solution restore.'
}

# Exercise the actual affected dispatch with inert process/scope recorders.
# Substitute only the repository location and the helper import seam in memory.
$recordedRunner = $affectedSource.Replace(
    '$repository = Split-Path -Parent $PSScriptRoot',
    ('$repository = ' + "'" + $repository.Replace("'", "''") + "'")).Replace(
    ". (Join-Path " + '$repository' + " 'build/NativeWindowsFixtures.ps1')",
    '# Fixture scope is supplied by the command recorder.')
$dispatch = [ScriptBlock]::Create($recordedRunner)
$script:fixtureScopeCalls = 0
$script:recordedManagedTests = [Collections.Generic.List[string]]::new()
$script:recordedFixtureCommands = [Collections.Generic.List[object]]::new()
function dotnet {
    $script:recordedFixtureCommands.Add([string[]]$args)
    if ($args[0] -eq 'test') { $script:recordedManagedTests.Add([string]$args[1]) }
    $global:LASTEXITCODE = 0
}
function Invoke-OpenBladeNativeFixtureScope {
    param($Repository, $Configuration, $OutputRoot, [scriptblock]$Action)
    $script:fixtureScopeCalls++
    $restored = @($script:recordedFixtureCommands | Where-Object { $_[0] -eq 'restore' } | ForEach-Object { $_[1] })
    foreach ($prerequisite in $fixturePrerequisites) {
        Assert-Contains $restored $prerequisite 'Offline fixture preparation must have its packages restored before scope entry.'
    }
    & $Action
}
try {
    foreach ($selection in @(
            @('tests/OpenBlade.Contracts.Tests/OpenBlade.Contracts.Tests.csproj'),
            @('tests/OpenBlade.Service.Tests/OpenBlade.Service.Tests.csproj'),
            @('tests/OpenBlade.Deployment.Tests/OpenBlade.Deployment.Tests.csproj'),
            @('tests/OpenBlade.Contracts.Tests/OpenBlade.Contracts.Tests.csproj',
              'tests/OpenBlade.Service.Tests/OpenBlade.Service.Tests.csproj',
              'tests/OpenBlade.Deployment.Tests/OpenBlade.Deployment.Tests.csproj'))) {
        $script:fixtureScopeCalls = 0
        $script:recordedManagedTests.Clear()
        $script:recordedFixtureCommands.Clear()
        $recordedPlan = @{ mode = 'affected'; qualityProjects = @($selection); testProjects = @($selection) } | ConvertTo-Json -Compress
        & $dispatch -Lane Quality -PlanJson $recordedPlan
        $restores = @($script:recordedFixtureCommands | Where-Object { $_[0] -eq 'restore' })
        Assert-Equal 3 $restores.Count 'Contracts-only and combined plans must restore exactly the deduplicated fixture prerequisites.'
        foreach ($restore in $restores) {
            Assert-Equal ('restore|' + $restore[1] + '|-p:NuGetAudit=false') ($restore -join '|') 'Fixture prerequisites must use normal repository-configured restore semantics.'
        }
        $builds = @($script:recordedFixtureCommands | Where-Object { $_[0] -eq 'build' })
        Assert-Equal @($selection).Count $builds.Count 'Restoring fixture dependencies must not expand selected Quality builds.'
        Assert-Equal 1 $script:fixtureScopeCalls 'Affected fixture consumers must share exactly one preparation scope.'
        Assert-Equal @($selection).Count $script:recordedManagedTests.Count 'Affected fixture scope must run every selected managed consumer.'
    }
    $script:fixtureScopeCalls = 0
    $script:recordedFixtureCommands.Clear()
    $unrelated = 'tests/OpenBlade.Protocol.Tests/OpenBlade.Protocol.Tests.csproj'
    $recordedPlan = @{ mode = 'affected'; qualityProjects = @($unrelated); testProjects = @($unrelated) } | ConvertTo-Json -Compress
    & $dispatch -Lane Quality -PlanJson $recordedPlan
    Assert-Equal 0 $script:fixtureScopeCalls 'Unrelated affected tests must not prepare native fixture corpora.'
    $unrelatedRestores = @($script:recordedFixtureCommands | Where-Object { $_[0] -eq 'restore' })
    Assert-Equal 1 $unrelatedRestores.Count 'Unrelated plans must not restore fixture prerequisite projects.'
}
finally {
    Remove-Item Function:dotnet
    Remove-Item Function:Invoke-OpenBladeNativeFixtureScope
}

if ($build.Contains('OPENBLADE_CHECKOUT_CAPTURES') -or
    $build.Contains('checkout-captures:')) {
    throw 'The shared build workflow must not depend on the removed captures submodule.'
}

$contractsStart = $build.IndexOf('  build-contracts:', [StringComparison]::Ordinal)
$contractsEnd = $build.IndexOf('  component-publish:', $contractsStart, [StringComparison]::Ordinal)
$contracts = $build.Substring($contractsStart, $contractsEnd - $contractsStart)
$uploadStart = $contracts.IndexOf('      - name: Retain localization workflow failure evidence', [StringComparison]::Ordinal)
if ($uploadStart -lt 0) {
    throw 'Build contracts must retain translator mismatch evidence on hosted runners.'
}
$nextStep = $contracts.IndexOf("`n      - name:", $uploadStart, [StringComparison]::Ordinal)
$upload = if ($nextStep -lt 0) {
    $contracts.Substring($uploadStart)
} else {
    $contracts.Substring($uploadStart, $nextStep - $uploadStart)
}
foreach ($required in @(
        'if: ${{ failure() }}',
        'uses: actions/upload-artifact@v4',
        'name: translator-archive-failures-${{ github.run_id }}-${{ github.run_attempt }}',
        'path: artifacts/translator-archive-failures/',
        'if-no-files-found: ignore',
        'retention-days: 14')) {
    if (-not $upload.Contains($required)) {
        throw "Translator failure-artifact handoff is missing '$required'."
    }
}

$uploadPathMatch = [Regex]::Match($upload, '(?m)^          path:\s*(\S+)\s*$')
if (-not $uploadPathMatch.Success) {
    throw 'Translator evidence upload must declare one artifact directory.'
}
$uploadPath = $uploadPathMatch.Groups[1].Value.TrimEnd('/')
$translatorTest = [IO.File]::ReadAllText((Join-Path $repository 'tests\Build.Tests\LocalizationTranslatorWorkflow.Tests.ps1'))
$normalizedProducer = $translatorTest.Replace('\', '/')
if (-not $normalizedProducer.Contains(
        '$archiveEvidenceRoot = Join-Path $repository ' + "'" + $uploadPath + "'")) {
    throw 'Translator archive producer and workflow uploader must use the same evidence directory.'
}

Write-Host 'CI build scope tests passed.'
