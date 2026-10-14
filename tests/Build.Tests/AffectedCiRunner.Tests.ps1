[CmdletBinding()]
param()

Set-StrictMode -Version 3.0
$ErrorActionPreference = 'Stop'
$repository = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$runner = Join-Path $repository 'build\Run-AffectedCi.ps1'

function Assert-ThrowsLike {
    param(
        [Parameter(Mandatory)][scriptblock]$Action,
        [Parameter(Mandatory)][string]$Pattern,
        [Parameter(Mandatory)][string]$Message
    )

    try {
        & $Action
    }
    catch {
        if ($_.Exception.Message -like $Pattern) { return }
        throw "$Message Unexpected error: $($_.Exception.Message)"
    }

    throw $Message
}

function Assert-Equal {
    param(
        [Parameter(Mandatory)]$Expected,
        [Parameter(Mandatory)]$Actual,
        [Parameter(Mandatory)][string]$Message
    )

    if ($Expected -ne $Actual) {
        throw "$Message Expected '$Expected', got '$Actual'."
    }
}

$tokens = $null
$parseErrors = $null
[void][Management.Automation.Language.Parser]::ParseFile(
    $runner,
    [ref]$tokens,
    [ref]$parseErrors)
if ($parseErrors.Count -ne 0) {
    throw "Affected CI runner contains a parser error: $($parseErrors[0].Message)"
}

$source = Get-Content -LiteralPath $runner -Raw
foreach ($requiredContract in @(
        "[ValidateSet('Quality', 'Contracts', 'Publish')]",
        'Get-ValidatedPlanPaths',
        "'--no-build'",
        "'--no-restore'",
        "-AllowedPrefix @('tests/Build.Tests/')",
        "'--self-contained', `$selfContained",
        'produced different Protocol assemblies')) {
    if (-not $source.Contains($requiredContract)) {
        throw "Affected CI runner is missing '$requiredContract'."
    }
}

$global:OpenBladeMockDotNetCalls = [Collections.Generic.List[object]]::new()
function global:dotnet {
    $global:OpenBladeMockDotNetCalls.Add([string[]]$args)
    $global:LASTEXITCODE = 0
}
$temporaryRoot = Join-Path ([IO.Path]::GetTempPath()) (
    'OpenBlade-AffectedCiRunner-' + [Guid]::NewGuid().ToString('N'))
$serviceProtocol = Join-Path $temporaryRoot `
    'OpenBlade.Service\OpenBlade.Protocol.dll'
$captureProtocol = Join-Path $temporaryRoot `
    'OpenBlade.Capture\OpenBlade.Protocol.dll'
