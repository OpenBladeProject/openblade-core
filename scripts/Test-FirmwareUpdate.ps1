[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [ValidateSet('Baseline', 'PostUpdate', 'Rehearsal', 'Reversible', 'Recover')]
    [string] $Phase,

    [string] $BaselinePath,

    [string] $OutputDirectory,

    [string] $ConfirmTarget
)

$ErrorActionPreference = 'Stop'
$repositoryRoot = Split-Path -Parent $PSScriptRoot
$projectPath = Join-Path $repositoryRoot 'src\OpenBlade.FirmwareSmoke\OpenBlade.FirmwareSmoke.csproj'

if ([string]::IsNullOrWhiteSpace($OutputDirectory)) {
    $timestamp = Get-Date -Format 'yyyyMMdd-HHmmss'
    $OutputDirectory = Join-Path $repositoryRoot "artifacts\firmware-smoke\$timestamp-$($Phase.ToLowerInvariant())"
}

$command = switch ($Phase) {
    'Baseline' { 'baseline' }
    'PostUpdate' { 'post-update' }
    'Rehearsal' { 'rehearsal' }
    'Reversible' { 'reversible' }
    'Recover' { 'recover' }
}

$toolArguments = @($command, '--output', $OutputDirectory)
if ($Phase -ne 'Baseline') {
    if ([string]::IsNullOrWhiteSpace($BaselinePath)) {
        throw '-BaselinePath is required for PostUpdate, Rehearsal, and Reversible.'
    }

    $toolArguments += @('--baseline', (Resolve-Path -LiteralPath $BaselinePath).Path)
}

if ($Phase -in @('Rehearsal', 'Reversible', 'Recover')) {
    if ([string]::IsNullOrWhiteSpace($ConfirmTarget)) {
        throw 'Reversible checks require -ConfirmTarget RZ09-0581-MCU-SMOKE.'
    }

    $toolArguments += @('--confirm-target', $ConfirmTarget)
}

Push-Location $repositoryRoot
try {
    & dotnet run `
        --project $projectPath `
        --configuration Release `
        -p:NuGetAudit=false `
        -- @toolArguments
    if ($LASTEXITCODE -ne 0) {
        throw "Firmware smoke utility exited with code $LASTEXITCODE."
    }
}
finally {
    Pop-Location
}
