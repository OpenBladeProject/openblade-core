[CmdletBinding()]
param()

Set-StrictMode -Version 3.0
$ErrorActionPreference = 'Stop'
$repository = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$skillDirectory = Join-Path $repository (
    '.agents\skills\openblade-local-nightly')

function Read-RequiredFile {
    param([Parameter(Mandatory)][string]$Path)

    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) {
        throw "Required local Nightly skill file is missing: $Path"
    }
    return [IO.File]::ReadAllText($Path)
}

function Assert-ContainsAll {
    param(
        [Parameter(Mandatory)][string]$Content,
        [Parameter(Mandatory)][string[]]$RequiredValues,
        [Parameter(Mandatory)][string]$Source
    )

    foreach ($value in $RequiredValues) {
        if ($Content.IndexOf($value, [StringComparison]::Ordinal) -lt 0) {
            throw "$Source is missing required contract '$value'."
        }
    }
}

$skill = Read-RequiredFile (Join-Path $skillDirectory 'SKILL.md')
if (-not [Regex]::IsMatch(
        $skill,
        '(?m)^name: openblade-local-nightly\r?$')) {
    throw 'The local Nightly skill has the wrong frontmatter name.'
}
if (-not [Regex]::IsMatch(
        $skill,
        '(?m)^description: [^\r\n]*hosted Nightly workflow cannot execute[^\r\n]*\r?$')) {
    throw 'The skill description must keep the hosted-CI fallback boundary.'
}
if ($skill.IndexOf('[TODO', [StringComparison]::OrdinalIgnoreCase) -ge 0) {
    throw 'The local Nightly skill contains unfinished scaffold text.'
}
Assert-ContainsAll `
    -Content $skill `
    -RequiredValues @(
        'references/workflow.md',
        'openblade_release_operator',
        'openblade_validation_operator',
        'ask for explicit approval to execute that same bounded build or validation directly',
        'force-with-lease guard',
        'Do not edit source',
        'install artifacts',
        'request UAC',
        'claim an attestation') `
    -Source 'SKILL.md'

$workflow = Read-RequiredFile (
    Join-Path $skillDirectory 'references\workflow.md')
Assert-ContainsAll `
    -Content $workflow `
    -RequiredValues @(
        '.github/workflows/nightly.yml',
        '.github/workflows/build.yml',
        'build/Publish.ps1',
        'build/PublishPayloadValidation.ps1',
        'build/NightlyChangelog.ps1',
        'full 40-character source SHA',
        'OSSBlade/openblade-core',
        'Before dispatch, list Nightly runs',
        'Do not manufacture a build revision',
        'Create a fresh detached exact-commit worktree under `C:\tmp`',
        '-BuildChannel Nightly',
        '-BuildRevision <BuildRevision>',
        '-SourceRevision <full-source-sha>',
        '-WorkflowRunId <run-id>.<run-attempt>',
        'Run the hosted-equivalent validation lanes',
        'dotnet format OpenBlade.slnx --verify-no-changes',
        'dotnet test OpenBlade.slnx',
        'every `tests\Build.Tests\*.Tests.ps1`',
        'localPreflightPassed',
        'Create an empty persistent draft',
        'Keep the release as a draft if any check fails',
        'Write-OpenBladeNightlyChangelogSnapshot',
        'docs/nightly-changelog-automation',
        '--force-with-lease=refs/heads/docs/nightly-changelog-automation:<observed-remote-sha>',
        'exactly four assets',
        'GitHub artifact attestations were unavailable',
        'Leave the exact build worktree') `
    -Source 'references/workflow.md'

$headings = @(
    '## 1. Establish an unavailable hosted run',
    '## 2. Build the exact source locally',
    '## 3. Run the hosted-equivalent validation lanes',
    '## 4. Validate local release artifacts',
    '## 5. Create and fill a persistent draft',
    '## 6. Attach the exact changelog snapshot',
    '## 7. Reconcile the Nightly changelog draft PR',
    '## 8. Perform final remote readback',
    '## 9. Preserve recoverability')
$previousIndex = -1
foreach ($heading in $headings) {
    $index = $workflow.IndexOf($heading, [StringComparison]::Ordinal)
    if ($index -le $previousIndex) {
        throw "Local Nightly workflow phase is missing or out of order: $heading"
    }
    $previousIndex = $index
}

$metadata = Read-RequiredFile (
    Join-Path $skillDirectory 'agents\openai.yaml')
Assert-ContainsAll `
    -Content $metadata `
    -RequiredValues @(
        'display_name: "OpenBlade Local Nightly"',
        'short_description: "Publish a Nightly when hosted CI is unavailable"',
        'default_prompt: "Use $openblade-local-nightly ') `
    -Source 'agents/openai.yaml'

Write-Host 'Local Nightly skill contracts passed.'