try {
    foreach ($protocolFile in @($serviceProtocol, $captureProtocol)) {
        [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($protocolFile)) | Out-Null
        [IO.File]::WriteAllBytes($protocolFile, [byte[]](1, 2, 3))
    }

    $qualityPlan = [ordered]@{
        mode = 'affected'
        qualityProjects = @(
            'tests/OpenBlade.Tray.Tests/OpenBlade.Tray.Tests.csproj',
            'src/OpenBlade.Tray/OpenBlade.Tray.csproj')
        testProjects = @('tests/OpenBlade.Tray.Tests/OpenBlade.Tray.Tests.csproj')
    } | ConvertTo-Json -Compress
    & $runner -Lane Quality -PlanJson $qualityPlan
    $qualityVerbs = @($global:OpenBladeMockDotNetCalls | ForEach-Object { $_[0] })
    Assert-Equal 'restore,restore,format,format,build,build,test' `
        ($qualityVerbs -join ',') `
        'Quality must restore the complete selection before formatting or building.'
    $testCall = @($global:OpenBladeMockDotNetCalls | Where-Object { $_[0] -eq 'test' })
    Assert-Equal 1 $testCall.Count 'Quality must test only declared test projects.'
    if (($testCall[0] -join '|') -notlike '*--no-build*--no-restore*') {
        throw 'Affected tests must reuse the selected build output.'
    }

    $global:OpenBladeMockDotNetCalls.Clear()
    $contractPlan = [ordered]@{
        mode = 'affected'
        contractRestoreProjects = @(
            'src/OpenBlade.Tray/OpenBlade.Tray.csproj',
            'build/installer/OpenBlade.Installer.UI.Tests/OpenBlade.Installer.UI.Tests.csproj')
        contractTests = @()
    } | ConvertTo-Json -Compress
    & $runner -Lane Contracts -PlanJson $contractPlan
    $contractRestores = @($global:OpenBladeMockDotNetCalls | Where-Object {
            $_[0] -eq 'restore'
        })
    Assert-Equal 2 $contractRestores.Count `
        'Contracts must restore every declared renderer project.'
    $previewRestore = @($contractRestores | Where-Object {
            $_[1] -eq 'src/OpenBlade.Tray/OpenBlade.Tray.csproj'
        })[0]
    if (($previewRestore -join '|') -notlike '*OpenBladeLocalizationPreview=true*') {
        throw 'The Tray contract restore must select localization-preview assets.'
    }

    $global:OpenBladeMockDotNetCalls.Clear()
    $trayPublishPlan = [ordered]@{
        mode = 'affected'
        publishProjects = @('src/OpenBlade.Tray/OpenBlade.Tray.csproj')
    } | ConvertTo-Json -Compress
    & $runner `
        -Lane Publish `
        -PlanJson $trayPublishPlan `
        -ComponentOutputRoot $temporaryRoot
    $publishCall = @($global:OpenBladeMockDotNetCalls | Where-Object {
        $_[0] -eq 'publish'
    })
    Assert-Equal 1 $publishCall.Count 'Publish must execute once per selected component.'
    if (($publishCall[0] -join '|') -notlike '*--self-contained|false*--output*OpenBlade.Tray*') {
        throw 'Affected component publish is missing its production output arguments.'
    }

    $protocolPublishPlan = [ordered]@{
        mode = 'affected'
        publishProjects = @(
            'src/OpenBlade.Capture/OpenBlade.Capture.csproj',
            'src/OpenBlade.Service/OpenBlade.Service.csproj')
    } | ConvertTo-Json -Compress
    & $runner `
        -Lane Publish `
        -PlanJson $protocolPublishPlan `
        -ComponentOutputRoot $temporaryRoot
    $protocolCalls = @($global:OpenBladeMockDotNetCalls | Where-Object { $_[0] -eq 'publish' })
    foreach ($expectedPublish in @(
            @{ Project = 'OpenBlade.Capture'; SelfContained = 'true' },
            @{ Project = 'OpenBlade.Service'; SelfContained = 'false' })) {
        $matchingCall = @($protocolCalls | Where-Object { ($_[1] -split '/')[-1] -eq ($expectedPublish.Project + '.csproj') })
        if ($matchingCall.Count -ne 1 -or ($matchingCall[0] -join '|') -notlike "*--self-contained|$($expectedPublish.SelfContained)*") {
            throw "$($expectedPublish.Project) did not use its production runtime boundary."
        }
    }
    [IO.File]::WriteAllBytes($captureProtocol, [byte[]](3, 2, 1))
    Assert-ThrowsLike -Pattern '*different Protocol assemblies*' -Message `
        'Protocol consumers must reject inconsistent published assemblies.' -Action {
        & $runner `
            -Lane Publish `
            -PlanJson $protocolPublishPlan `
            -ComponentOutputRoot $temporaryRoot
    }
}
finally {
    Remove-Item Function:\dotnet
    Remove-Variable OpenBladeMockDotNetCalls -Scope Global
    $resolvedTemporaryRoot = [IO.Path]::GetFullPath($temporaryRoot)
    $resolvedSystemTemporaryRoot = [IO.Path]::GetFullPath(
        [IO.Path]::GetTempPath()).TrimEnd('\', '/') + [IO.Path]::DirectorySeparatorChar
    if (-not $resolvedTemporaryRoot.StartsWith(
            $resolvedSystemTemporaryRoot,
            [StringComparison]::OrdinalIgnoreCase)) {
        throw "Unexpected affected-CI test cleanup target: $resolvedTemporaryRoot"
    }
    if ([IO.Directory]::Exists($resolvedTemporaryRoot)) {
        [IO.Directory]::Delete($resolvedTemporaryRoot, $true)
    }
}

$wrongMode = [ordered]@{
    mode = 'full'
    contractRestoreProjects = @()
    contractTests = @()
} | ConvertTo-Json -Compress
Assert-ThrowsLike -Pattern "*requires mode 'affected'*" -Message `
    'The affected runner must reject a full plan.' -Action {
    & $runner -Lane Contracts -PlanJson $wrongMode
}

$unsafe = [ordered]@{
    mode = 'affected'
    contractRestoreProjects = @()
    contractTests = @('../outside.Tests.ps1')
} | ConvertTo-Json -Compress
Assert-ThrowsLike -Pattern '*unsafe path*' -Message `
    'The affected runner must reject traversal outside the repository.' -Action {
    & $runner -Lane Contracts -PlanJson $unsafe
}

$wrongLane = [ordered]@{
    mode = 'affected'
    contractRestoreProjects = @()
    contractTests = @('build/BuildIdentity.Tests.ps1')
} | ConvertTo-Json -Compress
Assert-ThrowsLike -Pattern '*outside its lane*' -Message `
    'The affected runner must reject scripts outside Build.Tests.' -Action {
    & $runner -Lane Contracts -PlanJson $wrongLane
}

if (Get-Command dotnet -CommandType Function -ErrorAction SilentlyContinue) {
    throw 'The affected runner tests must remove their global dotnet function.'
}
if (Get-Variable OpenBladeMockDotNetCalls -Scope Global -ErrorAction SilentlyContinue) {
    throw 'The affected runner tests must remove their global call collection.'
}

Write-Host 'Affected CI runner tests passed.'
