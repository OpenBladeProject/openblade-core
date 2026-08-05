Set-StrictMode -Version 3.0
$ErrorActionPreference = 'Stop'

function ConvertTo-OpenBladeNormalizedText {
    [CmdletBinding()]
    param([Parameter(Mandatory)][AllowEmptyString()][string]$Text)

    return $Text.Replace("`r`n", "`n").Replace("`r", "`n")
}

function Get-OpenBladeChangelogNewLine {
    [CmdletBinding()]
    param([Parameter(Mandatory)][AllowEmptyString()][string]$Text)

    if ($Text.IndexOf("`r`n", [StringComparison]::Ordinal) -ge 0) {
        return "`r`n"
    }

    return "`n"
}

function Get-OpenBladeChangelogSection {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$Content,
        [Parameter(Mandatory)][string]$Heading
    )

    $normalized = ConvertTo-OpenBladeNormalizedText -Text $Content
    $lines = @($normalized -split "`n")
    $start = [Array]::IndexOf($lines, $Heading)
    if ($start -lt 0) {
        throw "The changelog is missing '$Heading'."
    }

    $end = $lines.Count
    for ($index = $start + 1; $index -lt $lines.Count; $index++) {
        if ($lines[$index].StartsWith('## ', [StringComparison]::Ordinal)) {
            $end = $index
            break
        }
    }

    return [pscustomobject]@{
        Lines = $lines
        Start = $start
        End = $end
        Content = ($lines[$start..($end - 1)] -join "`n")
    }
}

function Get-OpenBladeChangelogEntries {
    [CmdletBinding()]
    param([Parameter(Mandatory)][string]$Content)

    $section = Get-OpenBladeChangelogSection `
        -Content $Content `
        -Heading '## [Unreleased]'
    $sectionLines = @($section.Content -split "`n")
    $entries = [Collections.Generic.List[object]]::new()
    $category = $null
    $entryState = [pscustomobject]@{ Lines = $null }

    function Add-CurrentEntry {
        if ($null -eq $entryState.Lines) {
            return
        }

        while ($entryState.Lines.Count -gt 0 -and
            [string]::IsNullOrWhiteSpace(
                $entryState.Lines[$entryState.Lines.Count - 1])) {
            $entryState.Lines.RemoveAt($entryState.Lines.Count - 1)
        }
        $entryText = $entryState.Lines -join "`n"
        $entries.Add([pscustomobject]@{
                Category = $category
                Text = $entryText
                Key = "$category`n$entryText"
            })
        $entryState.Lines = $null
    }

    $bodyLines = if ($sectionLines.Count -gt 1) {
        @($sectionLines[1..($sectionLines.Count - 1)])
    }
    else {
        @()
    }
    foreach ($line in $bodyLines) {
        if ($line.StartsWith('### ', [StringComparison]::Ordinal)) {
            Add-CurrentEntry
            $category = $line.Substring(4).Trim()
            if ([string]::IsNullOrWhiteSpace($category)) {
                throw 'An Unreleased changelog category is empty.'
            }
            continue
        }

        if ($line.StartsWith('- ', [StringComparison]::Ordinal)) {
            Add-CurrentEntry
            if ([string]::IsNullOrWhiteSpace($category)) {
                throw 'An Unreleased changelog entry has no category.'
            }
            $entryState.Lines = [Collections.Generic.List[string]]::new()
            $entryState.Lines.Add($line)
            continue
        }

        if ($null -ne $entryState.Lines) {
            $entryState.Lines.Add($line)
            continue
        }

        if ([string]::IsNullOrWhiteSpace($line)) {
            continue
        }

        throw "Unsupported content in the Unreleased changelog section: $line"
    }
    Add-CurrentEntry

    return @($entries)
}

function Get-OpenBladeRecordedNightlyRevision {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$Content,
        [Parameter(Mandatory)][string]$BaseVersion
    )

    $escapedBaseVersion = [Regex]::Escape($BaseVersion)
    $revisionMatches = [Regex]::Matches(
        (ConvertTo-OpenBladeNormalizedText -Text $Content),
        "(?m)^## \[$escapedBaseVersion-nightly\.(?<revision>\d+)\] - \d{4}-\d{2}-\d{2}$")
    if ($revisionMatches.Count -eq 0) {
        return 0
    }

    return [int](
        $revisionMatches |
            ForEach-Object { [int]$_.Groups['revision'].Value } |
            Measure-Object -Maximum).Maximum
}

