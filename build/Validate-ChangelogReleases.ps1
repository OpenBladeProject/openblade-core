#Requires -Version 5.1
<#
.SYNOPSIS
Fails when a published release has no section in CHANGELOG.md.

.DESCRIPTION
The Nightly workflow records each published prerelease in CHANGELOG.md through
its own pull request. A release published by hand skips that job, which leaves
the repository claiming the entries are still unreleased while the published
notes say otherwise. This validator closes that gap.

Released versions come from an explicit published-release document when one is
supplied, and otherwise from the repository's own tags. A shallow checkout
carries no tags, so this reports that it verified nothing rather than pretending
to pass; supply -PublishedReleasesPath to check in that environment.
#>
[CmdletBinding()]
param(
    [string]$Repository,
    [string]$ChangelogPath,
    [string]$PublishedReleasesPath
)

Set-StrictMode -Version 3.0
$ErrorActionPreference = 'Stop'

if ([string]::IsNullOrWhiteSpace($Repository)) {
    $Repository = Split-Path -Parent $PSScriptRoot
}
$Repository = [IO.Path]::GetFullPath($Repository)
if ([string]::IsNullOrWhiteSpace($ChangelogPath)) {
    $ChangelogPath = Join-Path $Repository 'CHANGELOG.md'
}
if (-not (Test-Path -LiteralPath $ChangelogPath -PathType Leaf)) {
    throw "The changelog is missing: $ChangelogPath"
}

. (Join-Path $PSScriptRoot 'NightlyChangelog.ps1')

function Get-OpenBladeReleasedTagName {
    param(
        [Parameter(Mandatory)][string]$Repository,
        [string]$PublishedReleasesPath
    )

    if (-not [string]::IsNullOrWhiteSpace($PublishedReleasesPath)) {
        if (-not (Test-Path -LiteralPath $PublishedReleasesPath -PathType Leaf)) {
            throw "The published release document is missing: $PublishedReleasesPath"
        }
        $text = [IO.File]::ReadAllText(
            $PublishedReleasesPath,
            [Text.UTF8Encoding]::new($false, $true))
        try {
            $document = $text | ConvertFrom-Json
        }
        catch {
            throw "The published release document is not valid JSON: $PublishedReleasesPath"
        }
        return @(
            foreach ($entry in @($document)) {
                if ($entry -is [string]) { $entry }
                elseif ($null -ne $entry -and $entry.PSObject.Properties['tagName']) {
                    [string]$entry.tagName
                }
                else {
                    throw 'Each published release must be a tag name or carry a tagName property.'
                }
            }
        )
    }

    $tags = @(& git -C $Repository tag --list)
    if ($LASTEXITCODE -ne 0) {
        throw 'Unable to read the repository tags.'
    }
    return $tags
}

$normalizedChangelog = ConvertTo-OpenBladeNormalizedText -Text (
    [IO.File]::ReadAllText(
        $ChangelogPath,
        [Text.UTF8Encoding]::new($false, $true)))
$changelogLines = @($normalizedChangelog -split "`n")

$recordedVersions = [Collections.Generic.HashSet[string]]::new(
    [StringComparer]::Ordinal)
$linkedVersions = [Collections.Generic.HashSet[string]]::new(
    [StringComparer]::Ordinal)
foreach ($line in $changelogLines) {
    $heading = [regex]::Match(
        $line,
        '^## \[(?<version>[^\]]+)\] - \d{4}-\d{2}-\d{2}$')
    if ($heading.Success) {
        [void]$recordedVersions.Add($heading.Groups['version'].Value)
        continue
    }
    $link = [regex]::Match($line, '^\[(?<version>[^\]]+)\]:\s*\S+$')
    if ($link.Success) {
        [void]$linkedVersions.Add($link.Groups['version'].Value)
    }
}

$releasedVersions = [ordered]@{}
foreach ($tagName in Get-OpenBladeReleasedTagName `
        -Repository $Repository `
        -PublishedReleasesPath $PublishedReleasesPath) {
    if ([string]::IsNullOrWhiteSpace($tagName)) { continue }
    $parsed = ConvertFrom-OpenBladeNightlyTag -TagName $tagName.Trim()
    if ($null -eq $parsed) { continue }
    if (-not $releasedVersions.Contains($parsed.Version)) {
        $releasedVersions[$parsed.Version] = $tagName.Trim()
    }
}

$missingSection = @(
    foreach ($version in $releasedVersions.Keys) {
        if (-not $recordedVersions.Contains($version)) { $version }
    }
)
$missingLink = @(
    foreach ($version in $releasedVersions.Keys) {
        if ($recordedVersions.Contains($version) -and
            -not $linkedVersions.Contains($version)) {
            $version
        }
    }
)

if ($missingSection.Count -gt 0) {
    $list = ($missingSection | Sort-Object) -join ', '
    throw (
        "CHANGELOG.md does not record these published releases: $list. " +
        'Regenerate it with Write-OpenBladeNightlyChangelogSnapshot from ' +
        'build/NightlyChangelog.ps1, or let the Nightly workflow changelog job run.')
}

if ($missingLink.Count -gt 0) {
    $list = ($missingLink | Sort-Object) -join ', '
    throw "CHANGELOG.md records these releases without a comparison link: $list."
}

if ($releasedVersions.Count -eq 0) {
    Write-Output (
        'Changelog release check verified nothing: no release tags are present. ' +
        'A shallow checkout carries no tags; pass -PublishedReleasesPath to check here.')
    return
}

Write-Output (
    "Changelog releases passed: $($releasedVersions.Count) published " +
    'releases are each recorded with a comparison link.')
