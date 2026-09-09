#Requires -Version 5.1
[CmdletBinding()]
param(
    [string]$Tag,
    [string]$RepositoryRoot = (Split-Path -Parent $PSScriptRoot),
    [switch]$VerifyOnly
)

Set-StrictMode -Version 3.0
$ErrorActionPreference = 'Stop'

function Invoke-OpenBladeReleaseGh {
    param([Parameter(Mandatory)][string[]]$Arguments)
    $output = & gh @Arguments
    if ($LASTEXITCODE -ne 0) { throw "GitHub release operation failed: $($Arguments[0])" }
    return $output
}

function Complete-OpenBladeNightlyRelease {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$Tag,
        [Parameter(Mandatory)][string]$RepositoryRoot,
        [switch]$VerifyOnly
    )

    . (Join-Path $PSScriptRoot 'NightlyChangelog.ps1')
    $details = ConvertFrom-OpenBladeNightlyTag -TagName $Tag
    if ($null -eq $details) { throw 'An exact nightly release tag is required.' }
    $repo = 'OSSBlade/openblade-core'
    $release = (Invoke-OpenBladeReleaseGh -Arguments @('release', 'view', $Tag,
        '--repo', $repo, '--json',
        'tagName,targetCommitish,isDraft,isPrerelease,createdAt,assets,body')) | ConvertFrom-Json
    if (-not $release.isDraft -or -not $release.isPrerelease -or
        $release.tagName -cne $Tag -or
        $release.targetCommitish -notmatch '^[0-9a-f]{40}$') {
        throw 'Finalization requires an exact-SHA nightly draft.'
    }

    # Retain readback evidence on failure, including after an uncertain publish.
    $evidence = Join-Path ([IO.Path]::GetTempPath()) ('ob-release-gate-' + [Guid]::NewGuid().ToString('N'))
    [void][IO.Directory]::CreateDirectory($evidence)
    Write-Host "Release gate evidence: $evidence"
    $names = @('OpenBlade-Setup.exe', 'OpenBlade-Setup.exe.sha256',
        'OpenBlade-Nightly.json', 'OpenBlade-Nightly-CHANGELOG.md')
    if (@($release.assets).Count -ne $names.Count) { throw 'The draft must contain exactly four release assets.' }
    foreach ($name in $names) {
        if (@($release.assets | Where-Object name -CEQ $name).Count -ne 1) {
            throw "Missing or duplicate draft asset: $name"
        }
    }
    Invoke-OpenBladeReleaseGh -Arguments @('release', 'download', $Tag,
        '--repo', $repo, '--dir', $evidence) | Out-Null
    foreach ($asset in $release.assets) {
        $file = Get-Item -LiteralPath (Join-Path $evidence $asset.name)
        if ($file.Length -le 0 -or $file.Length -ne $asset.size) { throw "Asset size mismatch: $($asset.name)" }
    }
    $metadata = Get-Content -LiteralPath (Join-Path $evidence 'OpenBlade-Nightly.json') -Raw | ConvertFrom-Json
    if ($metadata.channel -cne 'nightly' -or
        $metadata.sourceRevision -cne $release.targetCommitish -or
        $metadata.baseVersion -cne $details.BaseVersion -or
        $metadata.buildRevision -ne $details.BuildRevision -or
        $metadata.informationalVersion -cne "$($details.Version)+$($metadata.sourceRevision.Substring(0, 8))") {
        throw 'The downloaded metadata does not match the draft identity.'
    }
    $installer = Join-Path $evidence 'OpenBlade-Setup.exe'
    $hash = (Get-FileHash -LiteralPath $installer -Algorithm SHA256).Hash
    $checksum = [IO.File]::ReadAllText((Join-Path $evidence 'OpenBlade-Setup.exe.sha256')).Trim()
    if ($hash -ine $metadata.installer.sha256 -or
        $checksum -notmatch ('(?i)^' + $hash + '\s+\*?OpenBlade-Setup\.exe$')) {
        throw 'Installer checksum does not match downloaded metadata and checksum asset.'
    }

    $pages = (Invoke-OpenBladeReleaseGh -Arguments @('api', '--paginate', '--slurp',
        "repos/$repo/releases?per_page=100")) | ConvertFrom-Json
    $history = @(
        foreach ($page in $pages) {
            foreach ($item in $page) {
                if (-not $item.draft) {
                    [pscustomobject]@{ tagName = $item.tag_name; publishedAt = $item.published_at }
                }
            }
        }
    )
    if (@($history | Where-Object tagName -CEQ $Tag).Count -ne 0) { throw 'The release is already published.' }
    $manifest = Join-Path $evidence 'releases.json'
    [IO.File]::WriteAllText($manifest, (ConvertTo-Json -InputObject @($history) -Depth 4))
    & git -C $RepositoryRoot fetch "https://github.com/$repo.git" main --tags
    if ($LASTEXITCODE -ne 0) { throw 'Unable to refresh canonical main and release tags.' }
    $mainSha = & git -C $RepositoryRoot rev-parse FETCH_HEAD
    if ($LASTEXITCODE -ne 0) { throw 'Unable to resolve current main.' }
    & git -C $RepositoryRoot merge-base --is-ancestor $metadata.sourceRevision $mainSha
    if ($LASTEXITCODE -ne 0) { throw 'The draft source is not contained in current main.' }
    $existingTag = & git -C $RepositoryRoot rev-parse --verify --quiet "refs/tags/$Tag^{commit}"
    if ($LASTEXITCODE -eq 0 -and $existingTag -cne $metadata.sourceRevision) {
        throw 'The existing release tag points to a different source commit.'
    }
    $mainContent = Get-OpenBladeGitFileContent -RepositoryRoot $RepositoryRoot -Revision $mainSha
    $mainPath = Join-Path $evidence 'main-CHANGELOG.md'
    [IO.File]::WriteAllText($mainPath, $mainContent)
    $sourcePath = Join-Path $evidence 'source-CHANGELOG.md'
    [IO.File]::WriteAllText($sourcePath, (Get-OpenBladeGitFileContent `
        -RepositoryRoot $RepositoryRoot -Revision $metadata.sourceRevision))
    $expected = Write-OpenBladeNightlyChangelogSnapshot `
        -RepositoryRoot $RepositoryRoot -ChangelogPath $sourcePath `
        -OutputPath (Join-Path $evidence 'expected-CHANGELOG.md') `
        -PublishedReleasesPath $manifest -BaseVersion $metadata.baseVersion `
        -BuildRevision $metadata.buildRevision -CurrentTag $Tag `
        -CurrentSourceRevision $metadata.sourceRevision `
        -CurrentPublishedAt ([DateTimeOffset]$release.createdAt)
    $snapshot = [IO.File]::ReadAllText((Join-Path $evidence 'OpenBlade-Nightly-CHANGELOG.md'))
    if ((ConvertTo-OpenBladeNormalizedText $snapshot) -cne
        (ConvertTo-OpenBladeNormalizedText $expected.Content)) {
        throw 'The attached changelog is not the exact generated source snapshot.'
    }
    if (-not (ConvertTo-OpenBladeNormalizedText $release.body).Contains($expected.CurrentSection)) {
        throw 'The draft release notes are missing the generated changelog section.'
    }
    $heading = "## [$($details.Version)] - $(([DateTimeOffset]$release.createdAt).UtcDateTime.ToString('yyyy-MM-dd'))"
    $mainSection = Get-OpenBladeChangelogSection -Content $mainContent -Heading $heading
    $snapshotSection = Get-OpenBladeChangelogSection -Content $snapshot -Heading $heading
    if ($mainSection.Content.Trim() -cne $snapshotSection.Content.Trim()) {
        throw 'The release changelog section has not been merged unchanged into main.'
    }
    $linkPattern = '(?m)^\[' + [Regex]::Escape($details.Version) + '\]:[^\r\n]+$'
    $expectedLink = [Regex]::Match((ConvertTo-OpenBladeNormalizedText $snapshot), $linkPattern).Value
    $mainLink = [Regex]::Match((ConvertTo-OpenBladeNormalizedText $mainContent), $linkPattern).Value
    if ([string]::IsNullOrWhiteSpace($expectedLink) -or
        $mainLink -cne $expectedLink) {
        throw 'The exact release comparison link is missing from main.'
    }
    [IO.File]::WriteAllText($manifest, (ConvertTo-Json -InputObject @($history +
        [pscustomobject]@{ tagName = $Tag; publishedAt = $release.createdAt }) -Depth 4))
    & (Join-Path $PSScriptRoot 'Validate-ChangelogReleases.ps1') `
        -Repository $RepositoryRoot -ChangelogPath $mainPath -PublishedReleasesPath $manifest
    if ($VerifyOnly) { Write-Host 'Nightly publication gate passed; draft retained.'; return }
    Invoke-OpenBladeReleaseGh -Arguments @('release', 'edit', $Tag, '--repo', $repo,
        '--draft=false', '--latest=false') | Out-Null
    $published = (Invoke-OpenBladeReleaseGh -Arguments @('release', 'view', $Tag,
        '--repo', $repo, '--json', 'isDraft,isPrerelease,tagName')) | ConvertFrom-Json
    if ($published.isDraft -or -not $published.isPrerelease -or $published.tagName -cne $Tag) {
        throw 'Publication readback failed; inspect remote state before retrying.'
    }
    Write-Host "Published $Tag after verifying changelog on main $mainSha."
}

if ($MyInvocation.InvocationName -ne '.') {
    Complete-OpenBladeNightlyRelease -Tag $Tag -RepositoryRoot $RepositoryRoot -VerifyOnly:$VerifyOnly
}
