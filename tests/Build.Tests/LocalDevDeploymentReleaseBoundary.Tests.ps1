[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$repository = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)

function Assert-True {
    param(
        [Parameter(Mandatory)][bool]$Condition,
        [Parameter(Mandatory)][string]$Message
    )

    if (-not $Condition) {
        throw $Message
    }
}

$publishPath = Join-Path $repository 'build\Publish.ps1'
$publishSource = [IO.File]::ReadAllText($publishPath)
$tokens = $null
$parseErrors = $null
$publishAst = [Management.Automation.Language.Parser]::ParseFile(
    $publishPath,
    [ref]$tokens,
    [ref]$parseErrors)
Assert-True ($null -ne $publishAst -and @($parseErrors).Count -eq 0) `
    'Publish.ps1 contains a PowerShell parser error.'

foreach ($forbiddenParameter in @(
        'UnattendedReleaseSigningKeyName',
        'UnattendedReleaseSigningKeyProvider')) {
    Assert-True (-not ($publishAst.ParamBlock.Parameters.Name.VariablePath.UserPath -ccontains
            $forbiddenParameter)) `
        "Ordinary publishing must not accept local-development parameter '$forbiddenParameter'."
}

$storeSource = [IO.File]::ReadAllText((
        Join-Path $repository 'build\StoreSubmissionValidation.ps1'))
$bundleSource = [IO.File]::ReadAllText((
        Join-Path $repository 'build\installer\Bundle.wxs'))
$bootstrapperSource = [IO.File]::ReadAllText((Join-Path $repository (
            'build\installer\OpenBlade.Installer.UI\OpenBladeBootstrapperApplication.cs')))
$installerUiProjectSource = [IO.File]::ReadAllText((Join-Path $repository (
            'build\installer\OpenBlade.Installer.UI\OpenBlade.Installer.UI.csproj')))
$setupProjectSource = [IO.File]::ReadAllText((
        Join-Path $repository 'src\OpenBlade.Setup\OpenBlade.Setup.csproj'))
$setupProgramSource = [IO.File]::ReadAllText((
        Join-Path $repository 'src\OpenBlade.Setup\Program.cs'))
$invokeLocalDevSource = [IO.File]::ReadAllText((
        Join-Path $repository 'build\development-deployment\Invoke-LocalDevDeployment.ps1'))
$thermalValidationBuildSource = [IO.File]::ReadAllText((
        Join-Path $repository 'build\Build-ThermalExternalDriftValidation.ps1'))

foreach ($forbiddenPublishText in @(
        'src\OpenBlade.DeploymentBroker\OpenBlade.DeploymentBroker.csproj',
        'src\OpenBlade.DeploymentClient\OpenBlade.DeploymentClient.csproj',
        'src\OpenBlade.DevelopmentDeployment.Setup\OpenBlade.DevelopmentDeployment.Setup.csproj',
        'build\development-deployment',
        'OpenBlade-Stable-Release',
        'OpenBlade.ReleaseTool')) {
    Assert-True (-not $publishSource.Contains($forbiddenPublishText)) `
        "Ordinary publishing must not build or ship local-development input '$forbiddenPublishText'."
}
foreach ($requiredRejection in @(
        'OpenBlade.DeploymentBroker.*',
        'OpenBlade.DeploymentClient.*',
        'OpenBlade.DevelopmentDeployment.Setup.*',
        'OpenBlade.LocalDevDeployment.Tool.*',
        'OpenBlade.Deployment.dll',
        'OpenBlade.LocalDevDeployment.json',
        'OpenBlade-Local-Dev-Deployment.json',
        'OpenBlade-Local-Dev-Deployment.sig',
        'Release publishing contains local-development deployment artifact')) {
    Assert-True ($publishSource.Contains($requiredRejection)) `
        "Ordinary publishing must reject local-development artifact '$requiredRejection'."
}

