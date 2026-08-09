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

function Get-OpenBladeLatestRecordedNightlyRevision {
    [CmdletBinding()]
    param([Parameter(Mandatory)][string]$Content)

    $revisionMatches = [Regex]::Matches(
        (ConvertTo-OpenBladeNormalizedText -Text $Content),
        '(?m)^## \[(?:0|[1-9]\d*)\.(?:0|[1-9]\d*)\.(?:0|[1-9]\d*)-nightly\.(?<revision>\d+)\] - \d{4}-\d{2}-\d{2}$')
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
    param([Parameter(Mandatory)][string]$Content)

    $normalizedContent = ConvertTo-OpenBladeNormalizedText -Text $Content
    $referenceLines = [Regex]::Matches(
        $normalizedContent,
        '(?m)^\[Unreleased\]:.*$')
    $referenceMatches = [Regex]::Matches(
        $normalizedContent,
        '(?m)^\[Unreleased\]:\s+\S+/compare/(?<tag>\S+)\.\.\.HEAD$')
    if ($referenceLines.Count -ne 1 -or $referenceMatches.Count -ne 1) {
        throw (
            'The changelog [Unreleased] comparison reference is missing, ' +
            'malformed, or duplicated.')
    }

    return $referenceMatches[0].Groups['tag'].Value
}

function ConvertFrom-OpenBladeNightlyTag {
    [CmdletBinding()]
    param([Parameter(Mandatory)][string]$TagName)

    $match = [Regex]::Match(
        $TagName,
        '^v(?<base>(?:0|[1-9]\d*)\.(?:0|[1-9]\d*)\.(?:0|[1-9]\d*))-nightly\.(?<revision>[1-9]\d*)(?:-attempt\.(?<attempt>[1-9]\d*))?$')
    if (-not $match.Success) {
        return $null
    }

    $revision = 0
    if (-not [int]::TryParse($match.Groups['revision'].Value, [ref]$revision) -or
        $revision -gt 65535) {
        return $null
    }

    $attempt = 0
    if ($match.Groups['attempt'].Success -and
        -not [int]::TryParse($match.Groups['attempt'].Value, [ref]$attempt)) {
        return $null
    }

    $baseVersion = $match.Groups['base'].Value
    return [pscustomobject]@{
        BaseVersion = $baseVersion
        BuildRevision = $revision
        Version = "$baseVersion-nightly.$revision"
        Attempt = $attempt
    }
}

function ConvertTo-OpenBladeNightlyRecord {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]$InputObject,
        [Parameter(Mandatory)][string]$FallbackBaseVersion
    )

    $buildRevision = [int]$InputObject.BuildRevision
    $version = if ($null -ne $InputObject.PSObject.Properties['Version']) {
        [string]$InputObject.Version
    }
    else {
        "$FallbackBaseVersion-nightly.$buildRevision"
    }
    $tagName = [string]$InputObject.TagName
    $tagDetails = ConvertFrom-OpenBladeNightlyTag -TagName $tagName
    if ($null -ne $tagDetails -and
        ($tagDetails.BuildRevision -ne $buildRevision -or
            -not $tagDetails.Version.Equals(
                $version,
                [StringComparison]::Ordinal))) {
        throw "Nightly record '$tagName' does not match version '$version'."
    }

    return [pscustomobject]@{
        BuildRevision = $buildRevision
        Version = $version
        TagName = $tagName
        Attempt = if ($null -eq $tagDetails) { 0 } else { $tagDetails.Attempt }
        PublishedAt = [DateTimeOffset]$InputObject.PublishedAt
        SourceContent = [string]$InputObject.SourceContent
    }
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
        $normalizedRecords = [Collections.Generic.List[object]]::new()
        $versions = [Collections.Generic.HashSet[string]]::new(
            [StringComparer]::Ordinal)
        foreach ($item in $group.Group) {
            # BaseVersion supports direct callers whose older records predate
            # the explicit Version field used by the workflow wrapper.
            $normalized = ConvertTo-OpenBladeNightlyRecord `
                -InputObject $item `
                -FallbackBaseVersion $BaseVersion
            [void]$versions.Add($normalized.Version)
            $normalizedRecords.Add($normalized)
        }
        if ($versions.Count -ne 1) {
            $collidingTags = @(
                $normalizedRecords.TagName |
                    Sort-Object -Unique) -join ', '
            throw (
                "Nightly revision $($group.Name) appears under multiple " +
                "base versions: $collidingTags.")
        }

        # GitHub's workflow run number is global, so one revision may identify
        # attempts of one version only. Prefer its canonical tag when present.
        $version = @($versions)[0]
        $canonicalTag = "v$version"
        $canonicalRecords = @(
            $normalizedRecords | Where-Object TagName -EQ $canonicalTag)
        if ($canonicalRecords.Count -gt 0) {
            $record = $canonicalRecords[0]
        }
        else {
            $fallbackRecords = @(
                $normalizedRecords |
                    Sort-Object `
                        @{ Expression = { $_.PublishedAt }; Descending = $true }, `
                        @{ Expression = { $_.Attempt }; Descending = $true }, `
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

    $selectedRecords = @(Select-OpenBladeNightlyReleaseRecords `
            -ReleaseRecord $ReleaseRecord `
            -BaseVersion $BaseVersion)
    $baselineTag = Get-OpenBladeUnreleasedReferenceTag -Content $CurrentContent
    $baselineRecords = @(
        $ReleaseRecord |
            Where-Object TagName -EQ $baselineTag |
            Select-Object -First 1)
    if ($baselineRecords.Count -eq 0) {
        throw "Release history is missing the changelog baseline tag '$baselineTag'."
    }
    $baselineRecord = ConvertTo-OpenBladeNightlyRecord `
        -InputObject $baselineRecords[0] `
        -FallbackBaseVersion $BaseVersion

    $pendingRecords = @(
        $selectedRecords |
            Where-Object {
                $_.BuildRevision -gt $baselineRecord.BuildRevision -and
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
        $previousEntryTexts = [Collections.Generic.HashSet[string]]::new(
            [StringComparer]::Ordinal)
        foreach ($entry in $previousEntries) {
            [void]$previousEntryTexts.Add($entry.Text)
        }

        $sourceEntries = @(Get-OpenBladeChangelogEntries `
                -Content $record.SourceContent)
        $releasedEntries = @(
            $sourceEntries |
                Where-Object { -not $previousEntryTexts.Contains($_.Text) })
        $version = $record.Version
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
        $output = @(& git show "$Revision`:$Path" 2>$null)
        if ($LASTEXITCODE -ne 0) {
            throw "Unable to read $Path at revision '$Revision'."
        }
        return ($output -join "`n") + "`n"
    }
    finally {
        Pop-Location
    }
}

