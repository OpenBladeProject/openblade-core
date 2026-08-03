[CmdletBinding()]
param()

Set-StrictMode -Version 3.0
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

$ci = Get-Content -LiteralPath (Join-Path $repository '.github\workflows\ci.yml') -Raw
$build = Get-Content -LiteralPath (Join-Path $repository '.github\workflows\build.yml') -Raw
$nightly = Get-Content -LiteralPath (Join-Path $repository '.github\workflows\nightly.yml') -Raw

Assert-True ($ci.Contains('uses: ./.github/workflows/build.yml')) `
    'CI must call the shared build workflow.'
Assert-True ($ci.Contains('channel: Stable')) `
    'CI must request the stable build identity.'
Assert-True ($build.Contains('workflow_call:')) `
    'The shared build workflow must be reusable.'
foreach ($requiredBuildStep in @(
        'dotnet format OpenBlade.slnx',
        'dotnet build OpenBlade.slnx',
        'dotnet test OpenBlade.slnx',
        'Get-ChildItem .\tests\Build.Tests\*.Tests.ps1',
        'Validate build request',
        'Nightly build revisions must be between 1 and 65535.',
        '.\build\Publish.ps1',
        '-BuildChannel Nightly',
        '-BuildRevision ${{ inputs.build-revision }}',
        '-SourceRevision ''${{ github.sha }}''',
        'uses: actions/upload-artifact@v4',
        'retention-days: 14',
        'if-no-files-found: error')) {
    Assert-True ($build.Contains($requiredBuildStep)) `
        "Shared build workflow is missing '$requiredBuildStep'."
}

Assert-True ($nightly.Contains("cron: '17 9 * * *'")) `
    'Nightly builds must run away from the top-of-hour Actions peak.'
Assert-True ($nightly.Contains('workflow_dispatch:')) `
    'Nightly builds must support explicit manual dispatch.'
Assert-True ($nightly.Contains('group: openblade-nightly')) `
    'Nightly builds must use a dedicated concurrency group.'
Assert-True ($nightly.Contains('channel: Nightly')) `
    'The nightly caller must request the nightly identity.'
Assert-True ($nightly.Contains(
        'build-revision: ${{ fromJSON(github.run_number) }}')) `
    'The nightly revision must convert the dedicated workflow run number to a number.'
Assert-True ($nightly.Contains('needs: build-and-publish')) `
    'Nightly attestation must wait for the reusable build job.'
Assert-True ($nightly.Contains(
        "if: `${{ vars.OPENBLADE_ENABLE_ATTESTATIONS == 'true' }}")) `
    'Nightly attestation must remain opt-in for repositories that support it.'
Assert-True ($nightly.Contains('uses: actions/download-artifact@v4')) `
    'Nightly attestation must download the installer from the build job.'
Assert-True ($nightly.Contains('uses: actions/attest@v4')) `
    'The nightly caller must attest the installer provenance.'
Assert-True ($nightly.Contains('id-token: write')) `
    'Nightly provenance attestation requires OIDC permission.'
Assert-True ($nightly.Contains('attestations: write')) `
    'Nightly provenance attestation requires attestation permission.'
foreach ($requiredReleaseContract in @(
        'publish-nightly-release:',
        "if: `${{ github.ref == 'refs/heads/main' }}",
        'contents: write',
        'gh release create',
        '--prerelease',
        '--latest=false',
        'artifacts/OpenBlade-Setup.exe.sha256',
        'artifacts/OpenBlade-Nightly.json')) {
    Assert-True ($nightly.Contains($requiredReleaseContract)) `
        "Nightly release publishing is missing '$requiredReleaseContract'."
}
Assert-True (-not $build.Contains('id-token: write')) `
    'The shared build workflow must not elevate stable CI permissions.'
Assert-True (-not $build.Contains('attestations: write')) `
    'The shared build workflow must keep stable CI read-only.'
Assert-True (-not $build.Contains('contents: write')) `
    'The shared build workflow must keep stable CI and product builds read-only.'

Write-Host 'Nightly workflow tests passed.'