function Get-OpenBladeUnreleasedReferenceTag {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$Content,
        [Parameter(Mandatory)][string]$FallbackVersion
    )

    $referenceMatch = [Regex]::Match(
        (ConvertTo-OpenBladeNormalizedText -Text $Content),
        '(?m)^\[Unreleased\]:\s+\S+/compare/(?<tag>\S+)\.\.\.HEAD$')
    if ($referenceMatch.Success) {
        return $referenceMatch.Groups['tag'].Value
    }

    return "v$FallbackVersion"
}

function Get-OpenBladeChangelogReferenceTag {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$Content,
        [Parameter(Mandatory)][string]$Version
    )

    $escapedVersion = [Regex]::Escape($Version)
    $match = [Regex]::Match(
        (ConvertTo-OpenBladeNormalizedText -Text $Content),
        "(?m)^\[$escapedVersion\]:\s+(?<url>\S+)$")
    if (-not $match.Success) {
        return "v$Version"
    }

    $url = $match.Groups['url'].Value
    if ($url.IndexOf('...', [StringComparison]::Ordinal) -ge 0) {
        return ($url -split '\.\.\.')[-1]
    }
    if ($url.IndexOf('/releases/tag/', [StringComparison]::Ordinal) -ge 0) {
        return ($url -split '/releases/tag/')[-1]
    }

    return "v$Version"
}

function New-OpenBladeChangelogSectionLines {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$Heading,
        [Parameter(Mandatory)][AllowEmptyCollection()][object[]]$Entry,
        [string]$EmptyMessage
    )

    $lines = [Collections.Generic.List[string]]::new()
    $lines.Add($Heading)
    if ($Entry.Count -eq 0) {
        if (-not [string]::IsNullOrWhiteSpace($EmptyMessage)) {
            $lines.Add('')
            $lines.Add($EmptyMessage)
        }
        return @($lines)
    }

    $categoryOrder = [Collections.Generic.List[string]]::new()
    foreach ($item in $Entry) {
        if (-not $categoryOrder.Contains($item.Category)) {
            $categoryOrder.Add($item.Category)
        }
    }

    foreach ($category in $categoryOrder) {
        $lines.Add('')
        $lines.Add("### $category")
        $lines.Add('')
        foreach ($item in @($Entry | Where-Object Category -EQ $category)) {
            foreach ($entryLine in @($item.Text -split "`n")) {
                $lines.Add($entryLine)
            }
        }
    }

    return @($lines)
}

