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
        if ($_.Exception.Message -notlike $Pattern) {
            throw (
                "$Message Expected an error like '$Pattern', received " +
                "'$($_.Exception.Message)'.")
        }
        return
    }

    throw "$Message Expected an error like '$Pattern', but no error was thrown."
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

$parsedNightly = ConvertFrom-OpenBladeNightlyTag `
    -TagName 'v1.2.3-nightly.1'
Assert-Equal '1.2.3' $parsedNightly.BaseVersion `
    'The nightly parser must retain the canonical base version.'
Assert-Equal 1 $parsedNightly.BuildRevision `
    'The nightly parser must retain the build revision.'
Assert-Equal '1.2.3-nightly.1' $parsedNightly.Version `
    'The nightly parser must produce the canonical version.'
Assert-Equal 0 $parsedNightly.Attempt `
    'A canonical nightly tag must have attempt zero.'
$parsedAttempt = ConvertFrom-OpenBladeNightlyTag `
    -TagName 'v1.2.3-nightly.65535-attempt.3'
Assert-Equal 65535 $parsedAttempt.BuildRevision `
    'The nightly parser must accept the maximum build revision.'
Assert-Equal 3 $parsedAttempt.Attempt `
    'The nightly parser must expose the attempt suffix.'
foreach ($invalidTag in @(
        'v1.2.3',
        'v01.2.3-nightly.1',
        'v1.2.3-nightly.0',
        'v1.2.3-nightly.65536',
        'v1.2.3-nightly.01',
        'v1.2.3-nightly.1-attempt.03')) {
    Assert-True ($null -eq (
            ConvertFrom-OpenBladeNightlyTag -TagName $invalidTag)) `
        "The nightly parser must reject noncanonical tag '$invalidTag'."
}
Assert-ThrowsLike `
    -Action {
        Write-OpenBladeNightlyChangelogSnapshot `
            -RepositoryRoot $repository `
            -ChangelogPath 'unused-changelog.md' `
            -OutputPath 'unused-output.md' `
            -PublishedReleasesPath 'unused-releases.json' `
            -BaseVersion '1.2.3' `
            -BuildRevision 1 `
            -CurrentTag 'v1.2.3-nightly.1-attempt.03' `
            -CurrentSourceRevision ('0' * 40) `
            -CurrentPublishedAt ([DateTimeOffset]::MinValue) | Out-Null
    } `
    -Pattern '*does not match build revision 1*' `
    -Message 'The wrapper must reject a noncanonical current nightly tag.'

$collisionRecords = @(
    [pscustomobject]@{
        BuildRevision = 8
        Version = '0.1.0-nightly.8'
        TagName = 'v0.1.0-nightly.8'
        PublishedAt = [DateTimeOffset]::Parse('2026-08-06T09:17:00Z')
        SourceContent = $nightly8Source
    },
    [pscustomobject]@{
        BuildRevision = 8
        Version = '0.2.0-nightly.8'
        TagName = 'v0.2.0-nightly.8'
        PublishedAt = [DateTimeOffset]::Parse('2026-08-06T10:17:00Z')
        SourceContent = $nightly8Source
    })
Assert-ThrowsLike `
    -Action {
        Select-OpenBladeNightlyReleaseRecords `
            -ReleaseRecord $collisionRecords `
            -BaseVersion '0.2.0' | Out-Null
    } `
    -Pattern '*v0.1.0-nightly.8*v0.2.0-nightly.8*' `
    -Message 'A global nightly revision must not identify multiple versions.'

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

$recategorizedNightly8Source = (
    ConvertTo-OpenBladeNormalizedText -Text $nightly8Source).Replace(
    "### Fixed`n`n- Fixed the first nightly regression.`n`n### Added",
    "### Changed`n`n- Fixed the first nightly regression.`n`n### Added")
$recategorizedReleaseRecords = @(
    foreach ($record in $releaseRecords) {
        if ($record.BuildRevision -eq 8) {
            [pscustomobject]@{
                BuildRevision = $record.BuildRevision
                TagName = $record.TagName
                PublishedAt = $record.PublishedAt
                SourceContent = $recategorizedNightly8Source
            }
        }
        else {
            $record
        }
    })
$recategorizedResult = New-OpenBladeNightlyChangelogContent `
    -CurrentContent $movedReleasedEntry `
    -BaseVersion '0.1.0' `
    -CurrentBuildRevision 8 `
    -ReleaseRecord $recategorizedReleaseRecords
$recategorizedNightly8 = Get-OpenBladeChangelogSection `
    -Content $recategorizedResult.Content `
    -Heading '## [0.1.0-nightly.8] - 2026-08-06'
Assert-True (-not $recategorizedNightly8.Content.Contains(
        '- Fixed the first nightly regression.')) `
    'A category-only move between nightly sources must not release the bullet twice.'
Assert-Equal 1 ([Regex]::Matches(
        $recategorizedResult.Content,
        [Regex]::Escape('- Fixed the first nightly regression.')).Count) `
    'A category-only move must remain recorded under only its first nightly.'

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
            ('[{"tagName":"v0.1.0-nightly.6",' +
                '"publishedAt":"2026-08-04T11:47:22Z"},' +
                '{"tagName":"v0.1.0-nightly.7",' +
                '"publishedAt":"2026-08-05T23:59:59Z"}]'),
            [Text.UTF8Encoding]::new($false))
        Assert-Equal 0 @(& git tag --list 'v0.1.0-nightly.7').Count `
            'The attachment regression must model a just-created tag absent locally.'
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
        $emptyManifestResult = Write-OpenBladeNightlyChangelogSnapshot `
            -RepositoryRoot $temporaryRoot `
            -ChangelogPath $changelogPath `
            -OutputPath 'generated\EMPTY-MANIFEST.md' `
            -PublishedReleasesPath $manifestPath `
            -BaseVersion '0.2.0' `
            -BuildRevision 7 `
            -CurrentTag 'v0.2.0-nightly.7' `
            -CurrentSourceRevision $sourceRevision `
            -CurrentPublishedAt ([DateTimeOffset]::Parse('2026-08-05T23:59:59Z'))
        $normalizedEmptyManifest = ConvertTo-OpenBladeNormalizedText `
            -Text $emptyManifestResult.Content
        $emptyManifestSection = Get-OpenBladeChangelogSection `
            -Content $normalizedEmptyManifest `
            -Heading '## [0.2.0-nightly.7] - 2026-08-05'
        Assert-Equal (
            "## [0.2.0-nightly.7] - 2026-08-05`n`n" +
            "### Fixed`n`n- Fixed the first nightly regression.`n") `
            $emptyManifestSection.Content `
            'An empty manifest must still diff from the exact old-base baseline.'
        Assert-True (-not $emptyManifestSection.Content.Contains(
                '- Already shipped before automation.')) `
            'An empty manifest must not replay entries published by the baseline.'
        Assert-Equal 1 ([Regex]::Matches(
                $normalizedEmptyManifest,
                '(?m)^\[0\.2\.0-nightly\.7\]: .+/compare/' +
                    'v0\.1\.0-nightly\.6\.\.\.v0\.2\.0-nightly\.7$')).Count `
            'The empty-manifest nightly must compare from the exact baseline tag.'

        $missingReferenceContent = [Regex]::Replace(
            $crlfLatestMain,
            '(?m)^\[Unreleased\]:.*(?:\r?\n|$)',
            '')
        Assert-ThrowsLike `
            -Action {
                Get-OpenBladeUnreleasedReferenceTag `
                    -Content $missingReferenceContent | Out-Null
            } `
            -Pattern '*Unreleased*missing*malformed*' `
            -Message 'A missing Unreleased reference must fail deliberately.'
        $malformedReferenceContent = $crlfLatestMain.Replace(
            'v0.1.0-nightly.6...HEAD',
            'v0.1.0-nightly.6..HEAD')
        Assert-ThrowsLike `
            -Action {
                Get-OpenBladeUnreleasedReferenceTag `
                    -Content $malformedReferenceContent | Out-Null
            } `
            -Pattern '*Unreleased*missing*malformed*' `
            -Message 'A malformed Unreleased reference must fail deliberately.'

        $missingTagContent = $crlfLatestMain.Replace(
            'v0.1.0-nightly.6...HEAD',
            'v0.1.0-nightly.5...HEAD')
        [IO.File]::WriteAllText(
            $changelogPath,
            $missingTagContent,
            [Text.UTF8Encoding]::new($false))
        $missingTagOutput = Join-Path $temporaryRoot 'generated\MISSING-TAG.md'
        Assert-ThrowsLike `
            -Action {
                Write-OpenBladeNightlyChangelogSnapshot `
                    -RepositoryRoot $temporaryRoot `
                    -ChangelogPath $changelogPath `
                    -OutputPath $missingTagOutput `
                    -PublishedReleasesPath $manifestPath `
                    -BaseVersion '0.2.0' `
                    -BuildRevision 7 `
                    -CurrentTag 'v0.2.0-nightly.7' `
                    -CurrentSourceRevision $sourceRevision `
                    -CurrentPublishedAt ([DateTimeOffset]::Parse(
                            '2026-08-05T23:59:59Z')) | Out-Null
            } `
            -Pattern "*baseline tag 'v0.1.0-nightly.5'*not available*" `
            -Message 'An unavailable baseline tag must stop reconstruction.'
        Assert-True (-not (Test-Path -LiteralPath $missingTagOutput)) `
            'A missing baseline tag must not create a changelog snapshot.'
        [IO.File]::WriteAllText(
            $changelogPath,
            $crlfLatestMain,
            [Text.UTF8Encoding]::new($false))

        & git tag v0.1.0-nightly.7-attempt.2 $sourceRevision
        $crlfNightly8 = (ConvertTo-OpenBladeNormalizedText -Text $nightly8Source).
            Replace("`n", "`r`n")
        [IO.File]::WriteAllText(
            $changelogPath,
            $crlfNightly8,
            [Text.UTF8Encoding]::new($false))
        & git add -- CHANGELOG.md
        & git commit --quiet -m 'first new-base nightly source'
        $newBaseSourceRevision = (& git rev-parse HEAD).Trim()
        [IO.File]::WriteAllText(
            $changelogPath,
            $crlfLatestMain,
            [Text.UTF8Encoding]::new($false))
        [IO.File]::WriteAllText(
            $manifestPath,
            ('[{"tagName":"v0.1.0-nightly.6",' +
                '"publishedAt":"2026-08-04T11:47:22Z"},' +
                '{"tagName":"v0.1.0-nightly.7-attempt.2",' +
                '"publishedAt":"2026-08-05T23:59:59Z"},' +
                '{"tagName":"v0.2.0-nightly.8",' +
                '"publishedAt":"2026-08-06T23:59:59Z"}]'),
            [Text.UTF8Encoding]::new($false))
        $versionBumpResult = Write-OpenBladeNightlyChangelogSnapshot `
            -RepositoryRoot $temporaryRoot `
            -ChangelogPath 'CHANGELOG.md' `
            -OutputPath 'generated\VERSION-BUMP.md' `
            -PublishedReleasesPath $manifestPath `
            -BaseVersion '0.2.0' `
            -BuildRevision 8 `
            -CurrentTag 'v0.2.0-nightly.8' `
            -CurrentSourceRevision $newBaseSourceRevision `
            -CurrentPublishedAt ([DateTimeOffset]::Parse('2026-08-06T23:59:59Z'))
        $normalizedVersionBump = ConvertTo-OpenBladeNormalizedText `
            -Text $versionBumpResult.Content
        $oldBaseSection = Get-OpenBladeChangelogSection `
            -Content $normalizedVersionBump `
            -Heading '## [0.1.0-nightly.7] - 2026-08-05'
        Assert-Equal (
            "## [0.1.0-nightly.7] - 2026-08-05`n`n" +
            "### Fixed`n`n- Fixed the first nightly regression.`n") `
            $oldBaseSection.Content `
            'An outstanding old-base nightly must retain its exact section.'
        $newBaseSection = Get-OpenBladeChangelogSection `
            -Content $normalizedVersionBump `
            -Heading '## [0.2.0-nightly.8] - 2026-08-06'
        Assert-Equal (
            "## [0.2.0-nightly.8] - 2026-08-06`n`n" +
            "### Added`n`n- Added the next nightly capability.`n") `
            $newBaseSection.Content `
            'The first new-base nightly must contain only its new entries.'
        $unreleasedSection = Get-OpenBladeChangelogSection `
            -Content $normalizedVersionBump `
            -Heading '## [Unreleased]'
        Assert-Equal (
            "## [Unreleased]`n`n### Changed`n`n" +
            "- Preserved a change merged after the nightly source commit.`n") `
            $unreleasedSection.Content `
            'Post-source changes must remain under Unreleased.'
        Assert-Equal '0.1.0-nightly.7,0.2.0-nightly.8' `
            ($versionBumpResult.PendingVersions -join ',') `
            'Pending sections must remain ordered across version bases.'
        foreach ($entry in @(
                '- Fixed the first nightly regression.',
                '- Added the next nightly capability.',
                '- Preserved a change merged after the nightly source commit.')) {
            Assert-Equal 1 ([Regex]::Matches(
                    $normalizedVersionBump,
                    [Regex]::Escape($entry))).Count `
                "The cross-base changelog must contain '$entry' exactly once."
        }
        foreach ($reference in @(
                @{
                    Key = 'Unreleased'
                    Line = '[Unreleased]: https://github.com/OSSBlade/openblade-core/compare/v0.2.0-nightly.8...HEAD'
                },
                @{
                    Key = '0.2.0-nightly.8'
                    Line = '[0.2.0-nightly.8]: https://github.com/OSSBlade/openblade-core/compare/v0.1.0-nightly.7-attempt.2...v0.2.0-nightly.8'
                },
                @{
                    Key = '0.1.0-nightly.7'
                    Line = '[0.1.0-nightly.7]: https://github.com/OSSBlade/openblade-core/compare/v0.1.0-nightly.6...v0.1.0-nightly.7-attempt.2'
                })) {
            Assert-Equal 1 ([Regex]::Matches(
                    $normalizedVersionBump,
                    "(?m)^$([Regex]::Escape($reference.Line))$")).Count `
                "The cross-base changelog must contain '$($reference.Line)' exactly once."
            Assert-Equal 1 ([Regex]::Matches(
                    $normalizedVersionBump,
                    "(?m)^\[$([Regex]::Escape($reference.Key))\]:\s+\S+$")).Count `
                "The cross-base changelog must define '$($reference.Key)' exactly once."
        }
        & git tag v0.2.0-nightly.8 $newBaseSourceRevision
        [IO.File]::WriteAllText(
            $changelogPath,
            $versionBumpResult.Content,
            [Text.UTF8Encoding]::new($false))
        $rerunOutput = Join-Path $temporaryRoot 'generated\VERSION-BUMP-RERUN.md'
        $versionBumpRerun = Write-OpenBladeNightlyChangelogSnapshot `
            -RepositoryRoot $temporaryRoot `
            -ChangelogPath $changelogPath `
            -OutputPath $rerunOutput `
            -PublishedReleasesPath $manifestPath `
            -BaseVersion '0.2.0' `
            -BuildRevision 8 `
            -CurrentTag 'v0.2.0-nightly.8' `
            -CurrentSourceRevision $newBaseSourceRevision `
            -CurrentPublishedAt ([DateTimeOffset]::Parse('2026-08-06T23:59:59Z'))
        Assert-Equal $versionBumpResult.Content $versionBumpRerun.Content `
            'Rerunning the cross-base reconstruction must be idempotent.'

        $stableSource = @'
# Changelog

## [Unreleased]

## [0.3.0] - 2026-08-06

### Added

- Added the stable release.

## [0.2.0-nightly.8] - 2026-08-06

### Added

- Added the next nightly capability.

## [0.1.0] - 2026-07-17

### Added

- Initial release.

[Unreleased]: https://github.com/OSSBlade/openblade-core/compare/v0.3.0...HEAD
[0.3.0]: https://github.com/OSSBlade/openblade-core/compare/v0.2.0-nightly.8...v0.3.0
[0.2.0-nightly.8]: https://github.com/OSSBlade/openblade-core/compare/v0.1.0-nightly.7-attempt.2...v0.2.0-nightly.8
[0.1.0]: https://github.com/OSSBlade/openblade-core/releases/tag/v0.1.0
'@
        $firstNightlySource = @'
# Changelog

## [Unreleased]

### Fixed

- Fixed the first post-stable nightly regression.

## [0.3.0] - 2026-08-06

### Added

- Added the stable release.

## [0.2.0-nightly.8] - 2026-08-06

### Added

- Added the next nightly capability.

## [0.1.0] - 2026-07-17

### Added

- Initial release.

[Unreleased]: https://github.com/OSSBlade/openblade-core/compare/v0.3.0...HEAD
[0.3.0]: https://github.com/OSSBlade/openblade-core/compare/v0.2.0-nightly.8...v0.3.0
[0.2.0-nightly.8]: https://github.com/OSSBlade/openblade-core/compare/v0.1.0-nightly.7-attempt.2...v0.2.0-nightly.8
[0.1.0]: https://github.com/OSSBlade/openblade-core/releases/tag/v0.1.0
'@
        [IO.File]::WriteAllText(
            $changelogPath,
            $stableSource,
            [Text.UTF8Encoding]::new($false))
        & git add -- CHANGELOG.md
        & git commit --quiet -m 'stable baseline source'
        & git tag v0.3.0
        [IO.File]::WriteAllText(
            $changelogPath,
            $firstNightlySource,
            [Text.UTF8Encoding]::new($false))
        & git add -- CHANGELOG.md
        & git commit --quiet -m 'post-stable nightly source'
        $firstNightlyRevision = (& git rev-parse HEAD).Trim()
        [IO.File]::WriteAllText(
            $manifestPath,
            '[]',
            [Text.UTF8Encoding]::new($false))
        $sameRevisionOutput = Join-Path `
            $temporaryRoot `
            'generated\STABLE-SAME-REVISION.md'
        $sameRevisionResult = Write-OpenBladeNightlyChangelogSnapshot `
            -RepositoryRoot $temporaryRoot `
            -ChangelogPath $changelogPath `
            -OutputPath $sameRevisionOutput `
            -PublishedReleasesPath $manifestPath `
            -BaseVersion '0.2.0' `
            -BuildRevision 8 `
            -CurrentTag 'v0.2.0-nightly.8-attempt.2' `
            -CurrentSourceRevision $newBaseSourceRevision `
            -CurrentPublishedAt ([DateTimeOffset]::Parse('2026-08-07T22:59:59Z'))
        Assert-Equal $firstNightlySource $sameRevisionResult.Content `
            'A stale nightly rerun at the stable watermark must be a no-op.'
        Assert-Equal 0 $sameRevisionResult.PendingVersions.Count `
            'A stale nightly rerun must not report pending releases.'
        Assert-Equal (
            "## [0.2.0-nightly.8] - 2026-08-06`n`n" +
            "### Added`n`n- Added the next nightly capability.") `
            $sameRevisionResult.CurrentSection `
            'A stale rerun must recover its retained nightly section.'
        Assert-Equal $firstNightlySource `
            ([IO.File]::ReadAllText($sameRevisionOutput)) `
            'A stale nightly rerun must write the unchanged changelog.'
        $normalizedSameRevision = ConvertTo-OpenBladeNormalizedText `
            -Text $sameRevisionResult.Content
        Assert-Equal 1 ([Regex]::Matches(
                $normalizedSameRevision,
                '(?m)^\[Unreleased\]: .+/compare/v0\.3\.0\.\.\.HEAD$')).Count `
            'A stale rerun must retain the stable Unreleased baseline.'
        Assert-Equal 1 ([Regex]::Matches(
                $normalizedSameRevision,
                [Regex]::Escape(
                    '- Fixed the first post-stable nightly regression.'))).Count `
            'A stale rerun must preserve the post-stable entry exactly once.'

        # The absent attempt tag is deliberate: releases at or below a stable
        # watermark must be filtered without requiring tag resolution.
        [IO.File]::WriteAllText(
            $manifestPath,
            ('[{"tagName":"v0.2.0-nightly.8-attempt.2",' +
                '"publishedAt":"2026-08-06T23:59:59Z"}]'),
            [Text.UTF8Encoding]::new($false))
        $stableBaselineResult = Write-OpenBladeNightlyChangelogSnapshot `
            -RepositoryRoot $temporaryRoot `
            -ChangelogPath $changelogPath `
            -OutputPath 'generated\STABLE-BASELINE.md' `
            -PublishedReleasesPath $manifestPath `
            -BaseVersion '0.4.0' `
            -BuildRevision 9 `
            -CurrentTag 'v0.4.0-nightly.9' `
            -CurrentSourceRevision $firstNightlyRevision `
            -CurrentPublishedAt ([DateTimeOffset]::Parse('2026-08-07T23:59:59Z'))
        $normalizedStableBaseline = ConvertTo-OpenBladeNormalizedText `
            -Text $stableBaselineResult.Content
        $stableBaselineSection = Get-OpenBladeChangelogSection `
            -Content $normalizedStableBaseline `
            -Heading '## [0.4.0-nightly.9] - 2026-08-07'
        Assert-Equal (
            "## [0.4.0-nightly.9] - 2026-08-07`n`n" +
            "### Fixed`n`n" +
            "- Fixed the first post-stable nightly regression.`n") `
            $stableBaselineSection.Content `
            'A stable baseline must produce the exact first nightly section.'
        Assert-Equal 1 ([Regex]::Matches(
                $normalizedStableBaseline,
                [Regex]::Escape(
                    '- Fixed the first post-stable nightly regression.'))).Count `
            'The first post-stable entry must be released exactly once.'
        $retainedSection = Get-OpenBladeChangelogSection `
            -Content $normalizedStableBaseline `
            -Heading '## [0.2.0-nightly.8] - 2026-08-06'
        Assert-Equal (
            "## [0.2.0-nightly.8] - 2026-08-06`n`n" +
            "### Added`n`n- Added the next nightly capability.`n") `
            $retainedSection.Content `
            'The stable watermark must retain the preceding nightly section.'
        Assert-Equal 1 ([Regex]::Matches(
                $normalizedStableBaseline,
                '(?m)^## \[0\.2\.0-nightly\.8\] - 2026-08-06$')).Count `
            'The preceding nightly section must occur exactly once.'
        Assert-Equal 1 ([Regex]::Matches(
                $normalizedStableBaseline,
                '(?m)^\[0\.4\.0-nightly\.9\]: .+/compare/' +
                    'v0\.3\.0\.\.\.v0\.4\.0-nightly\.9$')).Count `
            'The first post-stable nightly must compare from the stable tag.'
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
