[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [ValidateSet('Quality', 'Contracts', 'Publish')]
    [string]$Lane,

    [Parameter(Mandatory)]
    [string]$PlanJson,

    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Debug',

    [string]$ComponentOutputRoot
)

Set-StrictMode -Version 3.0
$ErrorActionPreference = 'Stop'
$repository = Split-Path -Parent $PSScriptRoot

function Get-ValidatedPlanPaths {
    param(
        [Parameter(Mandatory)]$Plan,
        [Parameter(Mandatory)][string]$Property,
        [Parameter(Mandatory)][string[]]$AllowedExtension,
        [Parameter(Mandatory)][string[]]$AllowedPrefix
    )

    if (-not ($Plan.PSObject.Properties.Name -contains $Property)) {
        throw "The affected CI plan is missing '$Property'."
    }

    $paths = @($Plan.$Property)
    foreach ($pathValue in $paths) {
        $path = [string]$pathValue
        if ([string]::IsNullOrWhiteSpace($path) -or
            [IO.Path]::IsPathRooted($path) -or
            @($path.Replace('\', '/').Split('/')) -contains '..') {
            throw "The affected CI plan contains an unsafe path: '$path'."
        }
        if (-not ($AllowedExtension | Where-Object {
                    $path.EndsWith($_, [StringComparison]::OrdinalIgnoreCase)
                })) {
            throw "The affected CI plan path has an unsupported extension: '$path'."
        }
        if (-not ($AllowedPrefix | Where-Object {
                    $path.Replace('\', '/').StartsWith(
                        $_,
                        [StringComparison]::OrdinalIgnoreCase)
                })) {
            throw "The affected CI plan path is outside its lane: '$path'."
        }

        $resolved = Join-Path $repository $path
        if (-not (Test-Path -LiteralPath $resolved -PathType Leaf)) {
            throw "The affected CI plan path does not exist: '$path'."
        }
    }

    return @($paths | ForEach-Object { [string]$_ } | Sort-Object -Unique)
}

function Invoke-DotNetChecked {
    param(
        [Parameter(Mandatory)][string]$Operation,
        [Parameter(Mandatory)][string[]]$Arguments
    )

    & dotnet @Arguments
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet $Operation failed with exit code $LASTEXITCODE."
    }
}

try {
    $plan = $PlanJson | ConvertFrom-Json
}
catch {
    throw "The affected CI plan is not valid JSON: $($_.Exception.Message)"
}
if ($plan.mode -ne 'affected') {
    throw "The affected CI runner requires mode 'affected', received '$($plan.mode)'."
}

switch ($Lane) {
    'Quality' {
        $qualityProjects = Get-ValidatedPlanPaths `
            -Plan $plan `
            -Property 'qualityProjects' `
            -AllowedExtension @('.csproj') `
            -AllowedPrefix @('src/', 'tests/', 'build/installer/')
        $testProjects = Get-ValidatedPlanPaths `
            -Plan $plan `
            -Property 'testProjects' `
            -AllowedExtension @('.csproj') `
            -AllowedPrefix @('tests/', 'build/installer/')

        # Restore the complete selected set before any custom build target can
        # consume another selected project's assets.
        foreach ($project in $qualityProjects) {
            Invoke-DotNetChecked -Operation "restore $project" -Arguments @(
                'restore', $project, '-p:NuGetAudit=false')
        }
        foreach ($project in $qualityProjects) {
            Invoke-DotNetChecked -Operation "format $project" -Arguments @(
                'format', $project, '--verify-no-changes', '--no-restore',
                '--severity', 'error', '--verbosity', 'minimal')
        }
        foreach ($project in $qualityProjects) {
            Invoke-DotNetChecked -Operation "build $project" -Arguments @(
                'build', $project, '--configuration', $Configuration,
                '-p:NuGetAudit=false', '--no-restore', '-m:1', '--verbosity', 'minimal')
        }

        foreach ($project in $testProjects) {
            Invoke-DotNetChecked -Operation "test $project" -Arguments @(
                'test', $project, '--configuration', $Configuration,
                '-p:NuGetAudit=false', '--no-build', '--no-restore',
                '-m:1', '--verbosity', 'minimal')
        }
    }

    'Contracts' {
        $restoreProjects = Get-ValidatedPlanPaths `
            -Plan $plan `
            -Property 'contractRestoreProjects' `
            -AllowedExtension @('.csproj') `
            -AllowedPrefix @('src/', 'tests/', 'build/installer/')
        $contractTests = Get-ValidatedPlanPaths `
            -Plan $plan `
            -Property 'contractTests' `
            -AllowedExtension @('.Tests.ps1') `
            -AllowedPrefix @('tests/Build.Tests/')

        foreach ($project in $restoreProjects) {
            Invoke-DotNetChecked -Operation "restore $project" -Arguments @(
                'restore', $project, '-p:NuGetAudit=false')
        }
        foreach ($test in $contractTests) {
            $global:LASTEXITCODE = 0
            & (Join-Path $repository $test)
            if ($LASTEXITCODE -ne 0) {
                throw "Build-contract suite failed with exit code ${LASTEXITCODE}: $test"
            }
        }
    }

    'Publish' {
        $publishProjects = Get-ValidatedPlanPaths `
            -Plan $plan `
            -Property 'publishProjects' `
            -AllowedExtension @('.csproj') `
            -AllowedPrefix @('src/')
        $outputRoot = if ([string]::IsNullOrWhiteSpace($ComponentOutputRoot)) {
            Join-Path $repository 'artifacts\ci-components'
        }
        else {
            [IO.Path]::GetFullPath($ComponentOutputRoot)
        }

        foreach ($project in $publishProjects) {
            $projectName = [IO.Path]::GetFileNameWithoutExtension($project)
            $output = Join-Path $outputRoot $projectName
            Invoke-DotNetChecked -Operation "restore $project" -Arguments @(
                'restore', $project, '--runtime', 'win-x64', '-p:NuGetAudit=false')
            Invoke-DotNetChecked -Operation "publish $project" -Arguments @(
                'publish', $project, '--configuration', 'Release',
                '--runtime', 'win-x64', '--self-contained', 'true',
                '--property:PublishTrimmed=false', '--property:DebugType=None',
                '--property:DebugSymbols=false',
                '--property:SatelliteResourceLanguages=en-US%3Bzh-CN%3Bru-RU',
                '--property:NuGetAudit=false', '--no-restore',
                '--output', $output)
        }

        $serviceProject = 'src/OpenBlade.Service/OpenBlade.Service.csproj'
        $captureProject = 'src/OpenBlade.Capture/OpenBlade.Capture.csproj'
        if ($publishProjects -contains $serviceProject -and
            $publishProjects -contains $captureProject) {
            $serviceProtocol = Join-Path $outputRoot 'OpenBlade.Service\OpenBlade.Protocol.dll'
            $captureProtocol = Join-Path $outputRoot 'OpenBlade.Capture\OpenBlade.Protocol.dll'
            $serviceHash = (Get-FileHash -LiteralPath $serviceProtocol -Algorithm SHA256).Hash
            $captureHash = (Get-FileHash -LiteralPath $captureProtocol -Algorithm SHA256).Hash
            if ($serviceHash -ne $captureHash) {
                throw 'Affected Service and Capture publishes produced different Protocol assemblies.'
            }
        }
    }
}