function Select-OpenBladeNightlyReleaseRecords {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][object[]]$ReleaseRecord,
        [Parameter(Mandatory)][string]$BaseVersion
    )

    $selected = [Collections.Generic.List[object]]::new()
    foreach ($group in @($ReleaseRecord | Group-Object BuildRevision)) {
        $canonicalTag = "v$BaseVersion-nightly.$($group.Name)"
        $canonicalRecords = @($group.Group | Where-Object TagName -EQ $canonicalTag)
        if ($canonicalRecords.Count -gt 0) {
            $record = $canonicalRecords[0]
        }
        else {
            $fallbackRecords = @(
                $group.Group |
                    Sort-Object `
                        @{ Expression = { $_.PublishedAt }; Descending = $true }, `
                        @{ Expression = {
                                if ($_.TagName -match '-attempt\.(?<attempt>\d+)$') {
                                    return [int]$Matches['attempt']
                                }
                                return 0
                            }; Descending = $true }, `
                        TagName |
                    Select-Object -First 1)
            $record = $fallbackRecords[0]
        }
        $selected.Add($record)
    }

    return @($selected | Sort-Object BuildRevision)
}

function New-OpenBladeNightlyChangelogContent {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$CurrentContent,
        [Parameter(Mandatory)][string]$BaseVersion,
        [Parameter(Mandatory)][ValidateRange(1, 65535)][int]$CurrentBuildRevision,
        [Parameter(Mandatory)][object[]]$ReleaseRecord,
        [string]$RepositoryUrl = 'https://github.com/OSSBlade/openblade-core'
    )

    $recordedRevision = Get-OpenBladeRecordedNightlyRevision `
        -Content $CurrentContent `
        -BaseVersion $BaseVersion
    $selectedRecords = @(Select-OpenBladeNightlyReleaseRecords `
            -ReleaseRecord $ReleaseRecord `
            -BaseVersion $BaseVersion)
    $baselineRecords = @(
        $selectedRecords |
            Where-Object BuildRevision -EQ $recordedRevision |
            Select-Object -First 1)
    if ($baselineRecords.Count -eq 0) {
        throw "Release history is missing the recorded changelog baseline revision $recordedRevision."
    }
    $baselineRecord = $baselineRecords[0]

    $pendingRecords = @(
        $selectedRecords |
            Where-Object {
                $_.BuildRevision -gt $recordedRevision -and
                $_.BuildRevision -le $CurrentBuildRevision
            } |
            Sort-Object BuildRevision)
    if ($pendingRecords.Count -eq 0) {
        $currentVersion = "$BaseVersion-nightly.$CurrentBuildRevision"
        $normalizedCurrent = ConvertTo-OpenBladeNormalizedText -Text $CurrentContent
        $headingMatch = [Regex]::Match(
            $normalizedCurrent,
            "(?m)^## \[$([Regex]::Escape($currentVersion))\] - \d{4}-\d{2}-\d{2}$")
        $currentSection = if ($headingMatch.Success) {
            (Get-OpenBladeChangelogSection `
                    -Content $normalizedCurrent `
                    -Heading $headingMatch.Value).Content.Trim()
        }
        else {
            $null
        }
        return [pscustomobject]@{
            Content = $CurrentContent
            CurrentSection = $currentSection
            PendingVersions = @()
        }
    }
    if ($pendingRecords[-1].BuildRevision -ne $CurrentBuildRevision) {
        throw "Release history is missing nightly revision $CurrentBuildRevision."
    }

    $previousEntries = @(Get-OpenBladeChangelogEntries `
            -Content $baselineRecord.SourceContent)
    $previousTag = $baselineRecord.TagName
    $releaseSections = [Collections.Generic.List[object]]::new()
    foreach ($record in $pendingRecords) {
        $previousKeys = [Collections.Generic.HashSet[string]]::new(
            [StringComparer]::Ordinal)
        foreach ($entry in $previousEntries) {
            [void]$previousKeys.Add($entry.Key)
        }

        $sourceEntries = @(Get-OpenBladeChangelogEntries `
                -Content $record.SourceContent)
        $releasedEntries = @(
            $sourceEntries |
                Where-Object { -not $previousKeys.Contains($_.Key) })
        $version = "$BaseVersion-nightly.$($record.BuildRevision)"
        $releaseDate = ([DateTimeOffset]$record.PublishedAt).
            ToUniversalTime().ToString('yyyy-MM-dd')
        $sectionLines = @(New-OpenBladeChangelogSectionLines `
                -Heading "## [$version] - $releaseDate" `
                -Entry $releasedEntries `
                -EmptyMessage 'No notable changelog entries were added since the previous nightly.')
        $releaseSections.Add([pscustomobject]@{
                Version = $version
                BuildRevision = $record.BuildRevision
                TagName = $record.TagName
                PreviousTag = $previousTag
                Entries = $releasedEntries
                Lines = $sectionLines
            })
        $previousEntries = $sourceEntries
        $previousTag = $record.TagName
    }

    $currentEntries = [Collections.Generic.List[object]]::new()
    foreach ($entry in @(Get-OpenBladeChangelogEntries -Content $CurrentContent)) {
        $currentEntries.Add($entry)
    }
    foreach ($section in $releaseSections) {
        foreach ($releasedEntry in $section.Entries) {
            $matchIndex = -1
            for ($index = 0; $index -lt $currentEntries.Count; $index++) {
                if ($currentEntries[$index].Key -eq $releasedEntry.Key) {
                    $matchIndex = $index
                    break
                }
            }
            if ($matchIndex -lt 0) {
                for ($index = 0; $index -lt $currentEntries.Count; $index++) {
                    if ($currentEntries[$index].Text -eq $releasedEntry.Text) {
                        $matchIndex = $index
                        break
                    }
                }
            }
            if ($matchIndex -lt 0) {
                continue
            }
            $currentEntries.RemoveAt($matchIndex)
        }
    }

    $normalized = ConvertTo-OpenBladeNormalizedText -Text $CurrentContent
    $unreleasedSection = Get-OpenBladeChangelogSection `
        -Content $normalized `
        -Heading '## [Unreleased]'
    $lines = @($normalized -split "`n")
    $before = if ($unreleasedSection.Start -eq 0) {
        @()
    }
    else {
        @($lines[0..($unreleasedSection.Start - 1)])
    }
    $after = if ($unreleasedSection.End -lt $lines.Count) {
        @($lines[$unreleasedSection.End..($lines.Count - 1)])
    }
    else {
        @()
    }
    $newLines = [Collections.Generic.List[string]]::new()
    foreach ($line in $before) { $newLines.Add($line) }
    foreach ($line in @(New-OpenBladeChangelogSectionLines `
                -Heading '## [Unreleased]' `
                -Entry @($currentEntries))) {
        $newLines.Add($line)
    }
    foreach ($section in @($releaseSections | Sort-Object BuildRevision -Descending)) {
        $newLines.Add('')
        foreach ($line in $section.Lines) { $newLines.Add($line) }
    }
    $newLines.Add('')
    foreach ($line in $after) { $newLines.Add($line) }

    $updated = ($newLines -join "`n").TrimEnd("`n") + "`n"
    $latestSection = @($releaseSections | Sort-Object BuildRevision -Descending)[0]
    $unreleasedUrl = "$RepositoryUrl/compare/$($latestSection.TagName)...HEAD"
    $referenceLines = [Collections.Generic.List[string]]::new()
    $referenceLines.Add("[Unreleased]: $unreleasedUrl")
    foreach ($section in @($releaseSections | Sort-Object BuildRevision -Descending)) {
        $referenceLines.Add(
            "[$($section.Version)]: $RepositoryUrl/compare/$($section.PreviousTag)...$($section.TagName)")
    }
    $referenceRegex = [Regex]::new(
        '^\[Unreleased\]:\s+\S+$',
        [Text.RegularExpressions.RegexOptions]::Multiline)
    if (-not $referenceRegex.IsMatch($updated)) {
        throw 'The changelog is missing its Unreleased comparison reference.'
    }
    $updated = $referenceRegex.Replace(
        $updated,
        ($referenceLines -join "`n"),
        1)

    $currentVersion = "$BaseVersion-nightly.$CurrentBuildRevision"
    $currentReleaseSections = @(
        $releaseSections |
            Where-Object Version -EQ $currentVersion |
            Select-Object -First 1)
    if ($currentReleaseSections.Count -eq 0) {
        throw "The generated changelog is missing nightly revision $CurrentBuildRevision."
    }
    $currentReleaseSection = $currentReleaseSections[0]
    $currentSection = Get-OpenBladeChangelogSection `
        -Content $updated `
        -Heading $currentReleaseSection.Lines[0]

    return [pscustomobject]@{
        Content = $updated
        CurrentSection = $currentSection.Content.Trim()
        PendingVersions = @($releaseSections.Version)
    }
}

function Get-OpenBladeGitFileContent {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$RepositoryRoot,
        [Parameter(Mandatory)][string]$Revision,
        [string]$Path = 'CHANGELOG.md'
    )

    Push-Location $RepositoryRoot
    try {
        $output = @(& git show "$Revision`:$Path")
        if ($LASTEXITCODE -ne 0) {
            throw "Unable to read $Path at revision '$Revision'."
        }
        return ($output -join "`n") + "`n"
    }
    finally {
        Pop-Location
    }
}

