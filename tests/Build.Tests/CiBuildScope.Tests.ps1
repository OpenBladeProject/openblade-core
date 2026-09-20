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
Assert-NotContains $contracts.AffectedProjects `
    'src/OpenBlade.Display/OpenBlade.Display.csproj' `
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

$captures = Get-OpenBladeCiPlan -Repository $repository -ChangedPath @('captures')
Assert-Equal $true $captures.RequiresCaptures `
    'Capture pointer changes must initialize the evidence submodule.'
Assert-Contains $captures.TestProjects `
    'tests/OpenBlade.Protocol.Tests/OpenBlade.Protocol.Tests.csproj' `
    'Capture changes must run Protocol evidence tests.'
Assert-Contains $captures.ContractTests `
    'tests/Build.Tests/UsbPcapCaptureControl.Tests.ps1' `
    'Capture changes must run capture-tool contracts.'

$sparseContract = Get-OpenBladeCiPlan -Repository $repository -ChangedPath @(
    'tests/Build.Tests/SparsePackageIdentity.Tests.ps1')
Assert-Contains $sparseContract.ContractRestoreProjects `
    'src/OpenBlade.SessionAgent/OpenBlade.SessionAgent.csproj' `
    'A changed sparse-identity suite must restore its publish input.'
$captureContract = Get-OpenBladeCiPlan -Repository $repository -ChangedPath @(
    'tests/Build.Tests/UsbPcapCaptureControl.Tests.ps1')
Assert-Equal $true $captureContract.RequiresCaptures `
    'A changed capture contract must initialize its submodule input.'
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

foreach ($globalPath in @(
        'Directory.Build.props',
        'src/OpenBlade.Service/OpenBlade.Service.csproj',
        '.github/workflows/ci.yml',
        'tools/new-script.ps1')) {
    $full = Get-OpenBladeCiPlan -Repository $repository -ChangedPath @($globalPath)
    Assert-Equal 'full' $full.Mode `
        "Global or unknown path '$globalPath' must fail closed."
}
$forced = Get-OpenBladeCiPlan -Repository $repository -ChangedPath @('README.md') -ForceFull
Assert-Equal 'full' $forced.Mode 'Forced workflow boundaries must run full validation.'
$empty = Get-OpenBladeCiPlan -Repository $repository -ChangedPath @()
Assert-Equal 'full' $empty.Mode 'An empty change set must fail closed.'

$manifest = Get-OpenBladeCiManifest -Repository $repository
$declaredContracts = @($manifest.contractRules | ForEach-Object { [string]$_.test } | Sort-Object)
$actualContracts = @(Get-ChildItem -LiteralPath (Join-Path $repository 'tests\Build.Tests') `
        -Filter '*.Tests.ps1' | ForEach-Object {
        'tests/Build.Tests/' + $_.Name
    } | Sort-Object)
$contractDifference = @(Compare-Object $declaredContracts $actualContracts)
if ($contractDifference.Count -ne 0) {
    throw "Every build-contract suite must have an explicit input rule: $($contractDifference -join ', ')"
}

$ci = Get-Content -LiteralPath (Join-Path $repository '.github\workflows\ci.yml') -Raw
foreach ($requiredContract in @(
        'OPENBLADE_PR_DRAFT:',
        'Assert-OpenBladeCiSelectorHealthy',
        'Get-OpenBladeCiPlan',
        'validation-plan-json:',
        'run-component-publish:',
        'checkout-captures:',
        'Format-OpenBladeCiPlan',
        'uses: ./.github/workflows/build.yml')) {
    if (-not $ci.Contains($requiredContract)) {
        throw "CI workflow is missing '$requiredContract'."
    }
}
if ($ci.Contains('if: ${{ needs.classify-changes.outputs.requires-build')) {
    throw 'The stable aggregate build check must not disappear for an empty plan.'
}

$build = Get-Content -LiteralPath (Join-Path $repository '.github\workflows\build.yml') -Raw
foreach ($requiredBuildContract in @(
        'validation-mode:',
        'Run affected quality and tests',
        'Run affected build contracts',
        'component-publish:',
        '.\build\Run-AffectedCi.ps1',
        '$expectedInputs = [ordered]@{',
        'OPENBLADE_CHECKOUT_CAPTURES',
        'OPENBLADE_COMPONENT_PUBLISH_RESULT:',
        '-RunComponentPublish')) {
    if (-not $build.Contains($requiredBuildContract)) {
        throw "Shared build workflow is missing '$requiredBuildContract'."
    }
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
