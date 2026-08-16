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
        "'--self-contained', 'true'",
        'produced different Protocol assemblies')) {
    if (-not $source.Contains($requiredContract)) {
        throw "Affected CI runner is missing '$requiredContract'."
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

Write-Host 'Affected CI runner tests passed.'
