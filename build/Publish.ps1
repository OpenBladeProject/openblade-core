[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Release'
)

$ErrorActionPreference = 'Stop'
$repository = Split-Path -Parent $PSScriptRoot
$artifacts = [IO.Path]::GetFullPath((Join-Path $repository 'artifacts'))
. (Join-Path $PSScriptRoot 'PublishPayloadValidation.ps1')
. (Join-Path $PSScriptRoot 'InstallerValidation.ps1')
. (Join-Path $PSScriptRoot 'StoreSubmissionValidation.ps1')
. (Join-Path $PSScriptRoot 'WindowsAppRuntimeNotificationResource.ps1')

& (Join-Path $PSScriptRoot 'Verify-Branding.ps1') -Repository $repository
if ($LASTEXITCODE -ne 0) {
    throw "OpenBlade branding verification failed with exit code $LASTEXITCODE"
}

$bundle = [IO.Path]::GetFullPath((Join-Path $artifacts 'win-x64\OpenBlade'))
$installer = [IO.Path]::GetFullPath((Join-Path $artifacts 'win-x64\OpenBlade-Setup.exe'))
$storeReadiness = [IO.Path]::GetFullPath((
    Join-Path $artifacts 'win-x64\OpenBlade-Store-Readiness.json'))
$developerTools = [IO.Path]::GetFullPath((Join-Path $artifacts 'win-x64\OpenBlade.DeveloperTools'))
$stagingRoot = [IO.Path]::GetFullPath((Join-Path $artifacts ('.staging\' + [Guid]::NewGuid().ToString('N'))))
$stagingBundle = Join-Path $stagingRoot 'OpenBlade'
$stagingDeveloperTools = Join-Path $stagingRoot 'OpenBlade.DeveloperTools'
$stagingInstallerOutput = Join-Path $stagingRoot 'installer'
$stagingInstaller = Join-Path $stagingInstallerOutput 'OpenBlade-Setup.exe'
$stagingStoreReadiness = Join-Path $stagingRoot 'OpenBlade-Store-Readiness.json'
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
Assert-SafeArtifactPath -Path $developerTools
Assert-SafeArtifactPath -Path $stagingRoot

[xml]$buildProperties = Get-Content -LiteralPath (Join-Path $repository 'Directory.Build.props')
$openBladeVersionNode = $buildProperties.SelectSingleNode(
    '/Project/PropertyGroup/OpenBladeVersion')
if ($null -eq $openBladeVersionNode -or
    [string]::IsNullOrWhiteSpace($openBladeVersionNode.InnerText)) {
    throw 'Directory.Build.props does not define OpenBladeVersion.'
}
$openBladeVersion = $openBladeVersionNode.InnerText

$publishArguments = @(
    '--configuration', $Configuration,
    '--runtime', 'win-x64',
    '--self-contained', 'true',
    '--property:PublishTrimmed=false'
)
$productionPublishArguments = @(
    '--property:DebugType=None',
    '--property:DebugSymbols=false',
    # OpenBlade currently ships English UI strings. Keep native WinUI MUI
    # resources, but do not deploy unused managed satellite assemblies.
    '--property:SatelliteResourceLanguages=en-US'
)
$setupPublishArguments = @(
    '--property:PublishSingleFile=true',
    '--property:IncludeNativeLibrariesForSelfExtract=true',
    '--property:EnableCompressionInSingleFile=true'
)

try {
    New-Item -ItemType Directory -Path $payload -Force | Out-Null

    Invoke-CheckedPublish (Join-Path $repository 'src\OpenBlade.Service\OpenBlade.Service.csproj') `
        $payload $productionPublishArguments
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

    $captureExecutable = Join-Path $stagingDeveloperTools 'OpenBlade.Capture.exe'
    if (-not (Test-Path -LiteralPath $captureExecutable -PathType Leaf)) {
        throw "OpenBlade developer-tools artifact is missing: $captureExecutable"
    }

    Assert-LightingValidationProtocolPair `
        -ServicePayload $payload `
        -DeveloperTools $stagingDeveloperTools

    Assert-ProductionBundleFootprint -Bundle $stagingBundle

    New-Item -ItemType Directory -Path $stagingInstallerOutput -Force | Out-Null
    & dotnet build (Join-Path $repository 'build\installer\OpenBlade.Installer.wixproj') `
        --configuration $Configuration `
        --property:NuGetAudit=false `
        --property:OpenBladeBundle=$stagingBundle `
        --output $stagingInstallerOutput
    if ($LASTEXITCODE -ne 0) {
        throw "OpenBlade installer build failed with exit code $LASTEXITCODE"
    }

    Assert-OpenBladeInstallerBundle `
        -Installer $stagingInstaller `
        -PublishedBundle $stagingBundle `
        -InstallerProject (Join-Path $repository 'build\installer\OpenBlade.Installer.wixproj') `
        -ExpectedVersion $openBladeVersion `
        -ValidationRoot (Join-Path $stagingRoot 'installer-validation') `
        -PermittedRoot $artifacts

    Write-OpenBladeStoreReadinessReport `
        -Bundle $stagingBundle `
        -Installer $stagingInstaller `
        -ReportPath $stagingStoreReadiness | Out-Null

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
