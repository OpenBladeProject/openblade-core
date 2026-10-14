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
}
finally {
    Remove-Item Function:\Get-FileHash -ErrorAction SilentlyContinue
    Remove-Item Function:\Get-AuthenticodeSignature -ErrorAction SilentlyContinue
    Remove-Item -LiteralPath $testFile -ErrorAction SilentlyContinue
}
Write-Host 'Installer prerequisite integrity tests passed.'