function Resolve-OpenBladeChangelogBaseline {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$RepositoryRoot,
        [Parameter(Mandatory)][string]$CurrentContent,
        [Parameter(Mandatory)]
        [ValidateRange(0, 65535)]
        [int]$LatestRecordedRevision
    )

    $tagName = Get-OpenBladeUnreleasedReferenceTag -Content $CurrentContent
    $tagDetails = ConvertFrom-OpenBladeNightlyTag -TagName $tagName
    if ($null -eq $tagDetails) {
        if ($tagName -notmatch '^v(?:0|[1-9]\d*)\.(?:0|[1-9]\d*)\.(?:0|[1-9]\d*)$') {
            throw (
                "The [Unreleased] comparison reference names unsupported tag " +
                "'$tagName'.")
        }
        $buildRevision = $LatestRecordedRevision
        $version = $tagName.Substring(1)
        $attempt = 0
        $kind = 'Stable'
    }
    else {
        $buildRevision = $tagDetails.BuildRevision
        $version = $tagDetails.Version
        $attempt = $tagDetails.Attempt
        $kind = 'Nightly'
    }

    try {
        $sourceContent = Get-OpenBladeGitFileContent `
            -RepositoryRoot $RepositoryRoot `
            -Revision $tagName
    }
    catch {
        throw (
            "The changelog baseline tag '$tagName' is not available locally. " +
            'Fetch the referenced tag before reconstruction; using an empty ' +
            'baseline would replay published entries.')
    }

    return [pscustomobject]@{
        BuildRevision = $buildRevision
        Version = $version
        TagName = $tagName
        Attempt = $attempt
        PublishedAt = [DateTimeOffset]::MinValue
        SourceContent = $sourceContent
        Kind = $kind
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

    $currentTagDetails = ConvertFrom-OpenBladeNightlyTag -TagName $CurrentTag
    if ($null -eq $currentTagDetails -or
        $currentTagDetails.BuildRevision -ne $BuildRevision -or
        -not $currentTagDetails.BaseVersion.Equals(
            $BaseVersion,
            [StringComparison]::Ordinal)) {
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
    $latestRecordedRevision = Get-OpenBladeLatestRecordedNightlyRevision `
        -Content $currentContent
    $baseline = Resolve-OpenBladeChangelogBaseline `
        -RepositoryRoot $RepositoryRoot `
        -CurrentContent $currentContent `
        -LatestRecordedRevision $latestRecordedRevision
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
        if ([string]::Equals(
                $tagName,
                $CurrentTag,
                [StringComparison]::Ordinal)) {
            continue
        }
        $publishedAt = if ($null -ne $item.PSObject.Properties['publishedAt']) {
            [DateTimeOffset]$item.publishedAt
        }
        else {
            [DateTimeOffset]$item.published_at
        }
        $tagDetails = ConvertFrom-OpenBladeNightlyTag -TagName $tagName
        if ($null -eq $tagDetails) {
            continue
        }
        $revision = $tagDetails.BuildRevision
        if ($revision -gt $BuildRevision) {
            continue
        }
        if ($baseline.Kind -eq 'Nightly') {
            if ($revision -lt $baseline.BuildRevision) {
                continue
            }
        }
        elseif ($revision -le $baseline.BuildRevision) {
            continue
        }
        $releaseRecords.Add([pscustomobject]@{
                BuildRevision = $revision
                Version = $tagDetails.Version
                TagName = $tagName
                PublishedAt = $publishedAt
                SourceContent = Get-OpenBladeGitFileContent `
                    -RepositoryRoot $RepositoryRoot `
                    -Revision $tagName
            })
    }

    if (@($releaseRecords | Where-Object TagName -EQ $baseline.TagName).Count -eq 0) {
        $releaseRecords.Add($baseline)
    }

    $releaseRecords.Add([pscustomobject]@{
            BuildRevision = $BuildRevision
            Version = "$BaseVersion-nightly.$BuildRevision"
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
