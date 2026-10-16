[CmdletBinding()]
param()

Set-StrictMode -Version 3.0
$ErrorActionPreference = 'Stop'
$repository = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
. (Join-Path $repository 'build\InstallerPrerequisites.ps1')

function Assert-Rejected {
    param([scriptblock]$Action, [string]$Message)
    try { & $Action }
    catch {
        if ($_.Exception.Message.Contains($Message)) { return }
        throw
    }
    throw "Expected rejection containing '$Message'."
}

$prerequisite = Get-OpenBladeDotNetRuntimePrerequisite
[xml]$properties = Get-Content -LiteralPath (Join-Path $repository 'Directory.Build.props')
if ($prerequisite.Version -ne [string]$properties.Project.PropertyGroup.OpenBladeMinimumDotNetRuntimeVersion) {
    throw 'The runtime download must match the installed apps minimum version.'
}
$runtimeUri = [Uri]$prerequisite.Uri
if ($runtimeUri.Scheme -ne 'https' -or $runtimeUri.Host -ne 'builds.dotnet.microsoft.com' -or
    $runtimeUri.Segments[-1] -ne $prerequisite.FileName -or $prerequisite.Sha512 -notmatch '^[0-9a-f]{128}$') {
    throw 'The runtime prerequisite must pin a Microsoft HTTPS x64 installer and SHA-512.'
}

$testFile = Join-Path ([IO.Path]::GetTempPath()) ('OpenBlade.Prerequisite.' + [Guid]::NewGuid().ToString('N'))
try {
    [IO.File]::WriteAllBytes($testFile, [byte[]]@(1, 2, 3))
    Assert-Rejected { Assert-OpenBladeDotNetRuntimePrerequisite -Path $testFile } 'not the pinned Microsoft release'
    function Get-FileHash { return [pscustomobject]@{ Hash = $prerequisite.Sha512 } }
    foreach ($untrustedSignature in @(
            [pscustomobject]@{ Status = 'NotSigned'; SignerCertificate = $null },
            [pscustomobject]@{ Status = 'Valid'; SignerCertificate = [pscustomobject]@{ Subject = 'CN=Other Publisher,' } })) {
        function Get-AuthenticodeSignature { return $untrustedSignature }
        Assert-Rejected { Assert-OpenBladeDotNetRuntimePrerequisite -Path $testFile } 'valid Microsoft signature'
    }
    $trustedSignature = [pscustomobject]@{
        Status = 'Valid'
        SignerCertificate = [pscustomobject]@{ Subject = 'CN=.NET, O=Microsoft Corporation, L=Redmond, S=Washington, C=US' }
    }
    function Get-AuthenticodeSignature { return $trustedSignature }
    Assert-OpenBladeDotNetRuntimePrerequisite -Path $testFile
}
finally {
    Remove-Item Function:\Get-FileHash -ErrorAction SilentlyContinue
    Remove-Item Function:\Get-AuthenticodeSignature -ErrorAction SilentlyContinue
    Remove-Item -LiteralPath $testFile -ErrorAction SilentlyContinue
}
Write-Host 'Installer prerequisite integrity tests passed.'

