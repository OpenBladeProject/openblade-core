[CmdletBinding()]
param()

Set-StrictMode -Version 3.0
$ErrorActionPreference = 'Stop'
$repository = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
. (Join-Path $repository 'build\NightlyChangelog.ps1')

function Assert-True {
    param(
        [Parameter(Mandatory)][bool]$Condition,
        [Parameter(Mandatory)][string]$Message
    )

    if (-not $Condition) {
        throw $Message
    }
}

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

$nightly6Source = @'
# Changelog

## [Unreleased]

### Added

- Already shipped before automation.

## [0.1.0] - 2026-07-17

### Added

- Initial release.

[Unreleased]: https://github.com/OSSBlade/openblade-core/compare/v0.1.0...HEAD
[0.1.0]: https://github.com/OSSBlade/openblade-core/releases/tag/v0.1.0
'@

$nightly7Source = @'
# Changelog

## [Unreleased]

### Fixed

- Fixed the first nightly regression.

## [0.1.0-nightly.6] - 2026-08-04

### Added

- Already shipped before automation.

## [0.1.0] - 2026-07-17

### Added

- Initial release.

[Unreleased]: https://github.com/OSSBlade/openblade-core/compare/v0.1.0-nightly.6...HEAD
[0.1.0-nightly.6]: https://github.com/OSSBlade/openblade-core/compare/v0.1.0...v0.1.0-nightly.6
[0.1.0]: https://github.com/OSSBlade/openblade-core/releases/tag/v0.1.0
'@

$nightly8Source = @'
# Changelog

## [Unreleased]

### Fixed

- Fixed the first nightly regression.

### Added

- Added the next nightly capability.

## [0.1.0-nightly.6] - 2026-08-04

### Added

- Already shipped before automation.

## [0.1.0] - 2026-07-17

### Added

- Initial release.

[Unreleased]: https://github.com/OSSBlade/openblade-core/compare/v0.1.0-nightly.6...HEAD
[0.1.0-nightly.6]: https://github.com/OSSBlade/openblade-core/compare/v0.1.0...v0.1.0-nightly.6
[0.1.0]: https://github.com/OSSBlade/openblade-core/releases/tag/v0.1.0
'@

$latestMain = @'
# Changelog

## [Unreleased]

### Changed

- Preserved a change merged after the nightly source commit.

### Fixed

- Fixed the first nightly regression.

### Added

- Added the next nightly capability.

## [0.1.0-nightly.6] - 2026-08-04

### Added

- Already shipped before automation.

## [0.1.0] - 2026-07-17

### Added

- Initial release.

[Unreleased]: https://github.com/OSSBlade/openblade-core/compare/v0.1.0-nightly.6...HEAD
[0.1.0-nightly.6]: https://github.com/OSSBlade/openblade-core/compare/v0.1.0...v0.1.0-nightly.6
[0.1.0]: https://github.com/OSSBlade/openblade-core/releases/tag/v0.1.0
'@

$releaseRecords = @(
    [pscustomobject]@{
        BuildRevision = 6
        TagName = 'v0.1.0-nightly.6'
        PublishedAt = [DateTimeOffset]::Parse('2026-08-04T11:47:22Z')
        SourceContent = $nightly6Source
    },
    [pscustomobject]@{
        BuildRevision = 7
        TagName = 'v0.1.0-nightly.7-attempt.2'
        PublishedAt = [DateTimeOffset]::Parse('2026-08-05T09:17:00Z')
        SourceContent = $nightly7Source
    },
    [pscustomobject]@{
        BuildRevision = 7
        TagName = 'v0.1.0-nightly.7-attempt.3'
        PublishedAt = [DateTimeOffset]::Parse('2026-08-05T10:17:00Z')
        SourceContent = $nightly7Source
    },
    [pscustomobject]@{
        BuildRevision = 8
        TagName = 'v0.1.0-nightly.8'
        PublishedAt = [DateTimeOffset]::Parse('2026-08-06T09:17:00Z')
        SourceContent = $nightly8Source
    })

$result = New-OpenBladeNightlyChangelogContent `
    -CurrentContent $latestMain `
    -BaseVersion '0.1.0' `
    -CurrentBuildRevision 8 `
    -ReleaseRecord $releaseRecords

