[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$repository = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
. (Join-Path $repository 'build\PublishPayloadValidation.ps1')

function Assert-True {
    param(
        [Parameter(Mandatory)][bool]$Condition,
        [Parameter(Mandatory)][string]$Message
    )

    if (-not $Condition) {
        throw $Message
    }
}

function Get-RealHostingAssemblies {
    $repositoryCandidates = @(
        (Join-Path $repository 'artifacts\win-x64\OpenBlade\payload'),
        (Join-Path $repository 'src\OpenBlade.Service\bin')
    )
    foreach ($candidateRoot in $repositoryCandidates) {
        if (-not (Test-Path -LiteralPath $candidateRoot)) {
            continue
        }
        $hostingCandidate = Get-ChildItem -LiteralPath $candidateRoot -Recurse `
            -Filter 'Microsoft.Extensions.Hosting.dll' -File -ErrorAction SilentlyContinue | Select-Object -First 1
        if ($null -eq $hostingCandidate) {
            continue
        }
        $windowsServices = Join-Path $hostingCandidate.Directory.FullName 'Microsoft.Extensions.Hosting.WindowsServices.dll'
        if (Test-Path -LiteralPath $windowsServices -PathType Leaf) {
            return [ordered]@{
                'Microsoft.Extensions.Hosting.dll' = $hostingCandidate.FullName
                'Microsoft.Extensions.Hosting.WindowsServices.dll' = $windowsServices
            }
        }
    }

    $packageRoot = if ([string]::IsNullOrWhiteSpace($env:NUGET_PACKAGES)) {
        Join-Path ([Environment]::GetFolderPath([Environment+SpecialFolder]::UserProfile)) '.nuget\packages'
    }
    else {
        $env:NUGET_PACKAGES
    }
    $hostingPackages = Join-Path $packageRoot 'microsoft.extensions.hosting'
    $windowsServicePackages = Join-Path $packageRoot 'microsoft.extensions.hosting.windowsservices'
    $hostingVersions = if (Test-Path -LiteralPath $hostingPackages) {
        @(Get-ChildItem -LiteralPath $hostingPackages -Directory | Sort-Object { [version]$_.Name } -Descending)
    }
    else { @() }
    foreach ($hostingVersion in $hostingVersions) {
        $windowsServiceVersion = Join-Path $windowsServicePackages $hostingVersion.Name
        $hosting = Join-Path $hostingVersion.FullName 'lib\net10.0\Microsoft.Extensions.Hosting.dll'
        $windowsServices = Join-Path $windowsServiceVersion 'lib\net10.0\Microsoft.Extensions.Hosting.WindowsServices.dll'
        if ((Test-Path -LiteralPath $hosting -PathType Leaf) -and
            (Test-Path -LiteralPath $windowsServices -PathType Leaf)) {
            return [ordered]@{
                'Microsoft.Extensions.Hosting.dll' = $hosting
                'Microsoft.Extensions.Hosting.WindowsServices.dll' = $windowsServices
            }
        }
    }

    $dotnet = Get-Command dotnet -ErrorAction Stop
    $sharedFrameworkRoot = Join-Path (Split-Path -Parent $dotnet.Source) 'shared\Microsoft.AspNetCore.App'
    $frameworkDirectories = @(Get-ChildItem -LiteralPath $sharedFrameworkRoot -Directory -ErrorAction Stop |
        Sort-Object { [version]($_.Name.Split('-')[0]) } -Descending)

    foreach ($frameworkDirectory in $frameworkDirectories) {
        $hosting = Join-Path $frameworkDirectory.FullName 'Microsoft.Extensions.Hosting.dll'
        $windowsServices = Join-Path $frameworkDirectory.FullName 'Microsoft.Extensions.Hosting.WindowsServices.dll'
        if ((Test-Path -LiteralPath $hosting -PathType Leaf) -and
            (Test-Path -LiteralPath $windowsServices -PathType Leaf)) {
            return [ordered]@{
                'Microsoft.Extensions.Hosting.dll' = $hosting
                'Microsoft.Extensions.Hosting.WindowsServices.dll' = $windowsServices
            }
        }
    }

    throw "Could not locate real hosting assemblies in build output, $packageRoot, or $sharedFrameworkRoot."
}

function New-ValidServiceManifest {
    $targetName = '.NETCoreApp,Version=v10.0/win-x64'
    return [ordered]@{
        runtimeTarget = [ordered]@{ name = $targetName }
        targets = [ordered]@{
            $targetName = [ordered]@{
                'OpenBlade.Service/1.0.0' = [ordered]@{
                    dependencies = [ordered]@{
                        'Microsoft.Extensions.Hosting' = '10.0.9'
                        'Microsoft.Extensions.Hosting.WindowsServices' = '10.0.9'
                    }
                    runtime = [ordered]@{
                        'OpenBlade.Service.dll' = [ordered]@{}
                    }
                }
                'Microsoft.Extensions.Hosting/10.0.9' = [ordered]@{
                    runtime = [ordered]@{
                        'lib/net10.0/Microsoft.Extensions.Hosting.dll' = [ordered]@{}
                    }
                }
                'Microsoft.Extensions.Hosting.WindowsServices/10.0.9' = [ordered]@{
                    dependencies = [ordered]@{
                        'Microsoft.Extensions.Hosting' = '10.0.9'
                    }
                    runtime = [ordered]@{
                        'lib/net10.0/Microsoft.Extensions.Hosting.WindowsServices.dll' = [ordered]@{}
                    }
                }
            }
        }
        libraries = [ordered]@{
            'Microsoft.Extensions.Hosting/10.0.9' = [ordered]@{ type = 'package' }
            'Microsoft.Extensions.Hosting.WindowsServices/10.0.9' = [ordered]@{ type = 'package' }
        }
    }
}

function Write-ServiceManifest {
    param(
        [Parameter(Mandatory)][string]$Payload,
        [Parameter(Mandatory)]$Manifest
    )

    $Manifest | ConvertTo-Json -Depth 10 |
        Set-Content -LiteralPath (Join-Path $Payload 'OpenBlade.Service.deps.json') -Encoding UTF8
}

function Write-ValidServicePayload {
    param(
        [Parameter(Mandatory)][string]$Payload,
        [Parameter(Mandatory)][System.Collections.IDictionary]$AssemblySources
    )

    New-Item -ItemType Directory -Path $Payload -Force | Out-Null
    Write-ServiceManifest -Payload $Payload -Manifest (New-ValidServiceManifest)
    foreach ($sessionAgentFile in @(
            'OpenBlade.Platform.dll',
            'OpenBlade.SessionAgent.exe',
            'OpenBlade.SessionAgent.dll',
            'OpenBlade.SessionAgent.deps.json',
            'OpenBlade.SessionAgent.runtimeconfig.json',
            'Microsoft.WindowsAppRuntime.Insights.Resource.dll')) {
        Set-Content -LiteralPath (Join-Path $Payload $sessionAgentFile) `
            -Value 'session agent payload' -NoNewline
    }
    foreach ($trayUiFile in @(
            'OpenBlade.Tray.pri',
            'App.xbf',
            'MainWindow.xbf')) {
        Set-Content -LiteralPath (Join-Path $Payload $trayUiFile) `
            -Value 'tray UI payload' -NoNewline
    }
    foreach ($assembly in $AssemblySources.GetEnumerator()) {
        Copy-Item -LiteralPath $assembly.Value -Destination (Join-Path $Payload $assembly.Key)
    }
}

function Assert-RejectedAndPreserved {
    param(
        [Parameter(Mandatory)][string]$PermittedRoot,
        [Parameter(Mandatory)][string]$StagingBundle,
        [Parameter(Mandatory)][string]$ExistingBundle,
        [Parameter(Mandatory)][string]$ExpectedError,
        [Parameter(Mandatory)][string]$CaseName
    )

    New-Item -ItemType Directory -Path $ExistingBundle -Force | Out-Null
    $sentinel = Join-Path $ExistingBundle 'sentinel.txt'
    Set-Content -LiteralPath $sentinel -Value 'previous bundle' -NoNewline

    $caught = $null
    try {
        Complete-ValidatedBundleReplacement `
            -PermittedRoot $PermittedRoot `
            -StagingBundle $StagingBundle `
            -Bundle $ExistingBundle
    }
    catch {
        $caught = $_
    }

    Assert-True ($null -ne $caught) "$CaseName was accepted."
    Assert-True ($caught.Exception.Message -match $ExpectedError) "$CaseName failed for an unexpected reason: $($caught.Exception.Message)"
    Assert-True (Test-Path -LiteralPath $sentinel -PathType Leaf) "$CaseName removed the prior bundle."
    Assert-True ((Get-Content -Raw -LiteralPath $sentinel) -eq 'previous bundle') "$CaseName changed the prior bundle."
}

function New-TestBundlePair {
    param(
        [Parameter(Mandatory)][string]$PermittedRoot,
        [Parameter(Mandatory)][string]$CaseName,
        [Parameter(Mandatory)][System.Collections.IDictionary]$AssemblySources
    )

    $caseRoot = Join-Path $PermittedRoot $CaseName
    $stagingBundle = Join-Path $caseRoot 'staging\OpenBlade'
    $existingBundle = Join-Path $caseRoot 'existing\OpenBlade'
    Write-ValidServicePayload -Payload (Join-Path $stagingBundle 'payload') -AssemblySources $AssemblySources
    return [pscustomobject]@{
        StagingBundle = $stagingBundle
        ExistingBundle = $existingBundle
        Payload = Join-Path $stagingBundle 'payload'
    }
}

$testRoot = Join-Path ([IO.Path]::GetTempPath()) ('OpenBlade.PublishValidation.Tests.' + [Guid]::NewGuid().ToString('N'))

try {
    New-Item -ItemType Directory -Path $testRoot -Force | Out-Null
    $permittedRoot = Join-Path $testRoot 'artifacts'
    New-Item -ItemType Directory -Path $permittedRoot -Force | Out-Null
    $assemblySources = Get-RealHostingAssemblies

    $matchingPayload = Join-Path $testRoot 'matching-payload'
    $matchingTools = Join-Path $testRoot 'matching-tools'
    New-Item -ItemType Directory -Path $matchingPayload, $matchingTools -Force | Out-Null
    [IO.File]::WriteAllBytes(
        (Join-Path $matchingPayload 'OpenBlade.Protocol.dll'),
        [byte[]]@(1, 2, 3))
    [IO.File]::WriteAllBytes(
        (Join-Path $matchingTools 'OpenBlade.Protocol.dll'),
        [byte[]]@(1, 2, 3))
    Assert-LightingValidationProtocolPair `
        -ServicePayload $matchingPayload `
        -DeveloperTools $matchingTools

    [IO.File]::WriteAllBytes(
        (Join-Path $matchingTools 'OpenBlade.Protocol.dll'),
        [byte[]]@(3, 2, 1))
    $mismatchRejected = $false
    try {
        Assert-LightingValidationProtocolPair `
            -ServicePayload $matchingPayload `
            -DeveloperTools $matchingTools
    }
    catch {
        $mismatchRejected = $_.Exception.Message -match 'different Protocol binaries'
    }
    Assert-True $mismatchRejected `
        'Publish validation accepted a developer-tool Protocol binary that cannot admit the service payload.'

    $outsideStaging = New-TestBundlePair -PermittedRoot $testRoot -CaseName 'outside-staging' -AssemblySources $assemblySources
    $insideDestination = Join-Path $permittedRoot 'outside-staging-destination\OpenBlade'
    Assert-RejectedAndPreserved -PermittedRoot $permittedRoot -StagingBundle $outsideStaging.StagingBundle `
        -ExistingBundle $insideDestination -ExpectedError 'outside the permitted root' -CaseName 'An out-of-root staging bundle'

    $insideStaging = New-TestBundlePair -PermittedRoot $permittedRoot -CaseName 'outside-destination' -AssemblySources $assemblySources
    $outsideDestination = Join-Path $testRoot 'outside-destination\OpenBlade'
    Assert-RejectedAndPreserved -PermittedRoot $permittedRoot -StagingBundle $insideStaging.StagingBundle `
        -ExistingBundle $outsideDestination -ExpectedError 'outside the permitted root' -CaseName 'An out-of-root destination bundle'

    $externalStagingTarget = Join-Path $testRoot 'junction-staging-target'
    $externalStagingBundle = Join-Path $externalStagingTarget 'OpenBlade'
    Write-ValidServicePayload -Payload (Join-Path $externalStagingBundle 'payload') -AssemblySources $assemblySources
    Set-Content -LiteralPath (Join-Path $externalStagingBundle 'external-sentinel.txt') -Value 'external staging' -NoNewline
    $stagingJunction = Join-Path $permittedRoot 'staging-junction'
    $null = New-Item -ItemType Junction -Path $stagingJunction -Target $externalStagingTarget
    $junctionStagingDestination = Join-Path $permittedRoot 'junction-staging-destination\OpenBlade'
    Assert-RejectedAndPreserved -PermittedRoot $permittedRoot `
        -StagingBundle (Join-Path $stagingJunction 'OpenBlade') `
        -ExistingBundle $junctionStagingDestination `
        -ExpectedError 'through reparse point' `
        -CaseName 'A staging bundle traversing a junction'
    Assert-True (Test-Path -LiteralPath (Join-Path $externalStagingBundle 'external-sentinel.txt')) `
        'A rejected staging junction changed its external target.'

    $externalDestinationTarget = Join-Path $testRoot 'junction-destination-target'
    New-Item -ItemType Directory -Path $externalDestinationTarget -Force | Out-Null
    $destinationJunction = Join-Path $permittedRoot 'destination-junction'
    $null = New-Item -ItemType Junction -Path $destinationJunction -Target $externalDestinationTarget
    $junctionDestinationStaging = New-TestBundlePair `
        -PermittedRoot $permittedRoot `
        -CaseName 'junction-destination-staging' `
        -AssemblySources $assemblySources
    $junctionDestinationBundle = Join-Path $destinationJunction 'OpenBlade'
    Assert-RejectedAndPreserved -PermittedRoot $permittedRoot `
        -StagingBundle $junctionDestinationStaging.StagingBundle `
        -ExistingBundle $junctionDestinationBundle `
        -ExpectedError 'through reparse point' `
        -CaseName 'A destination bundle traversing a junction'
    Assert-True (Test-Path -LiteralPath (Join-Path $externalDestinationTarget 'OpenBlade\sentinel.txt')) `
        'A rejected destination junction changed its external target.'

    $externalCleanupTarget = Join-Path $testRoot 'junction-cleanup-target'
    New-Item -ItemType Directory -Path $externalCleanupTarget -Force | Out-Null
    $externalCleanupSentinel = Join-Path $externalCleanupTarget 'sentinel.txt'
    Set-Content -LiteralPath $externalCleanupSentinel -Value 'external cleanup' -NoNewline
    $cleanupJunction = Join-Path $permittedRoot 'cleanup-junction'
    $null = New-Item -ItemType Junction -Path $cleanupJunction -Target $externalCleanupTarget
    $cleanupRejected = $false
    try {
        Remove-ValidatedPublishPath -Path $cleanupJunction -PermittedRoot $permittedRoot
    }
    catch {
        $cleanupRejected = $_.Exception.Message -match 'through reparse point'
    }
    Assert-True $cleanupRejected 'Cleanup accepted a path traversing a junction.'
    Assert-True (Test-Path -LiteralPath $externalCleanupSentinel) 'Rejected cleanup removed external data.'

    $invalidAssemblyCases = @(
        [pscustomobject]@{
            Name = 'empty-assembly'
            ExpectedError = 'not a loadable managed assembly'
            Mutate = {
                param($pair)
                [IO.File]::WriteAllBytes((Join-Path $pair.Payload 'Microsoft.Extensions.Hosting.dll'), [byte[]]@())
            }
        },
        [pscustomobject]@{
            Name = 'corrupt-assembly'
            ExpectedError = 'not a loadable managed assembly'
            Mutate = {
                param($pair)
                [IO.File]::WriteAllText((Join-Path $pair.Payload 'Microsoft.Extensions.Hosting.dll'), 'not an assembly')
            }
        },
        [pscustomobject]@{
            Name = 'wrong-assembly-name'
            ExpectedError = 'has managed assembly name'
            Mutate = {
                param($pair)
                Copy-Item -LiteralPath (Join-Path $pair.Payload 'Microsoft.Extensions.Hosting.dll') `
                    -Destination (Join-Path $pair.Payload 'Microsoft.Extensions.Hosting.WindowsServices.dll') -Force
            }
        }
    )

    foreach ($case in $invalidAssemblyCases) {
        $pair = New-TestBundlePair -PermittedRoot $permittedRoot -CaseName $case.Name -AssemblySources $assemblySources
        & $case.Mutate $pair
        Assert-RejectedAndPreserved -PermittedRoot $permittedRoot -StagingBundle $pair.StagingBundle `
            -ExistingBundle $pair.ExistingBundle -ExpectedError $case.ExpectedError -CaseName $case.Name
    }

    $malformed = New-TestBundlePair -PermittedRoot $permittedRoot -CaseName 'malformed-json' -AssemblySources $assemblySources
    Set-Content -LiteralPath (Join-Path $malformed.Payload 'OpenBlade.Service.deps.json') -Value '{' -NoNewline
    Assert-RejectedAndPreserved -PermittedRoot $permittedRoot -StagingBundle $malformed.StagingBundle `
        -ExistingBundle $malformed.ExistingBundle -ExpectedError 'manifest is invalid' -CaseName 'Malformed dependency JSON'

    $manifestCases = @(
        [pscustomobject]@{
            Name = 'missing-library'
            ExpectedError = 'exactly one library entry'
            Mutate = {
                param($manifest)
                $manifest.libraries.Remove('Microsoft.Extensions.Hosting/10.0.9')
            }
        },
        [pscustomobject]@{
            Name = 'missing-target-package'
            ExpectedError = 'exactly one runtime target entry'
            Mutate = {
                param($manifest)
                $target = $manifest.targets[$manifest.runtimeTarget.name]
                $target.Remove('Microsoft.Extensions.Hosting/10.0.9')
            }
        },
        [pscustomobject]@{
            Name = 'missing-runtime-entry'
            ExpectedError = 'missing runtime entry'
            Mutate = {
                param($manifest)
                $target = $manifest.targets[$manifest.runtimeTarget.name]
                $target['Microsoft.Extensions.Hosting/10.0.9'].Remove('runtime')
            }
        },
        [pscustomobject]@{
            Name = 'missing-runtime-asset'
            ExpectedError = 'exactly one runtime asset'
            Mutate = {
                param($manifest)
                $target = $manifest.targets[$manifest.runtimeTarget.name]
                $target['Microsoft.Extensions.Hosting/10.0.9'].runtime = [ordered]@{}
            }
        },
        [pscustomobject]@{
            Name = 'library-version-mismatch'
            ExpectedError = 'incoherent versions'
            Mutate = {
                param($manifest)
                $manifest.libraries['Microsoft.Extensions.Hosting/10.0.8'] = $manifest.libraries['Microsoft.Extensions.Hosting/10.0.9']
                $manifest.libraries.Remove('Microsoft.Extensions.Hosting/10.0.9')
            }
        },
        [pscustomobject]@{
            Name = 'runtime-target-mismatch'
            ExpectedError = 'unexpected runtime target'
            Mutate = {
                param($manifest)
                $manifest.runtimeTarget.name = '.NETCoreApp,Version=v10.0/win-arm64'
            }
        },
        [pscustomobject]@{
            Name = 'package-type-mismatch'
            ExpectedError = 'is not a package'
            Mutate = {
                param($manifest)
                $manifest.libraries['Microsoft.Extensions.Hosting/10.0.9'].type = 'project'
            }
        },
        [pscustomobject]@{
            Name = 'hosting-dependency-version-mismatch'
            ExpectedError = 'Hosting dependency version does not match'
            Mutate = {
                param($manifest)
                $target = $manifest.targets[$manifest.runtimeTarget.name]
                $target['Microsoft.Extensions.Hosting.WindowsServices/10.0.9'].dependencies['Microsoft.Extensions.Hosting'] = '10.0.8'
            }
        }
    )

    foreach ($case in $manifestCases) {
        $pair = New-TestBundlePair -PermittedRoot $permittedRoot -CaseName $case.Name -AssemblySources $assemblySources
        $manifest = New-ValidServiceManifest
        & $case.Mutate $manifest
        Write-ServiceManifest -Payload $pair.Payload -Manifest $manifest
        Assert-RejectedAndPreserved -PermittedRoot $permittedRoot -StagingBundle $pair.StagingBundle `
            -ExistingBundle $pair.ExistingBundle -ExpectedError $case.ExpectedError -CaseName $case.Name
    }

    $missingDll = New-TestBundlePair -PermittedRoot $permittedRoot -CaseName 'missing-dll' -AssemblySources $assemblySources
    Remove-Item -LiteralPath (Join-Path $missingDll.Payload 'Microsoft.Extensions.Hosting.dll')
    Assert-RejectedAndPreserved -PermittedRoot $permittedRoot -StagingBundle $missingDll.StagingBundle `
        -ExistingBundle $missingDll.ExistingBundle -ExpectedError 'missing required assembly' -CaseName 'A missing required assembly'

    $missingSessionAgent = New-TestBundlePair `
        -PermittedRoot $permittedRoot `
        -CaseName 'missing-session-agent' `
        -AssemblySources $assemblySources
    Remove-Item -LiteralPath (Join-Path $missingSessionAgent.Payload 'OpenBlade.SessionAgent.exe')
    Assert-RejectedAndPreserved `
        -PermittedRoot $permittedRoot `
        -StagingBundle $missingSessionAgent.StagingBundle `
        -ExistingBundle $missingSessionAgent.ExistingBundle `
        -ExpectedError 'missing required session agent file' `
        -CaseName 'A missing required session agent executable'

    $staleInputAgent = New-TestBundlePair `
        -PermittedRoot $permittedRoot `
        -CaseName 'stale-input-agent' `
        -AssemblySources $assemblySources
    Set-Content -LiteralPath (Join-Path $staleInputAgent.Payload 'OpenBlade.InputAgent.exe') `
        -Value 'stale input agent' -NoNewline
    Assert-RejectedAndPreserved `
        -PermittedRoot $permittedRoot `
        -StagingBundle $staleInputAgent.StagingBundle `
        -ExistingBundle $staleInputAgent.ExistingBundle `
        -ExpectedError 'abandoned UIAccess input agent' `
        -CaseName 'A stale UIAccess input agent'

    $missingTrayUi = New-TestBundlePair `
        -PermittedRoot $permittedRoot `
        -CaseName 'missing-tray-ui' `
        -AssemblySources $assemblySources
    Remove-Item -LiteralPath (Join-Path $missingTrayUi.Payload 'App.xbf')
    Assert-RejectedAndPreserved `
        -PermittedRoot $permittedRoot `
        -StagingBundle $missingTrayUi.StagingBundle `
        -ExistingBundle $missingTrayUi.ExistingBundle `
        -ExpectedError 'missing required tray UI file' `
        -CaseName 'A missing required compiled XAML file'

    $captureTool = New-TestBundlePair -PermittedRoot $permittedRoot -CaseName 'capture-tool' -AssemblySources $assemblySources
    $captureToolDirectory = Join-Path $captureTool.Payload 'tools'
    New-Item -ItemType Directory -Path $captureToolDirectory -Force | Out-Null
    Set-Content -LiteralPath (Join-Path $captureToolDirectory 'openblade.capture-helper.exe') -Value 'developer tool' -NoNewline
    Assert-RejectedAndPreserved -PermittedRoot $permittedRoot -StagingBundle $captureTool.StagingBundle `
        -ExistingBundle $captureTool.ExistingBundle -ExpectedError 'must not include developer tool' `
        -CaseName 'A nested case-variant Capture developer tool'

    $footprint = Join-Path $testRoot 'localized-footprint\OpenBlade'
    $footprintPayload = Join-Path $footprint 'payload'
    New-Item -ItemType Directory -Path $footprintPayload -Force | Out-Null
    Set-Content -LiteralPath (Join-Path $footprint 'OpenBlade.Setup.exe') `
        -Value 'single-file setup' -NoNewline
    Set-Content -LiteralPath (
        Join-Path $footprintPayload 'Microsoft.Windows.ApplicationModel.Resources.dll') `
        -Value 'runtime resource loader' -NoNewline
    foreach ($culture in @('zh-CN', 'ru-RU')) {
        $cultureDirectory = Join-Path $footprintPayload $culture
        New-Item -ItemType Directory -Path $cultureDirectory -Force | Out-Null
        Set-Content -LiteralPath (
            Join-Path $cultureDirectory 'OpenBlade.Tray.Core.resources.dll') `
            -Value "$culture resources" -NoNewline
    }
    Assert-ProductionBundleFootprint -Bundle $footprint

    Remove-Item -LiteralPath (
        Join-Path $footprintPayload 'ru-RU\OpenBlade.Tray.Core.resources.dll')
    $missingSatelliteRejected = $false
    try {
        Assert-ProductionBundleFootprint -Bundle $footprint
    }
    catch {
        $missingSatelliteRejected = $_.Exception.Message -match (
            'missing required localization satellite')
    }
    Assert-True $missingSatelliteRejected `
        'Production footprint validation accepted a missing Russian satellite.'

    Set-Content -LiteralPath (
        Join-Path $footprintPayload 'ru-RU\OpenBlade.Tray.Core.resources.dll') `
        -Value 'ru-RU resources' -NoNewline
    $unexpectedCultureDirectory = Join-Path $footprintPayload 'de-DE'
    New-Item -ItemType Directory -Path $unexpectedCultureDirectory -Force | Out-Null
    Set-Content -LiteralPath (
        Join-Path $unexpectedCultureDirectory 'OpenBlade.Tray.Core.resources.dll') `
        -Value 'unexpected resources' -NoNewline
    $unexpectedSatelliteRejected = $false
    try {
        Assert-ProductionBundleFootprint -Bundle $footprint
    }
    catch {
        $unexpectedSatelliteRejected = $_.Exception.Message -match (
            'development or unused component file')
    }
    Assert-True $unexpectedSatelliteRejected `
        'Production footprint validation accepted an unsupported satellite culture.'

    $valid = New-TestBundlePair -PermittedRoot $permittedRoot -CaseName 'valid' -AssemblySources $assemblySources
    New-Item -ItemType Directory -Path $valid.ExistingBundle -Force | Out-Null
    Set-Content -LiteralPath (Join-Path $valid.ExistingBundle 'sentinel.txt') -Value 'previous bundle' -NoNewline
    Set-Content -LiteralPath (Join-Path $valid.StagingBundle 'replacement.txt') -Value 'validated bundle' -NoNewline

    Complete-ValidatedBundleReplacement -PermittedRoot $permittedRoot `
        -StagingBundle $valid.StagingBundle -Bundle $valid.ExistingBundle

    Assert-True (Test-Path -LiteralPath (Join-Path $valid.ExistingBundle 'replacement.txt') -PathType Leaf) 'A valid staged bundle was not installed.'
    Assert-True (-not (Test-Path -LiteralPath (Join-Path $valid.ExistingBundle 'sentinel.txt'))) 'The prior bundle was not replaced after validation succeeded.'

    Write-Host 'Publish payload validation tests passed.'
}
finally {
    if (Test-Path -LiteralPath $testRoot) {
        Remove-Item -LiteralPath $testRoot -Recurse -Force
    }
}