foreach ($forbiddenStoreText in @(
        'Get-OpenBladeUnattendedDeploymentSigningPolicy',
        'Get-OpenBladeUnattendedDeploymentReadiness',
        'UnattendedDeploymentPolicyPath',
        'ReleaseManifestValidated',
        'ReleaseSigningKeyId',
        'OpenBlade-Stable-Release')) {
    Assert-True (-not $storeSource.Contains($forbiddenStoreText)) `
        "Store readiness must remain independent of local development token '$forbiddenStoreText'."
}
Assert-True ($storeSource.Contains('Assert-NoOpenBladeLocalDevelopmentArtifacts')) `
    'Store readiness must reject local-development artifacts before inventorying signatures.'
foreach ($requiredStoreRejection in @(
        'OpenBlade.DeploymentBroker.*',
        'OpenBlade.DeploymentClient.*',
        'OpenBlade.DevelopmentDeployment.Setup.*',
        'OpenBlade.LocalDevDeployment.Tool.*',
        'OpenBlade.Deployment.dll',
        'OpenBlade.LocalDevDeployment.json',
        'OpenBlade-Local-Dev-Deployment.json',
        'OpenBlade-Local-Dev-Deployment.sig')) {
    Assert-True ($storeSource.Contains($requiredStoreRejection)) `
        "Store readiness must reject local-development artifact '$requiredStoreRejection'."
}

foreach ($source in @($bundleSource, $bootstrapperSource)) {
    foreach ($forbiddenText in @(
            'OpenBlade.DeploymentBroker',
            'OpenBlade.DeploymentClient',
            'OpenBlade.LocalDevDeployment',
            'OpenBladeUnattendedBroker',
            '--unattended-broker',
            '--enrolled-user-sid',
            'BrokerInstallerLaunchGate')) {
        Assert-True (-not $source.Contains($forbiddenText)) `
            "Published installer authoring must not contain local-development token '$forbiddenText'."
    }
}

Assert-True (
    $setupProjectSource.Contains("'`$(OpenBladeLocalDevDeployment)' != 'true'") -and
    $setupProjectSource.Contains('<Compile Remove="LocalDevEnrollmentUser.cs" />') -and
    $setupProjectSource.Contains('<Compile Remove="LocalDevRelatedBundleCompatibility.cs" />')) `
    'Ordinary Setup builds must compile out local-development enrollment and cached-bundle compatibility.'
Assert-True (
    $setupProgramSource.Contains('#if OPENBLADE_LOCAL_DEV_DEPLOYMENT') -and
    $setupProgramSource.Contains('LocalDevRelatedBundleCompatibility.PrepareCachedSetupEnvironment()') -and
    $publishSource.Contains("if (`$buildIdentity.Channel -eq 'LocalDev')") -and
    $publishSource.Contains('--property:OpenBladeLocalDevDeployment=true')) `
    'Only an explicit local-development publish may compile the enrollment handoff into Setup.'
Assert-True (
    $installerUiProjectSource.Contains("'`$(OpenBladeLocalDevDeployment)' == 'true'") -and
    $installerUiProjectSource.Contains('OPENBLADE_LOCAL_DEV_DEPLOYMENT') -and
    $bootstrapperSource.Contains('#if OPENBLADE_LOCAL_DEV_DEPLOYMENT') -and
    $bootstrapperSource.Contains('LocalDevCompatibilityEnabled = true') -and
    $publishSource.Contains(
        "`$installerUiPublishArguments += '--property:OpenBladeLocalDevDeployment=true'")) `
    'Only an explicit local-development publish may enable the installer compatibility transition.'
Assert-True ($setupProgramSource.Contains('SetupPaths.CreateForCurrentIdentity()')) `
    'Every Setup build must select LocalSystem-safe known folders at runtime for related-bundle removal.'
Assert-True (
    $invokeLocalDevSource.Contains('Get-OpenBladeNextLocalDevBuildVersion') -and
    $invokeLocalDevSource.Contains('-BuildChannel LocalDev') -and
    $invokeLocalDevSource.Contains('-LocalDevVersion $localDevVersion')) `
    'Successive local builds must use the reserved monotonic Burn identity.'
Assert-True (
    $publishSource.Contains('[switch]$ThermalExternalDriftValidation') -and
    $publishSource.Contains("`$buildIdentity.Channel -ne 'LocalDev'") -and
    $publishSource.Contains('--property:OpenBladeThermalExternalDriftValidation=true') -and
    $publishSource.Contains('--property:OpenBladeThermalExternalDriftValidation=false')) `
    'Thermal validation must be an explicit LocalDev-only Service marker with an ordinary disabled path.'
Assert-True (
    $invokeLocalDevSource.Contains('[switch]$ThermalExternalDriftValidation') -and
    $invokeLocalDevSource.Contains('-ValidationOutputDirectory is required') -and
    $invokeLocalDevSource.Contains('Build-ThermalExternalDriftValidation.ps1') -and
    $thermalValidationBuildSource.Contains('-BuildChannel LocalDev') -and
    $thermalValidationBuildSource.Contains('-ThermalExternalDriftValidation')) `
    'The fixed LocalDev workflow must require explicit validation opt-in and build one coordinated installer.'
foreach ($forbiddenBrokerExpansion in @(
        'OpenBlade.DeploymentBroker.exe',
        '--operation',
        '--installer-arguments')) {
    Assert-True (-not $thermalValidationBuildSource.Contains($forbiddenBrokerExpansion)) `
        "Thermal validation must not expand the broker surface with '$forbiddenBrokerExpansion'."
}

Write-Host 'Local-development deployment release-boundary tests passed.'
