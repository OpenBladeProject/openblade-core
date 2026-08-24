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

function Get-WorkflowJobBlock {
    param(
        [Parameter(Mandatory)][string]$Workflow,
        [Parameter(Mandatory)][string]$Job
    )

    $pattern = '(?ms)^  ' + [Regex]::Escape($Job) +
        ':\r?\n.*?(?=^  [a-z0-9][a-z0-9-]*:\r?\n|\z)'
    $match = [Regex]::Match($Workflow, $pattern)
    Assert-True $match.Success "Workflow is missing job '$Job'."
    return $match.Value
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
foreach ($requiredBuildJob in @(
        'validate-request:',
        'quality-and-tests:',
        'build-contracts:',
        'component-publish:',
        'installer-validation:',
        'build-and-test:')) {
    Assert-True ($build.Contains($requiredBuildJob)) `
        "Shared build workflow is missing parallel job '$requiredBuildJob'."
}
$qualityJob = Get-WorkflowJobBlock -Workflow $build -Job 'quality-and-tests'
$contractJob = Get-WorkflowJobBlock -Workflow $build -Job 'build-contracts'
$componentJob = Get-WorkflowJobBlock -Workflow $build -Job 'component-publish'
$installerJob = Get-WorkflowJobBlock -Workflow $build -Job 'installer-validation'
$gateJob = Get-WorkflowJobBlock -Workflow $build -Job 'build-and-test'
foreach ($parallelJob in @($qualityJob, $contractJob, $componentJob, $installerJob)) {
    Assert-True ($parallelJob.Contains('needs: validate-request')) `
        'Each validation lane must depend only on the shared input gate.'
}
Assert-True ($qualityJob.Contains('dotnet test OpenBlade.slnx')) `
    'The quality lane must own solution tests.'
Assert-True ($qualityJob.Contains('submodules: recursive')) `
    'The quality lane must check out capture fixtures used by protocol tests.'
Assert-True (-not $qualityJob.Contains('Get-ChildItem .\tests\Build.Tests')) `
    'The quality lane must not serialize build contracts after solution tests.'
Assert-True ($contractJob.Contains('Get-ChildItem .\tests\Build.Tests')) `
    'The build-contract lane must own the PowerShell contract suites.'
Assert-True ($contractJob.Contains(
        'dotnet restore .\src\OpenBlade.Tray\OpenBlade.Tray.csproj -p:NuGetAudit=false -p:OpenBladeLocalizationPreview=true')) `
    'The full contract lane must restore localization-preview assets.'
Assert-True ($contractJob.Contains(
        'dotnet restore .\build\installer\OpenBlade.Installer.UI.Tests\OpenBlade.Installer.UI.Tests.csproj -p:NuGetAudit=false')) `
    'The full contract lane must restore the disconnected installer renderer.'
Assert-True ($contractJob.Contains('submodules: recursive')) `
    'The build-contract lane must check out capture tools used by contract tests.'
Assert-True (-not $contractJob.Contains('.\build\Publish.ps1')) `
    'The build-contract lane must not serialize installer publication.'
Assert-True ($componentJob.Contains('.\build\Run-AffectedCi.ps1')) `
    'The component-publish lane must use the validated affected runner.'
Assert-True ($componentJob.Contains('-Lane Publish')) `
    'The component-publish lane must own affected executable publication.'
Assert-True ($installerJob.Contains('.\build\Publish.ps1')) `
    'The installer lane must own production publication.'
Assert-True ($installerJob.Contains('submodules: false')) `
    'The installer lane must not fetch the captures submodule it does not consume.'
foreach ($requiredGateDependency in @(
        '- validate-request',
        '- quality-and-tests',
        '- build-contracts',
        '- component-publish',
        '- installer-validation')) {
    Assert-True ($gateJob.Contains($requiredGateDependency)) `
        "The final build gate is missing dependency '$requiredGateDependency'."
}
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
foreach ($requiredGateContract in @(
        'if: ${{ inputs.run-installer }}',
        'if: ${{ always() }}',
        'OPENBLADE_INSTALLER_RESULT: ${{ needs.installer-validation.result }}',
        'OPENBLADE_COMPONENT_PUBLISH_RESULT: ${{ needs.component-publish.result }}',
        'OPENBLADE_RUN_COMPONENT_PUBLISH: ${{ inputs.run-component-publish }}',
        'OPENBLADE_RUN_INSTALLER: ${{ inputs.run-installer }}',
        './build/Assert-CiLaneResults.ps1')) {
    Assert-True ($build.Contains($requiredGateContract)) `
        "Shared build workflow is missing final-gate contract '$requiredGateContract'."
}
Assert-True (-not $gateJob.Contains('!cancelled()')) `
    'Cancellation must reach the final gate and fail its required check.'

Assert-True ($nightly.Contains("cron: '17 9 * * *'")) `
    'Nightly builds must run away from the top-of-hour Actions peak.'
Assert-True ($nightly.Contains('workflow_dispatch:')) `
    'Nightly builds must support explicit manual dispatch.'
Assert-True ($nightly.Contains('group: openblade-nightly')) `
    'Nightly builds must use a dedicated concurrency group.'
Assert-True ($nightly.Contains('channel: Nightly')) `
    'The nightly caller must request the nightly identity.'
Assert-True ($nightly.Contains('run-installer: true')) `
    'Nightly builds must always run production installer validation.'
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
$publishJob = Get-WorkflowJobBlock `
    -Workflow $nightly `
    -Job 'publish-nightly-release'
$changelogJob = Get-WorkflowJobBlock `
    -Workflow $nightly `
    -Job 'update-nightly-changelog'
foreach ($requiredReleaseContract in @(
        'publish-nightly-release:',
        "if: `${{ github.ref == 'refs/heads/main' }}",
        'fetch-depth: 0',
        './build/NightlyChangelog.ps1',
        'OpenBlade-Nightly-CHANGELOG.md',
        'gh release create',
        '--prerelease',
        '--latest=false',
        'artifacts/OpenBlade-Setup.exe.sha256',
        'artifacts/OpenBlade-Nightly.json',
        'update-nightly-changelog:',
        'needs: publish-nightly-release',
        'docs/nightly-changelog-automation',
        '--force-with-lease=',
        'gh pr create',
        '--draft',
        'Version impact: none',
        'Version change: unchanged',
        'Reason: Records published changelog metadata without changing product behavior.')) {
    Assert-True ($nightly.Contains($requiredReleaseContract)) `
        "Nightly release publishing is missing '$requiredReleaseContract'."
}
Assert-True ($publishJob.Contains('contents: write')) `
    'Only the release job needs permission to publish repository contents.'
Assert-True (-not $publishJob.Contains('pull-requests: write')) `
    'The release job must not receive pull-request write permission.'
Assert-True ($changelogJob.Contains('contents: write')) `
    'The changelog job needs permission to update its bot branch.'
Assert-True ($changelogJob.Contains('pull-requests: write')) `
    'The changelog job needs permission to open or update its draft pull request.'
Assert-True ($publishJob.Contains(
        'Nightly changelog tally failed without blocking publication')) `
    'Changelog tally failures must not prevent nightly publication.'
Assert-True ($publishJob.Contains(
        'The attached changelog is the exact source snapshot.')) `
    'A failed tally must attach and identify the exact source changelog.'
Assert-True ($publishJob.Contains(
        'Changelog processing runs independently after publication.')) `
    'Initial release notes must describe processing without promising an attachment.'
Assert-True (-not $publishJob.Contains(
        'changelog tally is attached after publication')) `
    'Initial release notes must not promise an attachment before upload succeeds.'
Assert-True ($publishJob.Contains('continue-on-error: true')) `
    'Attaching the changelog must remain nonfatal after release publication.'
$publishIndex = $publishJob.IndexOf(
    'gh release create',
    [StringComparison]::Ordinal)
$tallyIndex = $publishJob.IndexOf(
    'Write-OpenBladeNightlyChangelogSnapshot',
    [StringComparison]::Ordinal)
Assert-True ($publishIndex -ge 0 -and $publishIndex -lt $tallyIndex) `
    'The nightly release must be published before changelog generation begins.'
Assert-True (-not $nightly.Contains('[DateTimeOffset]::UtcNow')) `
    'Nightly changelog dates must not depend on a second wall-clock reading.'
Assert-True (-not $nightly.Contains('metadata.createdAtUtc')) `
    'Nightly changelog dates must not differ between release and update jobs.'
Assert-True ([Regex]::Matches(
        $nightly,
        '-CurrentPublishedAt \$currentPublishedAt').Count -eq 2) `
    'Both changelog paths must use the canonical release publication timestamp.'
Assert-True (-not $nightly.Contains('git push origin main')) `
    'Nightly changelog automation must never push directly to main.'
$pwshBlockPattern =
    '(?ms)^      - name:.*?^        shell: pwsh\r?\n' +
    '        run: \|\r?\n' +
    '(?<script>(?:(?: {10}[^\r\n]*)?\r?\n)+)'
$pwshBlocks = [Regex]::Matches($nightly, $pwshBlockPattern)
Assert-True ($pwshBlocks.Count -ge 2) `
    'Nightly workflow tests must parse each multiline PowerShell step.'
foreach ($pwshBlock in $pwshBlocks) {
    $scriptText = @(
        $pwshBlock.Groups['script'].Value -split '\r?\n' |
            ForEach-Object { $_ -replace '^          ', '' }) -join "`n"
    $scriptText = [Regex]::Replace(
        $scriptText,
        '\$\{\{.*?\}\}',
        '1')
    $tokens = $null
    $parseErrors = $null
    [void][Management.Automation.Language.Parser]::ParseInput(
        $scriptText,
        [ref]$tokens,
        [ref]$parseErrors)
    Assert-True ($parseErrors.Count -eq 0) (
        'Nightly workflow contains invalid embedded PowerShell: ' +
        (($parseErrors | ForEach-Object Message) -join '; '))
}
Assert-True (-not $build.Contains('id-token: write')) `
    'The shared build workflow must not elevate stable CI permissions.'
Assert-True (-not $build.Contains('attestations: write')) `
    'The shared build workflow must keep stable CI read-only.'
Assert-True (-not $build.Contains('contents: write')) `
    'The shared build workflow must keep stable CI and product builds read-only.'

Write-Host 'Nightly workflow tests passed.'