# Exercise cache behavior offline with real file hashes and a trusted-signature stub.
$cacheTestRoot = Join-Path ([IO.Path]::GetTempPath()) ('OpenBlade.RuntimeCache.' + [Guid]::NewGuid().ToString('N'))
$cacheTestDirectory = Join-Path $cacheTestRoot 'cache'
$script:runtimeDownloadCount = 0
$script:runtimeDownloadFailure = $false
$script:runtimeBadDownloadSignature = $false
$script:runtimePublishCompetitor = $false
$script:runtimeFixture = [byte[]]@(1, 2, 3)
$fixtureFile = Join-Path $cacheTestRoot 'fixture.exe'
try {
    New-Item -ItemType Directory -Path $cacheTestRoot -Force | Out-Null
    [IO.File]::WriteAllBytes($fixtureFile, $script:runtimeFixture)
    $prerequisite.Sha512 = (Get-FileHash -LiteralPath $fixtureFile -Algorithm SHA512).Hash.ToLowerInvariant()
    function Get-OpenBladeDotNetRuntimePrerequisite { return $prerequisite }
    function Get-AuthenticodeSignature {
        param($LiteralPath, $FilePath)
        if ($script:runtimeBadDownloadSignature) {
            return [pscustomobject]@{ Status = 'NotSigned'; SignerCertificate = $null }
        }
        return $trustedSignature
    }
    function Invoke-WebRequest {
        param($Uri, $OutFile)
        $script:runtimeDownloadCount++
        [IO.File]::WriteAllBytes($OutFile, $script:runtimeFixture)
        if ($script:runtimeDownloadFailure) { throw 'Simulated interrupted download' }
        if ($script:runtimePublishCompetitor) {
            [IO.File]::WriteAllBytes($cached, $script:runtimeFixture)
        }
    }
    function Copy-TestRuntime([string]$Name, [string]$Source = '') {
        Copy-OpenBladeDotNetRuntime -SourcePath $Source -CacheDirectory $cacheTestDirectory -DestinationDirectory (Join-Path $cacheTestRoot $Name)
    }
    function Assert-DownloadCount([int]$Expected) {
        if ($script:runtimeDownloadCount -ne $Expected) {
            throw "Expected $Expected downloads, got $script:runtimeDownloadCount."
        }
    }

    $first = Copy-TestRuntime 'first-worktree'
    Assert-DownloadCount 1
    $second = Copy-TestRuntime 'second-worktree'
    Assert-DownloadCount 1
    if ((Get-FileHash -LiteralPath $second -Algorithm SHA512).Hash -ne $prerequisite.Sha512) {
        throw 'A cache hit must stage the verified installer.'
    }
    $cached = Join-Path (Join-Path $cacheTestDirectory $prerequisite.Sha512.Substring(0, 16)) $prerequisite.FileName
    [IO.File]::WriteAllBytes($cached, [byte[]]@(9))
    $null = Copy-TestRuntime 'repaired'
    Assert-DownloadCount 2
    $null = Copy-TestRuntime 'explicit' $fixtureFile
    Assert-DownloadCount 2
    [IO.File]::WriteAllBytes($fixtureFile, [byte[]]@(9))
    Assert-Rejected { Copy-TestRuntime 'bad-explicit' $fixtureFile } 'not the pinned Microsoft release'
    Assert-DownloadCount 2

    # A new pin gets a distinct entry. Interrupted/untrusted downloads never publish.
    $previousCached = $cached
    $script:runtimeFixture = [byte[]]@(4, 5, 6)
    [IO.File]::WriteAllBytes($fixtureFile, $script:runtimeFixture)
    $prerequisite.Sha512 = (Get-FileHash -LiteralPath $fixtureFile -Algorithm SHA512).Hash.ToLowerInvariant()
    $cached = Join-Path (Join-Path $cacheTestDirectory $prerequisite.Sha512.Substring(0, 16)) $prerequisite.FileName
    $script:runtimeDownloadFailure = $true
    Assert-Rejected { Copy-TestRuntime 'interrupted' } 'Simulated interrupted download'
    if (Test-Path -LiteralPath $cached) { throw 'An interrupted download was published.' }
    $script:runtimeDownloadFailure = $false
    $script:runtimeBadDownloadSignature = $true
    Assert-Rejected { Copy-TestRuntime 'bad-signature' } 'valid Microsoft signature'
    if (Test-Path -LiteralPath $cached) { throw 'An unsigned download was published.' }
    $script:runtimeBadDownloadSignature = $false
    $null = Copy-TestRuntime 'new-pin'
    Assert-DownloadCount 5
    if (!(Test-Path -LiteralPath $previousCached) -or !(Test-Path -LiteralPath $cached)) {
        throw 'A pin change must use a separate cache entry.'
    }
    $script:runtimeBadDownloadSignature = $true
    Assert-Rejected { Copy-TestRuntime 'untrusted-hit' } 'valid Microsoft signature'
    Assert-DownloadCount 6
    $script:runtimeBadDownloadSignature = $false
    # Simulate another build publishing while this one downloads.
    Remove-Item -LiteralPath $cached
    $script:runtimePublishCompetitor = $true
    $null = Copy-TestRuntime 'concurrent-winner'
    Assert-DownloadCount 7
    $script:runtimePublishCompetitor = $false
    if (@(Get-ChildItem -LiteralPath $cacheTestDirectory -Recurse -Filter '*.download').Count -ne 0) {
        throw 'Temporary downloads were not cleaned up.'
    }
}
finally {
    Remove-Item Function:\Get-AuthenticodeSignature -ErrorAction SilentlyContinue
    Remove-Item Function:\Invoke-WebRequest -ErrorAction SilentlyContinue
    Remove-Item Function:\Copy-TestRuntime -ErrorAction SilentlyContinue
    Remove-Item Function:\Assert-DownloadCount -ErrorAction SilentlyContinue
    . (Join-Path $repository 'build\InstallerPrerequisites.ps1')
    if (Test-Path -LiteralPath $cacheTestRoot) { Remove-Item -LiteralPath $cacheTestRoot -Recurse -Force }
}
Write-Host 'Installer runtime cache tests passed.'