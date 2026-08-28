[CmdletBinding()]
param()

Set-StrictMode -Version 3.0
$ErrorActionPreference = 'Stop'
$repository = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
. (Join-Path $repository 'build\BuildIdentity.ps1')

function Assert-True {
    param(
        [Parameter(Mandatory)][bool]$Condition,
        [Parameter(Mandatory)][string]$Message
    )

    if (-not $Condition) {
        throw $Message
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

$stable = Resolve-OpenBladeBuildIdentity -BaseVersion '0.1.0'
Assert-True ($stable.Channel -eq 'Stable') 'Stable channel was not preserved.'
Assert-True ($stable.BundleVersion -eq '0.1.0') `
    'Stable bundle versions must retain the three-part product version.'
Assert-True ($stable.AssemblyVersion -eq '0.1.0.0') `
    'Stable assembly versions must retain revision zero.'
Assert-True ($stable.InformationalVersion -eq '0.1.0') `
    'Stable informational versions must retain the product version.'

$sourceRevision = '0123456789abcdef0123456789abcdef01234567'
$nightly = Resolve-OpenBladeBuildIdentity `
    -BaseVersion '0.1.0' `
    -Channel Nightly `
    -BuildRevision 42 `
    -SourceRevision $sourceRevision
Assert-True ($nightly.BundleVersion -eq '0.1.0.42') `
    'Nightly bundle versions must use the ordered fourth component.'
Assert-True ($nightly.AssemblyVersion -eq '0.1.0.42') `
    'Nightly assembly versions must match the Burn ownership marker.'
Assert-True ($nightly.InformationalVersion -eq '0.1.0-nightly.42+01234567') `
    'Nightly informational versions must identify the channel, run, and commit.'

$localDev = Resolve-OpenBladeBuildIdentity `
    -BaseVersion '0.1.0' `
    -Channel LocalDev `
    -LocalDevVersion '65535.1.2.3' `
    -SourceRevision $sourceRevision
Assert-True ($localDev.BundleVersion -eq '65535.1.2.3') `
    'Local-development bundles must use the reserved monotonic version.'
Assert-True ($localDev.AssemblyVersion -eq '65534.1.2.3') `
    'Local-development assembly versions must use the valid shadow range.'
Assert-True ($localDev.FileVersion -eq '65534.1.2.3') `
    'Local-development file versions must use the valid shadow range.'
Assert-True ($localDev.InformationalVersion -eq "65535.1.2.3+$sourceRevision") `
    'Local-development product identity must bind the exact commit.'

Assert-Throws `
    { Resolve-OpenBladeBuildIdentity -BaseVersion '0.1.0' -BuildRevision 1 } `
    'Stable builds must use build revision 0.'
Assert-Throws `
    { Resolve-OpenBladeBuildIdentity -BaseVersion '0.1.0' -Channel Nightly } `
    'Nightly builds require a non-zero build revision.'
Assert-Throws `
    {
        Resolve-OpenBladeBuildIdentity `
            -BaseVersion '0.1.0' `
            -Channel Nightly `
            -BuildRevision 1 `
            -SourceRevision 'abcdef12'
    } `
    'Nightly builds require the full 40-character Git source revision.'
Assert-Throws `
    {
        Resolve-OpenBladeBuildIdentity `
            -BaseVersion '0.1.0' `
            -Channel LocalDev `
            -LocalDevVersion '0.1.0.1' `
            -SourceRevision $sourceRevision
    } `
    'Local-development builds require a canonical reserved 65535.x.y.z version.'
Assert-Throws `
    {
        Resolve-OpenBladeBuildIdentity `
            -BaseVersion '0.1.0' `
            -Channel LocalDev `
            -LocalDevVersion '65535.1.65535.0' `
            -SourceRevision $sourceRevision
    } `
    'Local-development version components after 65535 must be between 0 and 65534.'
Assert-Throws `
    { Resolve-OpenBladeBuildIdentity -BaseVersion '0.1.0.1' } `
    'OpenBlade base version must contain exactly three numeric components: 0.1.0.1'

$temporaryRoot = Join-Path ([IO.Path]::GetTempPath()) (
    'openblade-nightly-identity-' + [Guid]::NewGuid().ToString('N'))
try {
    New-Item -ItemType Directory -Path $temporaryRoot | Out-Null
    $installer = Join-Path $temporaryRoot 'OpenBlade-Setup.exe'
    [IO.File]::WriteAllBytes($installer, [byte[]](1, 2, 3, 4))
    $metadataPath = Join-Path $temporaryRoot 'OpenBlade-Nightly.json'
    $checksumPath = Join-Path $temporaryRoot 'OpenBlade-Setup.exe.sha256'
    $createdAt = [DateTimeOffset]::Parse('2026-08-02T09:17:00Z')
    $metadata = Write-OpenBladeNightlyArtifactMetadata `
        -Installer $installer `
        -MetadataPath $metadataPath `
        -ChecksumPath $checksumPath `
        -BuildIdentity $nightly `
        -WorkflowRunId '12345' `
        -CreatedAtUtc $createdAt

    Assert-True (Test-Path -LiteralPath $metadataPath -PathType Leaf) `
        'Nightly metadata was not written.'
    Assert-True (Test-Path -LiteralPath $checksumPath -PathType Leaf) `
        'Nightly checksum was not written.'
    $parsedMetadata = Get-Content -LiteralPath $metadataPath -Raw | ConvertFrom-Json
    Assert-True ($parsedMetadata.channel -eq 'nightly') `
        'Nightly metadata has the wrong channel.'
    Assert-True ($parsedMetadata.bundleVersion -eq '0.1.0.42') `
        'Nightly metadata has the wrong bundle version.'
    Assert-True ($parsedMetadata.sourceRevision -eq $sourceRevision) `
        'Nightly metadata has the wrong source revision.'
    Assert-True ($parsedMetadata.workflowRunId -eq '12345') `
        'Nightly metadata has the wrong workflow run.'
    Assert-True ($parsedMetadata.createdAtUtc -eq '2026-08-02T09:17:00.0000000+00:00') `
        'Nightly metadata has the wrong creation time.'
    Assert-True (-not [string]::IsNullOrWhiteSpace(
            $parsedMetadata.installer.authenticodeStatus)) `
        'Nightly metadata must report the installer Authenticode status.'
    Assert-True (
        (Get-Content -LiteralPath $checksumPath -Raw).Trim() -eq
            "$($metadata.installer.sha256) *OpenBlade-Setup.exe") `
        'Nightly checksum does not match the metadata.'
}
finally {
    if (Test-Path -LiteralPath $temporaryRoot) {
        Remove-Item -LiteralPath $temporaryRoot -Recurse -Force
    }
}

Write-Host 'Build identity tests passed.'