Assert-True ($result.Content.Contains(
        "## [0.1.0-nightly.8] - 2026-08-06`n`n### Added`n`n" +
        '- Added the next nightly capability.')) `
    'Nightly 8 must contain only its newly added entry.'
Assert-True ($result.Content.Contains(
        "## [0.1.0-nightly.7] - 2026-08-05`n`n### Fixed`n`n" +
        '- Fixed the first nightly regression.')) `
    'Nightly 7 must be reconstructed when its earlier automation PR is still open.'
Assert-True ($result.Content.Contains(
        "## [Unreleased]`n`n### Changed`n`n" +
        '- Preserved a change merged after the nightly source commit.')) `
    'Entries merged after the nightly source commit must remain Unreleased.'
Assert-Equal 1 ([Regex]::Matches(
        $result.Content,
        [Regex]::Escape('- Fixed the first nightly regression.')).Count) `
    'A released entry must not remain duplicated under Unreleased.'
Assert-True ($result.Content.Contains(
        '[Unreleased]: https://github.com/OSSBlade/openblade-core/compare/v0.1.0-nightly.8...HEAD')) `
    'Unreleased must compare from the exact latest nightly tag.'
Assert-True ($result.Content.Contains(
        '[0.1.0-nightly.8]: https://github.com/OSSBlade/openblade-core/compare/v0.1.0-nightly.7-attempt.3...v0.1.0-nightly.8')) `
    'Nightly comparisons must retain the latest successful attempt tag.'
Assert-True ($result.Content.Contains(
        '[0.1.0-nightly.7]: https://github.com/OSSBlade/openblade-core/compare/v0.1.0-nightly.6...v0.1.0-nightly.7-attempt.3')) `
    'The first pending nightly must compare from the recorded baseline tag.'
Assert-Equal '0.1.0-nightly.7,0.1.0-nightly.8' `
    ($result.PendingVersions -join ',') `
    'Pending nightly versions must be reported in chronological order.'

$rerun = New-OpenBladeNightlyChangelogContent `
    -CurrentContent $result.Content `
    -BaseVersion '0.1.0' `
    -CurrentBuildRevision 8 `
    -ReleaseRecord $releaseRecords
Assert-Equal $result.Content $rerun.Content `
    'Rerunning a recorded nightly must not change the changelog.'
Assert-True ($rerun.CurrentSection.Contains('## [0.1.0-nightly.8]')) `
    'A rerun must recover the existing current nightly section for release notes.'

$missingReleasedEntry = $latestMain -replace (
    '(?ms)### Fixed\r?\n\r?\n' +
    [Regex]::Escape('- Fixed the first nightly regression.') +
    '\r?\n\r?\n'), ''
$missingResult = New-OpenBladeNightlyChangelogContent `
    -CurrentContent $missingReleasedEntry `
    -BaseVersion '0.1.0' `
    -CurrentBuildRevision 8 `
    -ReleaseRecord $releaseRecords
Assert-Equal '0.1.0-nightly.7,0.1.0-nightly.8' `
    ($missingResult.PendingVersions -join ',') `
    'Editing or removing an earlier bullet must not block changelog reconstruction.'

$editedReleasedEntry = $latestMain.Replace(
    '- Fixed the first nightly regression.',
    '- Fixed the first nightly regression typo.')
$editedResult = New-OpenBladeNightlyChangelogContent `
    -CurrentContent $editedReleasedEntry `
    -BaseVersion '0.1.0' `
    -CurrentBuildRevision 8 `
    -ReleaseRecord $releaseRecords
Assert-True ($editedResult.Content.Contains(
        "## [Unreleased]`n`n### Changed`n`n" +
        '- Preserved a change merged after the nightly source commit.')) `
    'Post-source entries must remain Unreleased when an earlier bullet was edited.'
