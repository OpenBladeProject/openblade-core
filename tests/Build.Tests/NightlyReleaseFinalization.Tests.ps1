[CmdletBinding()]
param()

Set-StrictMode -Version 3.0
$ErrorActionPreference = 'Stop'
$repository = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)

function Assert-True {
    param([Parameter(Mandatory)][bool]$Condition, [Parameter(Mandatory)][string]$Message)
    if (-not $Condition) { throw $Message }
}

function Assert-ThrowsLike {
    param([Parameter(Mandatory)][scriptblock]$Action,
        [Parameter(Mandatory)][string]$Pattern, [Parameter(Mandatory)][string]$Message)
    try { & $Action }
    catch {
        if ($_.Exception.Message -notlike $Pattern) {
            throw "$Message Expected '$Pattern', received '$($_.Exception.Message)'."
        }
        return
    }
    throw "$Message Expected an exception."
}

. (Join-Path $repository 'build\NightlyChangelog.ps1')
. (Join-Path $repository 'build\Complete-NightlyRelease.ps1')
$gitApplication = (Get-Command git -CommandType Application | Select-Object -First 1).Path
$productionReleaseGh = (Get-Command Invoke-OpenBladeReleaseGh).ScriptBlock
$originalGitCommand = Get-Command git

function New-FinalizationFixture {
    param([switch]$MergeSnapshot, [switch]$MismatchedSnapshot, [switch]$ChecksumMismatch,
        [switch]$MissingAsset)

    $root = Join-Path ([IO.Path]::GetTempPath()) ('OpenBlade.NightlyRelease.Tests.' + [Guid]::NewGuid().ToString('N'))
    [IO.Directory]::CreateDirectory($root) | Out-Null
    $source = @'
# Changelog

## [Unreleased]

### Fixed

- Correct the nightly gate.

## [0.1.0] - 2026-01-01

### Added

- Initial release.

[Unreleased]: https://github.com/OSSBlade/openblade-core/compare/v0.1.0...HEAD
[0.1.0]: https://github.com/OSSBlade/openblade-core/releases/tag/v0.1.0
'@
    $baseline = $source.Replace('- Correct the nightly gate.', '')
    [IO.File]::WriteAllText((Join-Path $root 'CHANGELOG.md'), $baseline, [Text.UTF8Encoding]::new($false))
    & $gitApplication -C $root init -q
    & $gitApplication -C $root config user.email test@example.invalid
    & $gitApplication -C $root config user.name Test
    & $gitApplication -C $root add CHANGELOG.md
    & $gitApplication -C $root commit -qm 'test: baseline'
    $baselineSha = (& $gitApplication -C $root rev-parse HEAD).Trim()
    & $gitApplication -C $root tag v0.1.0 $baselineSha
    [IO.File]::WriteAllText((Join-Path $root 'CHANGELOG.md'), $source, [Text.UTF8Encoding]::new($false))
    & $gitApplication -C $root add CHANGELOG.md
    & $gitApplication -C $root commit -qm 'test: source'
    $sourceSha = (& $gitApplication -C $root rev-parse HEAD).Trim()
    $tag = 'v0.1.0-nightly.7'
    $createdAt = [DateTimeOffset]::Parse('2026-09-10T09:17:00Z')
    $history = @([pscustomobject]@{ tagName = 'v0.1.0'; publishedAt = '2026-01-01T00:00:00Z' })
    $apiHistory = @([pscustomobject]@{ tag_name = 'v0.1.0'; published_at = '2026-01-01T00:00:00Z'; draft = $false })
    $manifest = Join-Path $root 'history.json'
    [IO.File]::WriteAllText($manifest, ($history | ConvertTo-Json -Depth 4), [Text.UTF8Encoding]::new($false))
    $expectedPath = Join-Path $root 'expected.md'
    $expected = Write-OpenBladeNightlyChangelogSnapshot -RepositoryRoot $root `
        -ChangelogPath 'CHANGELOG.md' -OutputPath $expectedPath `
        -PublishedReleasesPath $manifest -BaseVersion '0.1.0' -BuildRevision 7 `
        -CurrentTag $tag -CurrentSourceRevision $sourceSha -CurrentPublishedAt $createdAt
    Assert-True ($expected.CurrentSection.Contains('- Correct the nightly gate.')) `
        'The fixture must generate a nightly section from the source-only changelog entry.'
    if ($MergeSnapshot) {
        [IO.File]::WriteAllText((Join-Path $root 'CHANGELOG.md'), $expected.Content, [Text.UTF8Encoding]::new($false))
        & $gitApplication -C $root add CHANGELOG.md
        & $gitApplication -C $root commit -qm 'docs: record nightly'
    }
    $mainSha = (& $gitApplication -C $root rev-parse HEAD).Trim()
    $installerBytes = [byte[]](1, 2, 3, 4)
    $sha256 = [Security.Cryptography.SHA256]::Create()
    try { $installerHash = ($sha256.ComputeHash($installerBytes) | ForEach-Object ToString x2) -join '' }
    finally { $sha256.Dispose() }
    $metadata = [ordered]@{
        channel = 'nightly'; sourceRevision = $sourceSha; baseVersion = '0.1.0'; buildRevision = 7
        informationalVersion = "0.1.0-nightly.7+$($sourceSha.Substring(0, 8))"
        installer = [ordered]@{ sha256 = $installerHash }
    } | ConvertTo-Json -Depth 5
    $snapshot = if ($MismatchedSnapshot) { $expected.Content + "`nTampered.`n" } else { $expected.Content }
    $checksum = if ($ChecksumMismatch) { ('0' * 64) + '  OpenBlade-Setup.exe' } else { "$installerHash  OpenBlade-Setup.exe" }
    $files = [ordered]@{
        'OpenBlade-Setup.exe' = $installerBytes
        'OpenBlade-Setup.exe.sha256' = [Text.Encoding]::UTF8.GetBytes($checksum)
        'OpenBlade-Nightly.json' = [Text.Encoding]::UTF8.GetBytes($metadata)
        'OpenBlade-Nightly-CHANGELOG.md' = [Text.Encoding]::UTF8.GetBytes($snapshot)
    }
    if ($MissingAsset) { [void]$files.Remove('OpenBlade-Nightly-CHANGELOG.md') }
    $assets = @($files.GetEnumerator() | ForEach-Object {
            [pscustomobject]@{ name = $_.Key; size = $_.Value.Length }
        })
    return [pscustomobject]@{
        Root = $root; SourceSha = $sourceSha; MainSha = $mainSha; Tag = $tag; CreatedAt = $createdAt
        Files = $files; History = $history; ApiHistory = $apiHistory; Release = [pscustomobject]@{
            tagName = $tag; targetCommitish = $sourceSha; isDraft = $true; isPrerelease = $true
            createdAt = $createdAt; assets = $assets; body = $expected.CurrentSection }
        ReleaseEditCalls = 0
    }
}

