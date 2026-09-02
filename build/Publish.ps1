[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Release',
    [string]$VcRedistPath,
    [string]$SigningThumbprint,
    [string]$SignTool,
    [string]$MakeAppx,
    [string]$TimestampUrl = 'http://timestamp.digicert.com',
    [switch]$VerifyPrivacyPolicy,
    [ValidateSet('Stable', 'Nightly', 'LocalDev')]
    [string]$BuildChannel = 'Stable',
    [ValidateRange(0, 65535)]
    [int]$BuildRevision = 0,
    [string]$LocalDevVersion,
    [string]$SourceRevision,
    [string]$WorkflowRunId,
    [switch]$ThermalExternalDriftValidation
)

Set-StrictMode -Version 3.0
$ErrorActionPreference = 'Stop'
$repository = Split-Path -Parent $PSScriptRoot
$artifacts = [IO.Path]::GetFullPath((Join-Path $repository 'artifacts'))
. (Join-Path $PSScriptRoot 'PublishPayloadValidation.ps1')
. (Join-Path $PSScriptRoot 'InstallerValidation.ps1')
. (Join-Path $PSScriptRoot 'InstallerPrerequisites.ps1')
. (Join-Path $PSScriptRoot 'StoreSubmissionValidation.ps1')
. (Join-Path $PSScriptRoot 'WindowsAppRuntimeNotificationResource.ps1')
. (Join-Path $PSScriptRoot 'BuildIdentity.ps1')
. (Join-Path $PSScriptRoot 'SparsePackageIdentity.ps1')

& (Join-Path $PSScriptRoot 'Verify-Branding.ps1') -Repository $repository
if ($LASTEXITCODE -ne 0) {
    throw "OpenBlade branding verification failed with exit code $LASTEXITCODE"
}

$bundle = [IO.Path]::GetFullPath((Join-Path $artifacts 'win-x64\OpenBlade'))
$installer = [IO.Path]::GetFullPath((Join-Path $artifacts 'win-x64\OpenBlade-Setup.exe'))
$storeReadiness = [IO.Path]::GetFullPath((
    Join-Path $artifacts 'win-x64\OpenBlade-Store-Readiness.json'))
$nightlyMetadata = [IO.Path]::GetFullPath((
    Join-Path $artifacts 'win-x64\OpenBlade-Nightly.json'))
$nightlyChecksum = [IO.Path]::GetFullPath((
    Join-Path $artifacts 'win-x64\OpenBlade-Setup.exe.sha256'))