Assert-True ($editedResult.Content.Contains(
        '- Fixed the first nightly regression typo.')) `
    'An edited historical bullet must remain as a new Unreleased correction.'

$movedReleasedEntry = (ConvertTo-OpenBladeNormalizedText -Text $latestMain).Replace(
    "### Fixed`n`n- Fixed the first nightly regression.`n`n### Added",
    "### Changed`n`n- Fixed the first nightly regression.`n`n### Added")
Assert-True ($movedReleasedEntry.Contains(
        "### Changed`n`n- Fixed the first nightly regression.")) `
    'The category-move fixture must move the released bullet.'
$movedResult = New-OpenBladeNightlyChangelogContent `
    -CurrentContent $movedReleasedEntry `
    -BaseVersion '0.1.0' `
    -CurrentBuildRevision 8 `
    -ReleaseRecord $releaseRecords
Assert-Equal 1 ([Regex]::Matches(
        $movedResult.Content,
        [Regex]::Escape('- Fixed the first nightly regression.')).Count) `
    'Moving an unchanged bullet between categories must not duplicate it.'

$markdownEntryContent = @'
# Changelog

## [Unreleased]

### Added

- Documented a command:

  ```powershell
  Get-OpenBladeStatus
  ```
  - Includes nested Markdown.
'@
$markdownEntries = @(Get-OpenBladeChangelogEntries `
        -Content $markdownEntryContent)
Assert-Equal 1 $markdownEntries.Count `
    'A changelog bullet may contain fenced and nested Markdown.'
Assert-True ($markdownEntries[0].Text.Contains('```powershell')) `
    'Fenced Markdown must remain part of its changelog bullet.'

$finalUnreleasedContent = @'
# Changelog

[Unreleased]: https://github.com/OSSBlade/openblade-core/compare/v0.1.0...HEAD

## [Unreleased]

### Added

- Kept an Unreleased section at end of file.
'@
$finalUnreleasedContent = $finalUnreleasedContent.TrimEnd()
$finalSectionResult = New-OpenBladeNightlyChangelogContent `
    -CurrentContent $finalUnreleasedContent `
    -BaseVersion '0.1.0' `
    -CurrentBuildRevision 1 `
    -ReleaseRecord @(
        [pscustomobject]@{
            BuildRevision = 0
            TagName = 'v0.1.0'
            PublishedAt = [DateTimeOffset]::MinValue
            SourceContent = "# Changelog`n`n## [Unreleased]`n"
        },
        [pscustomobject]@{
            BuildRevision = 1
            TagName = 'v0.1.0-nightly.1'
            PublishedAt = [DateTimeOffset]::Parse('2026-08-05T09:17:00Z')
            SourceContent = $finalUnreleasedContent
        })
Assert-True ($finalSectionResult.CurrentSection.Contains(
        '- Kept an Unreleased section at end of file.')) `
    'An Unreleased section at end of file must be sliced without indexing past EOF.'

Assert-Equal "`r`n" (Get-OpenBladeChangelogNewLine -Text "a`r`nb`r`n") `
    'CRLF changelogs must retain CRLF output.'