function Write-OpenBladeNightlyChangelogSnapshot {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$RepositoryRoot,
        [Parameter(Mandatory)][string]$ChangelogPath,
        [Parameter(Mandatory)][string]$OutputPath,
        [Parameter(Mandatory)][string]$PublishedReleasesPath,
        [Parameter(Mandatory)][string]$BaseVersion,
        [Parameter(Mandatory)][ValidateRange(1, 65535)][int]$BuildRevision,
        [Parameter(Mandatory)][string]$CurrentTag,
        [Parameter(Mandatory)][string]$CurrentSourceRevision,
        [Parameter(Mandatory)][DateTimeOffset]$CurrentPublishedAt,
        [string]$RepositoryUrl = 'https://github.com/OSSBlade/openblade-core'
    )

    if ($CurrentTag -notmatch "^v$([Regex]::Escape($BaseVersion))-nightly\.$BuildRevision(?:-attempt\.\d+)?$") {
        throw "Current nightly tag '$CurrentTag' does not match build revision $BuildRevision."
    }
    if ($CurrentSourceRevision -notmatch '^[0-9a-fA-F]{40}$') {
        throw 'The current nightly source revision must be a full Git commit SHA.'
    }

    $resolvedChangelogPath = if ([IO.Path]::IsPathRooted($ChangelogPath)) {
        $ChangelogPath
    }
    else {
        Join-Path $RepositoryRoot $ChangelogPath
    }
    $resolvedOutputPath = if ([IO.Path]::IsPathRooted($OutputPath)) {
        $OutputPath
    }
    else {
        Join-Path $RepositoryRoot $OutputPath
    }
    $resolvedPublishedReleasesPath = if (
        [IO.Path]::IsPathRooted($PublishedReleasesPath)) {
        $PublishedReleasesPath
    }
    else {
        Join-Path $RepositoryRoot $PublishedReleasesPath
    }

    $currentContent = [IO.File]::ReadAllText($resolvedChangelogPath)
    $newLine = Get-OpenBladeChangelogNewLine -Text $currentContent
    $recordedRevision = Get-OpenBladeRecordedNightlyRevision `
        -Content $currentContent `
        -BaseVersion $BaseVersion
    $rawManifest = Get-Content -LiteralPath $resolvedPublishedReleasesPath -Raw |
        ConvertFrom-Json
    $manifestItems = [Collections.Generic.List[object]]::new()
    foreach ($page in @($rawManifest)) {
        if ($page -is [Array]) {
            foreach ($item in $page) { $manifestItems.Add($item) }
        }
        else {
            $manifestItems.Add($page)
        }
    }

    $releaseRecords = [Collections.Generic.List[object]]::new()
    foreach ($item in $manifestItems) {
        $tagName = if ($null -ne $item.PSObject.Properties['tagName']) {
            [string]$item.tagName
        }
        else {
            [string]$item.tag_name
        }
        $publishedAt = if ($null -ne $item.PSObject.Properties['publishedAt']) {
            [DateTimeOffset]$item.publishedAt
        }
        else {
            [DateTimeOffset]$item.published_at
        }
        if ($tagName -notmatch "^v$([Regex]::Escape($BaseVersion))-nightly\.(?<revision>\d+)(?:-attempt\.\d+)?$") {
            continue
        }
        $revision = [int]$Matches['revision']
        if ($revision -lt $recordedRevision -or $revision -gt $BuildRevision) {
            continue
        }
        $releaseRecords.Add([pscustomobject]@{
                BuildRevision = $revision
                TagName = $tagName
                PublishedAt = $publishedAt
                SourceContent = Get-OpenBladeGitFileContent `
                    -RepositoryRoot $RepositoryRoot `
                    -Revision $tagName
            })
    }

    $baselineTag = if ($recordedRevision -eq 0) {
        Get-OpenBladeUnreleasedReferenceTag `
            -Content $currentContent `
            -FallbackVersion $BaseVersion
    }
    else {
        Get-OpenBladeChangelogReferenceTag `
            -Content $currentContent `
            -Version "$BaseVersion-nightly.$recordedRevision"
    }
    if (@($releaseRecords | Where-Object BuildRevision -EQ $recordedRevision).Count -eq 0) {
        $releaseRecords.Add([pscustomobject]@{
                BuildRevision = $recordedRevision
                TagName = $baselineTag
                PublishedAt = [DateTimeOffset]::MinValue
                SourceContent = if ($recordedRevision -eq 0) {
                    "# Changelog`n`n## [Unreleased]`n"
                }
                else {
                    Get-OpenBladeGitFileContent `
                        -RepositoryRoot $RepositoryRoot `
                        -Revision $baselineTag
                }
            })
    }

    $releaseRecords.Add([pscustomobject]@{
            BuildRevision = $BuildRevision
            TagName = $CurrentTag
            PublishedAt = $CurrentPublishedAt
            SourceContent = Get-OpenBladeGitFileContent `
                -RepositoryRoot $RepositoryRoot `
                -Revision $CurrentSourceRevision
        })
    $result = New-OpenBladeNightlyChangelogContent `
        -CurrentContent $currentContent `
        -BaseVersion $BaseVersion `
        -CurrentBuildRevision $BuildRevision `
        -ReleaseRecord @($releaseRecords) `
        -RepositoryUrl $RepositoryUrl

    $outputContent = (ConvertTo-OpenBladeNormalizedText -Text $result.Content).
        Replace("`n", $newLine)
    $outputDirectory = Split-Path -Parent $resolvedOutputPath
    if (-not [string]::IsNullOrWhiteSpace($outputDirectory)) {
        [IO.Directory]::CreateDirectory($outputDirectory) | Out-Null
    }
    [IO.File]::WriteAllText(
        $resolvedOutputPath,
        $outputContent,
        [Text.UTF8Encoding]::new($false))

    return [pscustomobject]@{
        Content = $outputContent
        CurrentSection = $result.CurrentSection
        PendingVersions = $result.PendingVersions
    }
}
