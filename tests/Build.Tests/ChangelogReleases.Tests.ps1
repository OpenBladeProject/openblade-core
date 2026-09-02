[CmdletBinding()]
param()

Set-StrictMode -Version 3.0
$ErrorActionPreference = 'Stop'
$repository = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$validator = Join-Path $repository 'build\Validate-ChangelogReleases.ps1'
if (-not (Test-Path -LiteralPath $validator -PathType Leaf)) {
    throw 'The changelog release validator is missing.'
}

$utf8 = [Text.UTF8Encoding]::new($false)
$root = Join-Path ([IO.Path]::GetTempPath()) (
    'OpenBlade-ChangelogReleases-' + [Guid]::NewGuid().ToString('n'))
[IO.Directory]::CreateDirectory($root) | Out-Null

function New-TestChangelog {
    param(
        [Parameter(Mandatory)][string]$Path,
        [Parameter(Mandatory)][AllowEmptyString()][string[]]$Lines
    )
    [IO.File]::WriteAllText($Path, ($Lines -join "`r`n") + "`r`n", $utf8)
}

function New-TestReleaseDocument {
    param(
        [Parameter(Mandatory)][string]$Path,
        [Parameter(Mandatory)][AllowEmptyCollection()][string[]]$TagName
    )
    $records = @(
        foreach ($tag in $TagName) {
            [ordered]@{ tagName = $tag; publishedAt = '2026-01-02T00:00:00Z' }
        }
    )
    $json = if ($records.Count -eq 0) { '[]' } else { $records | ConvertTo-Json -Depth 3 }
    [IO.File]::WriteAllText($Path, $json, $utf8)
}

function Assert-ValidatorThrows {
    param(
        [Parameter(Mandatory)][hashtable]$Arguments,
        [Parameter(Mandatory)][string]$Because,
        [string]$MessageLike
    )
    try {
        & $validator @Arguments -ErrorAction Stop | Out-Null
    }
    catch {
        if ($MessageLike -and $_.Exception.Message -notlike $MessageLike) {
            throw ("$Because The validator threw an unexpected message: " +
                $_.Exception.Message)
        }
        return
    }
    throw $Because
}

try {
    $completeLines = @(
        '# Changelog',
        '',
        '## [Unreleased]',
        '',
        '## [1.2.3-nightly.5] - 2026-01-02',
        '',
        '### Added',
        '',
        '- recorded entry',
        '',
        '[Unreleased]: https://example.invalid/compare/v1.2.3-nightly.5...HEAD',
        '[1.2.3-nightly.5]: https://example.invalid/compare/v1.0.0-nightly.1...v1.2.3-nightly.5')

    $releases = Join-Path $root 'releases.json'
    New-TestReleaseDocument -Path $releases -TagName @('v1.2.3-nightly.5')

    # A recorded release with its comparison link passes.
    $complete = Join-Path $root 'complete.md'
    New-TestChangelog -Path $complete -Lines $completeLines
    $result = & $validator -ChangelogPath $complete -PublishedReleasesPath $releases
    if ("$result" -notlike '*Changelog releases passed*') {
        throw 'A complete changelog must pass the changelog release check.'
    }

    # The exact regression this guards: a release published without recording it.
    $missing = Join-Path $root 'missing-section.md'
    New-TestChangelog -Path $missing -Lines @(
        '# Changelog',
        '',
        '## [Unreleased]',
        '',
        '### Added',
        '',
        '- unrecorded entry',
        '',
        '[Unreleased]: https://example.invalid/compare/v1.0.0-nightly.1...HEAD')
    Assert-ValidatorThrows `
        -Arguments @{ ChangelogPath = $missing; PublishedReleasesPath = $releases } `
        -Because 'A published release with no changelog section must fail.' `
        -MessageLike '*1.2.3-nightly.5*'

    # A section without its comparison link is a half-finished edit.
    $unlinked = Join-Path $root 'missing-link.md'
    New-TestChangelog -Path $unlinked -Lines @(
        $completeLines | Where-Object {
            -not $_.StartsWith('[1.2.3-nightly.5]:', [StringComparison]::Ordinal)
        })
    Assert-ValidatorThrows `
        -Arguments @{ ChangelogPath = $unlinked; PublishedReleasesPath = $releases } `
        -Because 'A recorded release without a comparison link must fail.' `
        -MessageLike '*comparison link*'

    # Attempt tags resolve to the same section as their base nightly.
    $attempt = Join-Path $root 'attempt.json'
    New-TestReleaseDocument -Path $attempt -TagName @('v1.2.3-nightly.5-attempt.2')
    $result = & $validator -ChangelogPath $complete -PublishedReleasesPath $attempt
    if ("$result" -notlike '*Changelog releases passed*') {
        throw 'An attempt tag must resolve to its base nightly section.'
    }

    # Non-release tags are ignored rather than demanding a section.
    $unrelated = Join-Path $root 'unrelated.json'
    New-TestReleaseDocument -Path $unrelated -TagName @('v1.2.3', 'pr-228-review')
    $result = & $validator -ChangelogPath $complete -PublishedReleasesPath $unrelated
    if ("$result" -notlike '*verified nothing*') {
        throw 'Tags that are not nightly releases must not be treated as releases.'
    }

    # An empty history reports that it verified nothing instead of passing quietly.
    $empty = Join-Path $root 'empty.json'
    New-TestReleaseDocument -Path $empty -TagName @()
    $result = & $validator -ChangelogPath $complete -PublishedReleasesPath $empty
    if ("$result" -notlike '*verified nothing*') {
        throw 'An empty release history must report that it verified nothing.'
    }

    # A malformed document fails loudly rather than silently checking nothing.
    $malformed = Join-Path $root 'malformed.json'
    [IO.File]::WriteAllText($malformed, '{ not json', $utf8)
    Assert-ValidatorThrows `
        -Arguments @{ ChangelogPath = $complete; PublishedReleasesPath = $malformed } `
        -Because 'A malformed release document must fail.' `
        -MessageLike '*valid JSON*'

    # The live repository must satisfy the same rule whenever tags are present.
    $live = & $validator -Repository $repository
    if ("$live" -notlike '*Changelog releases passed*' -and
        "$live" -notlike '*verified nothing*') {
        throw "The repository changelog release check returned an unexpected result: $live"
    }
}
finally {
    if (Test-Path -LiteralPath $root) {
        Remove-Item -LiteralPath $root -Recurse -Force -ErrorAction SilentlyContinue
    }
}

# Keep the rule discoverable for anyone publishing a release by hand.
$agents = [IO.File]::ReadAllText(
    (Join-Path $repository 'AGENTS.md'),
    [Text.UTF8Encoding]::new($false, $true))
if ($agents.IndexOf(
        'Validate-ChangelogReleases.ps1',
        [StringComparison]::Ordinal) -lt 0) {
    throw 'AGENTS.md must document the changelog release validator.'
}

Write-Host 'Changelog release contracts passed.'