Assert-Equal "`n" (Get-OpenBladeChangelogNewLine -Text "a`nb`n") `
    'LF changelogs must retain LF output.'

$temporaryRoot = Join-Path ([IO.Path]::GetTempPath()) (
    'openblade-nightly-changelog-' + [Guid]::NewGuid().ToString('N'))
try {
    [IO.Directory]::CreateDirectory($temporaryRoot) | Out-Null
    Push-Location $temporaryRoot
    try {
        & git init --quiet
        & git config user.name 'OpenBlade Tests'
        & git config user.email 'openblade-tests@example.invalid'
        $crlfNightly6 = (ConvertTo-OpenBladeNormalizedText -Text $nightly6Source).
            Replace("`n", "`r`n")
        [IO.File]::WriteAllText(
            (Join-Path $temporaryRoot 'CHANGELOG.md'),
            $crlfNightly6,
            [Text.UTF8Encoding]::new($false))
        & git add -- CHANGELOG.md
        & git commit --quiet -m 'baseline'
        & git tag v0.1.0-nightly.6

        $crlfNightly7 = (ConvertTo-OpenBladeNormalizedText -Text $nightly7Source).
            Replace("`n", "`r`n")
        [IO.File]::WriteAllText(
            (Join-Path $temporaryRoot 'CHANGELOG.md'),
            $crlfNightly7,
            [Text.UTF8Encoding]::new($false))
        & git add -- CHANGELOG.md
        & git commit --quiet -m 'nightly source'
        $sourceRevision = (& git rev-parse HEAD).Trim()

        $crlfLatestMain = (ConvertTo-OpenBladeNormalizedText -Text $latestMain).
            Replace("`n", "`r`n")
        $changelogPath = Join-Path $temporaryRoot 'CHANGELOG.md'
        [IO.File]::WriteAllText(
            $changelogPath,
            $crlfLatestMain,
            [Text.UTF8Encoding]::new($false))
        $manifestPath = Join-Path $temporaryRoot 'releases.json'
        [IO.File]::WriteAllText(
            $manifestPath,
            '[{"tagName":"v0.1.0-nightly.6","publishedAt":"2026-08-04T11:47:22Z"}]',
            [Text.UTF8Encoding]::new($false))
        $outputPath = Join-Path $temporaryRoot 'generated\CHANGELOG.md'
        $wrapperResult = Write-OpenBladeNightlyChangelogSnapshot `
            -RepositoryRoot $temporaryRoot `
            -ChangelogPath $changelogPath `
            -OutputPath $outputPath `
            -PublishedReleasesPath $manifestPath `
            -BaseVersion '0.1.0' `
            -BuildRevision 7 `
            -CurrentTag 'v0.1.0-nightly.7' `
            -CurrentSourceRevision $sourceRevision `
            -CurrentPublishedAt ([DateTimeOffset]::Parse('2026-08-05T23:59:59Z'))
        Assert-True ($wrapperResult.CurrentSection.Contains(
                '- Fixed the first nightly regression.')) `
            'The file wrapper must load the tagged and source changelog snapshots.'
        Assert-True ($wrapperResult.CurrentSection.Contains(
                '## [0.1.0-nightly.7] - 2026-08-05')) `
            'Nightly dates must come from the canonical release publication timestamp.'
        Assert-True ($wrapperResult.Content.Contains(
                '- Preserved a change merged after the nightly source commit.')) `
            'The file wrapper must preserve post-source Unreleased entries.'
        $outputBytes = [IO.File]::ReadAllBytes($outputPath)
        $bareLfCount = 0
        for ($index = 0; $index -lt $outputBytes.Length; $index++) {
            if ($outputBytes[$index] -eq 10 -and
                ($index -eq 0 -or $outputBytes[$index - 1] -ne 13)) {
                $bareLfCount++
            }
        }
        Assert-Equal 0 $bareLfCount `
            'The file wrapper must preserve CRLF without mixed line endings.'

        [IO.File]::WriteAllText(
            $manifestPath,
            '[]',
            [Text.UTF8Encoding]::new($false))
        $versionBumpResult = Write-OpenBladeNightlyChangelogSnapshot `
            -RepositoryRoot $temporaryRoot `
            -ChangelogPath 'CHANGELOG.md' `
            -OutputPath 'generated\VERSION-BUMP.md' `
            -PublishedReleasesPath $manifestPath `
            -BaseVersion '0.2.0' `
            -BuildRevision 7 `
            -CurrentTag 'v0.2.0-nightly.7' `
            -CurrentSourceRevision $sourceRevision `
            -CurrentPublishedAt ([DateTimeOffset]::Parse('2026-08-05T23:59:59Z'))
        Assert-True ($versionBumpResult.CurrentSection.Contains(
                '## [0.2.0-nightly.7] - 2026-08-05')) `
            'The first nightly after a version bump must not require a base tag.'
        Assert-True ($versionBumpResult.Content.Contains(
                '[0.2.0-nightly.7]: https://github.com/OSSBlade/openblade-core/compare/v0.1.0-nightly.6...v0.2.0-nightly.7')) `
            'A version bump must retain the prior Unreleased comparison baseline.'
    }
    finally {
        Pop-Location
    }
}
finally {
    if (Test-Path -LiteralPath $temporaryRoot) {
        Remove-Item -LiteralPath $temporaryRoot -Recurse -Force
    }
}

Write-Host 'Nightly changelog tests passed.'