function Invoke-FinalizationWithFixture {
    param([Parameter(Mandatory)]$Fixture, [switch]$VerifyOnly)

    # Keep the guard and mocks in the caller scope of the production function.
    # Global mocks lose to its script-scoped adapter when this test runs with &.
    function Stop-ExternalFinalizationGh {
        throw 'The offline finalization fixture blocked an external gh call.'
    }
    Set-Alias -Name gh -Value Stop-ExternalFinalizationGh -Scope Local
    Assert-ThrowsLike { & $productionReleaseGh -Arguments @('release', 'view', $Fixture.Tag) } `
        '*offline finalization fixture blocked an external gh call*' `
        'The production adapter must fail closed if the fixture mock is bypassed.'

    function Invoke-OpenBladeReleaseGh {
        param([Parameter(Mandatory)][string[]]$Arguments)
        $command = $Arguments -join ' '
        if ($command -like 'release view*') {
            if ($Fixture.ReleaseEditCalls -gt 0) {
                $Fixture.Release.isDraft = $false
            }
            return ($Fixture.Release | ConvertTo-Json -Depth 6)
        }
        if ($command -like 'release download*') {
            $directory = $Arguments[[Array]::IndexOf($Arguments, '--dir') + 1]
            foreach ($file in $Fixture.Files.GetEnumerator()) {
                [IO.File]::WriteAllBytes((Join-Path $directory $file.Key), $file.Value)
            }
            return
        }
        if ($command -like 'api *') { return (@($Fixture.ApiHistory) | ConvertTo-Json -Depth 6) }
        if ($command -like 'release edit*') { $Fixture.ReleaseEditCalls++; return }
        throw "Unexpected mocked gh call: $command"
    }
    function git {
        param([Parameter(ValueFromRemainingArguments = $true)][string[]]$Arguments)
        if ($Arguments -contains 'fetch') { $global:LASTEXITCODE = 0; return }
        if ($Arguments -contains 'FETCH_HEAD') { $global:LASTEXITCODE = 0; return $Fixture.MainSha }
        if ($Arguments -contains 'merge-base') { $global:LASTEXITCODE = 0; return }
        & $gitApplication @Arguments
    }

    # Function/alias scope unwinds on both success and failure without changing
    # caller commands, including when the entire test has been dot-sourced.
    Complete-OpenBladeNightlyRelease -Tag $Fixture.Tag -RepositoryRoot $Fixture.Root -VerifyOnly:$VerifyOnly
}

