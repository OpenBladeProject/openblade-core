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

Assert-Equal $false (Test-OpenBladeCiBuildRequired -ChangedPath @(
        'README.md',
        'docs/images/openblade-dashboard.png',
        '.github/ISSUE_TEMPLATE/bug.yml')) `
    'Documentation-only changes must skip the full build.'
Assert-Equal $false (Test-OpenBladeCiBuildRequired -ChangedPath @(
        '.\CHANGELOG.md',
        'LICENSE')) `
    'Normalized root documentation paths must skip the full build.'
Assert-Equal $true (Test-OpenBladeCiBuildRequired -ChangedPath @(
        'README.md',
        'src/OpenBlade.Service/Program.cs')) `
    'A source change must require the full build.'
Assert-Equal $true (Test-OpenBladeCiBuildRequired -ChangedPath @(
        'docs/images/openblade-dashboard.png',
        'build/identity/Assets/StoreLogo.png')) `
    'A production asset change must require the full build.'
Assert-Equal $true (Test-OpenBladeCiBuildRequired -ChangedPath @(
        '.github/workflows/ci.yml')) `
    'A workflow change must require the full build.'
Assert-Equal $true (Test-OpenBladeCiBuildRequired -ChangedPath @('captures')) `
    'A captures submodule update must require the full build.'
Assert-Equal $true (Test-OpenBladeCiBuildRequired -ChangedPath @()) `
    'An empty or unknown change set must require the full build.'
Assert-Equal $true (Test-OpenBladeCiBuildRequired `
        -ChangedPath @('README.md') `
        -Force) `
    'A forced run must require the full build.'

Assert-Equal $false (Test-OpenBladeCiInstallerRequired -ChangedPath @(
        'tests/OpenBlade.Service.Tests/DeviceWorkerTests.cs',
        'tests/Build.Tests/CiBuildScope.Tests.ps1')) `
    'Test-only changes must skip production installer validation.'
Assert-Equal $false (Test-OpenBladeCiInstallerRequired -ChangedPath @(
        'README.md',
        '.\tests\OpenBlade.Tray.Tests\CapabilityPresentationTests.cs')) `
    'Documentation mixed with normalized test paths must skip installer validation.'
Assert-Equal $true (Test-OpenBladeCiInstallerRequired -ChangedPath @(
        'tests/OpenBlade.Service.Tests/DeviceWorkerTests.cs',
        'src/OpenBlade.Service/DeviceWorker.cs')) `
    'A shipped source change must require installer validation.'
Assert-Equal $true (Test-OpenBladeCiInstallerRequired -ChangedPath @(
        '.github/workflows/build.yml')) `
    'A workflow change must require installer validation.'
Assert-Equal $true (Test-OpenBladeCiInstallerRequired -ChangedPath @(
        'test/unknown.txt')) `
    'An unknown test-like path must fail closed and require installer validation.'
Assert-Equal $true (Test-OpenBladeCiInstallerRequired -ChangedPath @()) `
    'An empty or unknown change set must require installer validation.'
Assert-Equal $true (Test-OpenBladeCiInstallerRequired `
        -ChangedPath @('tests/OpenBlade.Service.Tests/DeviceWorkerTests.cs') `
        -Force) `
    'A forced run must require installer validation.'

$ci = Get-Content -LiteralPath (
    Join-Path $repository '.github\workflows\ci.yml') -Raw
foreach ($requiredContract in @(
        '- opened',
        '- synchronize',
        '- reopened',
        '- ready_for_review',
        'classify-changes:',
        'fetch-depth: 0',
        'git diff --name-only --no-renames',
        'Test-OpenBladeCiBuildRequired',
        'Test-OpenBladeCiInstallerRequired',
        'requires-installer:',
        'needs: classify-changes',
        "if: `${{ needs.classify-changes.outputs.requires-build == 'true' }}",
        "run-installer: `${{ needs.classify-changes.outputs.requires-installer == 'true' }}")) {
    if (-not $ci.Contains($requiredContract)) {
        throw "CI workflow is missing '$requiredContract'."
    }
}
if ($ci.Contains('- edited')) {
    throw 'PR description edits must not rerun the full CI workflow.'
}

$build = Get-Content -LiteralPath (
    Join-Path $repository '.github\workflows\build.yml') -Raw
$qualityStart = $build.IndexOf('  quality-and-tests:', [StringComparison]::Ordinal)
$contractsStart = $build.IndexOf('  build-contracts:', [StringComparison]::Ordinal)
if ($qualityStart -lt 0 -or $contractsStart -le $qualityStart) {
    throw 'The shared build workflow quality job could not be isolated.'
}
$qualityJob = $build.Substring($qualityStart, $contractsStart - $qualityStart)
if (-not $qualityJob.Contains(
        'OPENBLADE_SKIP_INTERACTIVE_DESKTOP_TESTS: 0')) {
    throw 'Hosted CI must run interactive-desktop tests by default.'
}
$testStepStart = $qualityJob.IndexOf(
    '      - name: Test',
    [StringComparison]::Ordinal)
if ($testStepStart -lt 0) {
    throw 'Hosted CI quality job is missing the Test step.'
}

$nextStepStart = $qualityJob.IndexOf(
    '      - name:',
    $testStepStart + 1,
    [StringComparison]::Ordinal)
$testStep = if ($nextStepStart -ge 0) {
    $qualityJob.Substring($testStepStart, $nextStepStart - $testStepStart)
}
else {
    $qualityJob.Substring($testStepStart)
}

foreach ($requiredTestContract in @(
        'dotnet test OpenBlade.slnx',
        '-m:1')) {
    if (-not $testStep.Contains($requiredTestContract)) {
        throw "Hosted CI Test step is missing '$requiredTestContract'."
    }
}

Write-Host 'CI build scope tests passed.'
