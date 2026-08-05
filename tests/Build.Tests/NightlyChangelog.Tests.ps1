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

function Assert-Throws {
    param(
        [Parameter(Mandatory)][scriptblock]$Action,
        [Parameter(Mandatory)][string]$ExpectedMessage
    )

    try {
        & $Action
    }
    catch {
        Assert-True ($_.Exception.Message -eq $ExpectedMessage) `
            "Unexpected error message '$($_.Exception.Message)'."
        return
    }

    throw "Expected failure '$ExpectedMessage'."
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
        '[0.1.0-nightly.8]: https://github.com/OSSBlade/openblade-core/compare/v0.1.0-nightly.7-attempt.2...v0.1.0-nightly.8')) `
    'Nightly comparisons must retain the actual successful attempt tag.'
Assert-True ($result.Content.Contains(
        '[0.1.0-nightly.7]: https://github.com/OSSBlade/openblade-core/compare/v0.1.0-nightly.6...v0.1.0-nightly.7-attempt.2')) `
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
Assert-Throws `
    {
        New-OpenBladeNightlyChangelogContent `
            -CurrentContent $missingReleasedEntry `
            -BaseVersion '0.1.0' `
            -CurrentBuildRevision 8 `
            -ReleaseRecord $releaseRecords
    } `
    'Released entry is missing from the current Unreleased section: - Fixed the first nightly regression.'

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
            -CurrentPublishedAt ([DateTimeOffset]::Parse('2026-08-05T09:17:00Z'))
        Assert-True ($wrapperResult.CurrentSection.Contains(
                '- Fixed the first nightly regression.')) `
            'The file wrapper must load the tagged and source changelog snapshots.'
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