function Update-MainFixtureChangelog {
    param([Parameter(Mandatory)]$Fixture, [Parameter(Mandatory)][scriptblock]$Transform)
    $path = Join-Path $Fixture.Root 'CHANGELOG.md'
    $content = [IO.File]::ReadAllText($path)
    $updated = & $Transform $content
    Assert-True ($updated -cne $content) 'The requested main-changelog mutation did not change its fixture.'
    [IO.File]::WriteAllText($path, $updated, [Text.UTF8Encoding]::new($false))
    & $gitApplication -C $Fixture.Root add CHANGELOG.md
    & $gitApplication -C $Fixture.Root commit -qm 'test: alter merged changelog'
    $Fixture.MainSha = (& $gitApplication -C $Fixture.Root rev-parse HEAD).Trim()
}

$missingMain = New-FinalizationFixture
Assert-ThrowsLike { Invoke-FinalizationWithFixture -Fixture $missingMain } `
    '*changelog is missing*' 'A draft must not publish before the generated changelog reaches main.'
Assert-True ($missingMain.ReleaseEditCalls -eq 0) 'A missing main section must prevent release edit.'

$alteredMain = New-FinalizationFixture -MergeSnapshot
Update-MainFixtureChangelog -Fixture $alteredMain -Transform {
    param($content) $content.Replace('- Correct the nightly gate.', '- Altered after generation.')
}
Assert-ThrowsLike { Invoke-FinalizationWithFixture -Fixture $alteredMain } `
    '*has not been merged unchanged*' 'A changed release section on main must block publication.'
Assert-True ($alteredMain.ReleaseEditCalls -eq 0) 'An altered main section must prevent release edit.'

$missingLink = New-FinalizationFixture -MergeSnapshot
Update-MainFixtureChangelog -Fixture $missingLink -Transform {
    param($content) $content -replace '(?m)^\[0\.1\.0-nightly\.7\]:[^\r\n]+\r?\n?', ''
}
Assert-ThrowsLike { Invoke-FinalizationWithFixture -Fixture $missingLink } `
    '*comparison link is missing from main*' 'A missing release comparison link must block publication.'
Assert-True ($missingLink.ReleaseEditCalls -eq 0) 'A missing comparison link must prevent release edit.'

$mismatched = New-FinalizationFixture -MergeSnapshot -MismatchedSnapshot
Assert-ThrowsLike { Invoke-FinalizationWithFixture -Fixture $mismatched } `
    '*not the exact generated source snapshot*' 'A changed attached snapshot must block publication.'
Assert-True ($mismatched.ReleaseEditCalls -eq 0) 'A mismatched snapshot must prevent release edit.'

$badChecksum = New-FinalizationFixture -MergeSnapshot -ChecksumMismatch
Assert-ThrowsLike { Invoke-FinalizationWithFixture -Fixture $badChecksum } `
    '*Installer checksum does not match*' 'A checksum mismatch must block publication.'
Assert-True ($badChecksum.ReleaseEditCalls -eq 0) 'A checksum mismatch must prevent release edit.'

$missingAsset = New-FinalizationFixture -MergeSnapshot -MissingAsset
Assert-ThrowsLike { Invoke-FinalizationWithFixture -Fixture $missingAsset } `
    '*draft must contain exactly four release assets*' 'Missing release assets must block publication.'
Assert-True ($missingAsset.ReleaseEditCalls -eq 0) 'A missing asset must prevent release edit.'

$success = New-FinalizationFixture -MergeSnapshot
Invoke-FinalizationWithFixture -Fixture $success -VerifyOnly
Assert-True ($success.ReleaseEditCalls -eq 0) 'VerifyOnly must retain the validated draft.'
Invoke-FinalizationWithFixture -Fixture $success
Assert-True ($success.ReleaseEditCalls -eq 1) 'A valid merged snapshot must publish exactly once.'

Assert-True ([Object]::ReferenceEquals((Get-Command Invoke-OpenBladeReleaseGh).ScriptBlock, $productionReleaseGh)) `
    'The fixture must preserve the imported production adapter after failed and successful calls.'
Assert-True ((Get-Command git).CommandType -eq $originalGitCommand.CommandType -and
    (Get-Command git).Definition -ceq $originalGitCommand.Definition) `
    'The fixture must preserve the caller git command after failed and successful calls.'

Write-Host 'Nightly release finalization tests passed.'