$developerTools = [IO.Path]::GetFullPath((Join-Path $artifacts 'win-x64\OpenBlade.DeveloperTools'))
$stagingRoot = [IO.Path]::GetFullPath((Join-Path $artifacts ('.staging\' + [Guid]::NewGuid().ToString('N'))))
$stagingBundle = Join-Path $stagingRoot 'OpenBlade'
$stagingDeveloperTools = Join-Path $stagingRoot 'OpenBlade.DeveloperTools'
$stagingInstallerOutput = Join-Path $stagingRoot 'installer'
$stagingInstallerUi = Join-Path $stagingRoot 'installer-ui'
$stagingUpgradeProbeOutput = Join-Path $stagingRoot 'installer-upgrade-probe'
$stagingUpgradeProbe = Join-Path $stagingUpgradeProbeOutput 'OpenBlade-Setup.exe'
$stagingUpgradeProbeObj = (
    Join-Path $stagingRoot 'installer-upgrade-probe-obj').TrimEnd(
        [IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
$stagingPrerequisites = Join-Path $stagingRoot 'prerequisites'
$stagingInstaller = Join-Path $stagingInstallerOutput 'OpenBlade-Setup.exe'
$stagingStoreReadiness = Join-Path $stagingRoot 'OpenBlade-Store-Readiness.json'
$stagingNightlyMetadata = Join-Path $stagingRoot 'OpenBlade-Nightly.json'
$stagingNightlyChecksum = Join-Path $stagingRoot 'OpenBlade-Setup.exe.sha256'
$payload = Join-Path $stagingBundle 'payload'

function Assert-SafeArtifactPath {
    param([Parameter(Mandatory)][string]$Path)

    $candidate = [IO.Path]::GetFullPath($Path).TrimEnd([IO.Path]::DirectorySeparatorChar)
    $root = $artifacts.TrimEnd([IO.Path]::DirectorySeparatorChar)
    $prefix = $root + [IO.Path]::DirectorySeparatorChar
    if (-not $candidate.StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Refusing to modify path outside the artifacts directory: $candidate"
    }
}

function Invoke-CheckedPublish {
    param(
        [Parameter(Mandatory)][string]$Project,
        [Parameter(Mandatory)][string]$Output,
        [string[]]$AdditionalArguments = @()
    )

    & dotnet publish $Project @publishArguments @AdditionalArguments --output $Output
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet publish failed with exit code $LASTEXITCODE for $Project"
    }
}

Assert-SafeArtifactPath -Path $bundle
Assert-SafeArtifactPath -Path $installer
Assert-SafeArtifactPath -Path $storeReadiness
Assert-SafeArtifactPath -Path $nightlyMetadata
Assert-SafeArtifactPath -Path $nightlyChecksum
Assert-SafeArtifactPath -Path $developerTools
Assert-SafeArtifactPath -Path $stagingRoot

[xml]$buildProperties = Get-Content -LiteralPath (Join-Path $repository 'Directory.Build.props')
$openBladeVersionNode = $buildProperties.SelectSingleNode(
    '/Project/PropertyGroup/OpenBladeVersion')
if ($null -eq $openBladeVersionNode -or
    [string]::IsNullOrWhiteSpace($openBladeVersionNode.InnerText)) {
    throw 'Directory.Build.props does not define OpenBladeVersion.'
}
$buildIdentity = Resolve-OpenBladeBuildIdentity `
    -BaseVersion $openBladeVersionNode.InnerText `
    -Channel $BuildChannel `
    -BuildRevision $BuildRevision `
    -LocalDevVersion $LocalDevVersion `
    -SourceRevision $SourceRevision

if ($ThermalExternalDriftValidation -and $buildIdentity.Channel -ne 'LocalDev') {
    throw 'Thermal external-drift validation installers are restricted to the LocalDev channel.'
}
if ($ThermalExternalDriftValidation -and $SourceRevision -notmatch '^[0-9a-fA-F]{40}$') {
    throw 'Thermal external-drift validation installers require an exact source revision.'
}

function Invoke-OpenBladeSigning {
    param(
        [Parameter(Mandatory)][string]$Path,
        [Parameter(Mandatory)][string]$ResolvedSignTool
    )

    & $ResolvedSignTool sign `
        /sha1 $SigningThumbprint `
        /fd SHA256 `
        /tr $TimestampUrl `
        /td SHA256 `
        $Path
    if ($LASTEXITCODE -ne 0) {
        throw "Authenticode signing failed with exit code $LASTEXITCODE for $Path"
    }
}
$publishArguments = @(
    '--configuration', $Configuration,
    '--runtime', 'win-x64',
    '--self-contained', 'true',
    '--property:PublishTrimmed=false',
    '--property:OpenBladePadlessHyperBoostLifecycleValidation=false'
)
if ($buildIdentity.Channel -ne 'Stable') {
    $publishArguments += @(
        "--property:Version=$($buildIdentity.Version)",
        "--property:AssemblyVersion=$($buildIdentity.AssemblyVersion)",
        "--property:FileVersion=$($buildIdentity.FileVersion)",
        "--property:InformationalVersion=$($buildIdentity.InformationalVersion)",
        '--property:IncludeSourceRevisionInInformationalVersion=false'
    )
}
$productionPublishArguments = @(
    '--property:DebugType=None',
    '--property:DebugSymbols=false',
    # Keep only the supported production cultures. qps-ploc remains a
    # development-only qualifier and satellite resource.
    '--property:SatelliteResourceLanguages=en-US%3Bzh-CN%3Bru-RU',
    '--property:OpenBladeIncludePseudoLocalization=false'
)
$serviceValidationArguments = @(
    if ($ThermalExternalDriftValidation) {
        '--property:OpenBladeThermalExternalDriftValidation=true',
        "--property:SourceRevision=$SourceRevision"
    }
    else {
        '--property:OpenBladeThermalExternalDriftValidation=false'
    }
)
$setupPublishArguments = @(
    '--property:PublishSingleFile=true',
    '--property:IncludeNativeLibrariesForSelfExtract=true',
    '--property:EnableCompressionInSingleFile=true'
)
$installerUiPublishArguments = @()
if ($buildIdentity.Channel -eq 'LocalDev') {
    $setupPublishArguments += '--property:OpenBladeLocalDevDeployment=true'
    $installerUiPublishArguments += '--property:OpenBladeLocalDevDeployment=true'
}
$signingEnabled = -not [string]::IsNullOrWhiteSpace($SigningThumbprint)
$resolvedSignTool = $null
if ($signingEnabled) {
    if ([string]::IsNullOrWhiteSpace($SignTool)) {
        $signToolCommand = Get-Command 'signtool.exe' -ErrorAction Stop
        $resolvedSignTool = $signToolCommand.Source
    }
    else {
        $resolvedSignTool = [IO.Path]::GetFullPath($SignTool)
        if (-not (Test-Path -LiteralPath $resolvedSignTool -PathType Leaf)) {
            throw "The requested SignTool executable is missing: $resolvedSignTool"
        }
    }
}

try {
    New-Item -ItemType Directory -Path $payload -Force | Out-Null

    Invoke-CheckedPublish (Join-Path $repository 'src\OpenBlade.Service\OpenBlade.Service.csproj') `
        $payload ($productionPublishArguments + $serviceValidationArguments)
    Invoke-CheckedPublish (Join-Path $repository 'src\OpenBlade.SessionAgent\OpenBlade.SessionAgent.csproj') `
        $payload $productionPublishArguments
    Invoke-CheckedPublish (Join-Path $repository 'src\OpenBlade.Tray\OpenBlade.Tray.csproj') `
        $payload $productionPublishArguments
    $trayAssets = Join-Path $repository (
        'src\OpenBlade.Tray\obj\project.assets.json')
    Add-WindowsAppRuntimeNotificationResource `
        -Payload $payload `
        -RuntimeVersion (
            Get-WindowsAppRuntimeVersionFromAssets -AssetsPath $trayAssets)
    # Protocol is shared by the installed service and the capture validator.
    # Publish both with identical debug/satellite properties so a clean build
    # cannot produce byte-different Protocol assemblies solely because one
    # contains a portable-PDB debug directory.
    Invoke-CheckedPublish (Join-Path $repository 'src\OpenBlade.Capture\OpenBlade.Capture.csproj') `
        $stagingDeveloperTools $productionPublishArguments
    Invoke-CheckedPublish (Join-Path $repository 'src\OpenBlade.Setup\OpenBlade.Setup.csproj') `
        $stagingBundle ($productionPublishArguments + $setupPublishArguments)
    $forbiddenDevelopmentPayload = Get-ChildItem `
        -LiteralPath $stagingBundle `
        -Recurse `
        -File `
        -Force | Where-Object {
            $_.Name -like 'OpenBlade.DeploymentBroker.*' -or
            $_.Name -like 'OpenBlade.DeploymentClient.*' -or
            $_.Name -like 'OpenBlade.DevelopmentDeployment.Setup.*' -or
            $_.Name -like 'OpenBlade.LocalDevDeployment.Tool.*' -or
            $_.Name -eq 'OpenBlade.Deployment.dll' -or
            $_.Name -in @(
                'OpenBlade.LocalDevDeployment.json',
                'OpenBlade-Local-Dev-Deployment.json',
                'OpenBlade-Local-Dev-Deployment.sig')
        } | Select-Object -First 1
    if ($null -ne $forbiddenDevelopmentPayload) {
        throw "Release publishing contains local-development deployment artifact '$($forbiddenDevelopmentPayload.FullName)'."
    }
    $installerUiProject = Join-Path $repository (
        'build\installer\OpenBlade.Installer.UI\OpenBlade.Installer.UI.csproj')
    $installerUiTestProject = Join-Path $repository (
        'build\installer\OpenBlade.Installer.UI.Tests\OpenBlade.Installer.UI.Tests.csproj')
    & dotnet test $installerUiTestProject `
        --configuration $Configuration `
        --framework net472 `
        --runtime win-x64 `
        --property:NuGetAudit=false
    if ($LASTEXITCODE -ne 0) {
        throw "OpenBlade installer UI tests failed with exit code $LASTEXITCODE"
    }
    & dotnet publish $installerUiProject `
        --configuration $Configuration `
        --framework net472 `
        --runtime win-x64 `
        --self-contained false `
        --property:NuGetAudit=false `
        --property:DebugType=None `
        --property:DebugSymbols=false `
        --property:OpenBladeIncludePseudoLocalization=false `
        @installerUiPublishArguments `
        --output $stagingInstallerUi
    if ($LASTEXITCODE -ne 0) {
        throw "OpenBlade installer UI publish failed with exit code $LASTEXITCODE"
    }

    $captureExecutable = Join-Path $stagingDeveloperTools 'OpenBlade.Capture.exe'
    if (-not (Test-Path -LiteralPath $captureExecutable -PathType Leaf)) {
        throw "OpenBlade developer-tools artifact is missing: $captureExecutable"
    }

    Assert-LightingValidationProtocolPair `
        -ServicePayload $payload `
        -DeveloperTools $stagingDeveloperTools

    Assert-ProductionBundleFootprint -Bundle $stagingBundle
    [void](Copy-OpenBladeVcRedist `
        -SourcePath $VcRedistPath `
        -DestinationDirectory $stagingPrerequisites)

    if ($signingEnabled) {
        foreach ($file in Get-ChildItem -LiteralPath $stagingBundle -Recurse -File |
            Where-Object {
                ($_.Extension -in @('.exe', '.dll')) -and
                $_.Name.StartsWith('OpenBlade.', [StringComparison]::Ordinal)
            }) {
            Invoke-OpenBladeSigning -Path $file.FullName -ResolvedSignTool $resolvedSignTool
        }
        Invoke-OpenBladeSigning `
            -Path (Join-Path $stagingInstallerUi 'OpenBlade.Installer.UI.exe') `
            -ResolvedSignTool $resolvedSignTool
    }

    New-Item -ItemType Directory -Path $stagingInstallerOutput -Force | Out-Null
    $installerBuildArguments = @(
        'build',
        (Join-Path $repository 'build\installer\OpenBlade.Installer.wixproj'),
        '--configuration', $Configuration,
        '--property:NuGetAudit=false',
        "--property:OpenBladeBundle=$stagingBundle",
        "--property:OpenBladePrerequisites=$stagingPrerequisites",
        "--property:OpenBladeInstallerUi=$stagingInstallerUi",
        "--property:OpenBladeVersion=$($buildIdentity.BundleVersion)",
        '--output', $stagingInstallerOutput
    )
    if ($signingEnabled) {
        $installerBuildArguments += @(
            '--property:SignOutput=true',
            "--property:OpenBladeSigningThumbprint=$SigningThumbprint",
            "--property:OpenBladeSignTool=$resolvedSignTool",
            "--property:OpenBladeTimestampUrl=$TimestampUrl"
        )
    }
    & dotnet @installerBuildArguments
    if ($LASTEXITCODE -ne 0) {
        throw "OpenBlade installer build failed with exit code $LASTEXITCODE"
    }

    Assert-OpenBladeInstallerBundle `
        -Installer $stagingInstaller `
        -PublishedBundle $stagingBundle `
        -InstallerProject (Join-Path $repository 'build\installer\OpenBlade.Installer.wixproj') `
        -ExpectedVersion $buildIdentity.BundleVersion `
        -ValidationRoot (Join-Path $stagingRoot 'installer-validation') `
        -PermittedRoot $artifacts

    $parsedVersion = [Version]$buildIdentity.BundleVersion
    $upgradeProbeVersion = '{0}.{1}.{2}' -f @(
        $parsedVersion.Major,
        $parsedVersion.Minor,
        ($parsedVersion.Build + 1))
    New-Item -ItemType Directory -Path $stagingUpgradeProbeOutput -Force | Out-Null
    & dotnet build (Join-Path $repository 'build\installer\OpenBlade.Installer.wixproj') `
        --configuration $Configuration `
        --property:NuGetAudit=false `
        --property:OpenBladeBundle=$stagingBundle `
        --property:OpenBladePrerequisites=$stagingPrerequisites `
        --property:OpenBladeInstallerUi=$stagingInstallerUi `
        --property:OpenBladeVersion=$upgradeProbeVersion `
        --property:BaseIntermediateOutputPath=$stagingUpgradeProbeObj `
        --property:MSBuildProjectExtensionsPath=$stagingUpgradeProbeObj `
        --output $stagingUpgradeProbeOutput
    if ($LASTEXITCODE -ne 0) {
        throw "OpenBlade upgrade-identity probe build failed with exit code $LASTEXITCODE"
    }
    Assert-OpenBladeInstallerBundle `
        -Installer $stagingUpgradeProbe `
        -PublishedBundle $stagingBundle `
        -InstallerProject (Join-Path $repository 'build\installer\OpenBlade.Installer.wixproj') `
        -ExpectedVersion $upgradeProbeVersion `
        -ValidationRoot (Join-Path $stagingRoot 'upgrade-probe-validation') `
        -PermittedRoot $artifacts

    Write-OpenBladeStoreReadinessReport `
        -Bundle $stagingBundle `
        -Installer $stagingInstaller `
        -ReportPath $stagingStoreReadiness `
        -RequireSignatures:$signingEnabled `
        -VerifyPrivacyPolicy:$VerifyPrivacyPolicy | Out-Null

    if ($buildIdentity.Channel -eq 'Nightly') {
        Write-OpenBladeNightlyArtifactMetadata `
            -Installer $stagingInstaller `
            -MetadataPath $stagingNightlyMetadata `
            -ChecksumPath $stagingNightlyChecksum `
            -BuildIdentity $buildIdentity `
            -WorkflowRunId $WorkflowRunId | Out-Null
    }

    Complete-ValidatedBundleReplacement `
        -StagingBundle $stagingBundle `
        -Bundle $bundle `
        -PermittedRoot $artifacts

    if (Test-Path -LiteralPath $developerTools) {
        Remove-ValidatedPublishPath -Path $developerTools -PermittedRoot $artifacts
    }
    New-Item -ItemType Directory -Path (Split-Path -Parent $developerTools) -Force | Out-Null
    Move-Item -LiteralPath $stagingDeveloperTools -Destination $developerTools

    if (Test-Path -LiteralPath $installer) {
        Remove-ValidatedPublishPath -Path $installer -PermittedRoot $artifacts
    }
    Move-Item -LiteralPath $stagingInstaller -Destination $installer

    if (Test-Path -LiteralPath $storeReadiness) {
        Remove-ValidatedPublishPath -Path $storeReadiness -PermittedRoot $artifacts
    }
    Move-Item -LiteralPath $stagingStoreReadiness -Destination $storeReadiness

    foreach ($nightlyArtifact in @($nightlyMetadata, $nightlyChecksum)) {
        if (Test-Path -LiteralPath $nightlyArtifact) {
            Remove-ValidatedPublishPath -Path $nightlyArtifact -PermittedRoot $artifacts
        }
    }
    if ($buildIdentity.Channel -eq 'Nightly') {
        Move-Item -LiteralPath $stagingNightlyMetadata -Destination $nightlyMetadata
        Move-Item -LiteralPath $stagingNightlyChecksum -Destination $nightlyChecksum
    }
}
finally {
    if (Test-Path -LiteralPath $stagingRoot) {
        Remove-ValidatedPublishPath -Path $stagingRoot -PermittedRoot $artifacts
    }
}

Write-Host "Self-contained production bundle created at $bundle"
Write-Host "Graphical production installer created at $installer"
Write-Host "Microsoft Store readiness report created at $storeReadiness"
Write-Host "Self-contained developer tools created at $developerTools"
if ($buildIdentity.Channel -eq 'Nightly') {
    Write-Host "Nightly build metadata created at $nightlyMetadata"
    Write-Host "Nightly installer checksum created at $nightlyChecksum"
}
