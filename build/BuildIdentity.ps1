Set-StrictMode -Version 3.0
$ErrorActionPreference = 'Stop'

function Resolve-OpenBladeBuildIdentity {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$BaseVersion,
        [ValidateSet('Stable', 'Nightly')]
        [string]$Channel = 'Stable',
        [ValidateRange(0, 65535)]
        [int]$BuildRevision = 0,
        [string]$SourceRevision
    )

    $normalizedBaseVersion = $BaseVersion.Trim()
    if ($normalizedBaseVersion -notmatch '^\d+\.\d+\.\d+$') {
        throw "OpenBlade base version must contain exactly three numeric components: $BaseVersion"
    }

    $parsedBaseVersion = [Version]$normalizedBaseVersion
    foreach ($component in @(
            $parsedBaseVersion.Major,
            $parsedBaseVersion.Minor,
            $parsedBaseVersion.Build)) {
        if ($component -gt 65535) {
            throw "OpenBlade base version components must be between 0 and 65535: $BaseVersion"
        }
    }

    if ($Channel -eq 'Stable') {
        if ($BuildRevision -ne 0) {
            throw 'Stable builds must use build revision 0.'
        }

        return [pscustomobject]@{
            Channel = 'Stable'
            BaseVersion = $normalizedBaseVersion
            Version = $normalizedBaseVersion
            BundleVersion = $normalizedBaseVersion
            AssemblyVersion = "$normalizedBaseVersion.0"
            FileVersion = "$normalizedBaseVersion.0"
            InformationalVersion = $normalizedBaseVersion
            BuildRevision = 0
            SourceRevision = $null
        }
    }

    if ($BuildRevision -eq 0) {
        throw 'Nightly builds require a non-zero build revision.'
    }

    $normalizedSourceRevision = if ([string]::IsNullOrWhiteSpace($SourceRevision)) {
        ''
    }
    else {
        $SourceRevision.Trim().ToLowerInvariant()
    }
    if ($normalizedSourceRevision -notmatch '^[0-9a-f]{40}$') {
        throw 'Nightly builds require the full 40-character Git source revision.'
    }

    $shortSourceRevision = $normalizedSourceRevision.Substring(0, 8)
    $nightlyVersion = "$normalizedBaseVersion-nightly.$BuildRevision"
    $binaryVersion = "$normalizedBaseVersion.$BuildRevision"
    return [pscustomobject]@{
        Channel = 'Nightly'
        BaseVersion = $normalizedBaseVersion
        Version = $nightlyVersion
        BundleVersion = $binaryVersion
        AssemblyVersion = $binaryVersion
        FileVersion = $binaryVersion
        InformationalVersion = "$nightlyVersion+$shortSourceRevision"
        BuildRevision = $BuildRevision
        SourceRevision = $normalizedSourceRevision
    }
}

function Write-OpenBladeNightlyArtifactMetadata {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$Installer,
        [Parameter(Mandatory)][string]$MetadataPath,
        [Parameter(Mandatory)][string]$ChecksumPath,
        [Parameter(Mandatory)][psobject]$BuildIdentity,
        [string]$WorkflowRunId,
        [DateTimeOffset]$CreatedAtUtc = [DateTimeOffset]::UtcNow
    )

    if ($BuildIdentity.Channel -ne 'Nightly') {
        throw 'Nightly artifact metadata requires a Nightly build identity.'
    }
    if (-not (Test-Path -LiteralPath $Installer -PathType Leaf)) {
        throw "The nightly installer is missing: $Installer"
    }

    $installerHash = (Get-FileHash -LiteralPath $Installer -Algorithm SHA256).Hash.ToLowerInvariant()
    $signatureStatus = (Get-AuthenticodeSignature -LiteralPath $Installer).Status.ToString()
    $metadata = [ordered]@{
        schemaVersion = 1
        channel = 'nightly'
        baseVersion = $BuildIdentity.BaseVersion
        bundleVersion = $BuildIdentity.BundleVersion
        informationalVersion = $BuildIdentity.InformationalVersion
        buildRevision = $BuildIdentity.BuildRevision
        sourceRevision = $BuildIdentity.SourceRevision
        sourceUrl = "https://github.com/OSSBlade/openblade-core/commit/$($BuildIdentity.SourceRevision)"
        workflowRunId = if ([string]::IsNullOrWhiteSpace($WorkflowRunId)) {
            $null
        }
        else {
            $WorkflowRunId.Trim()
        }
        createdAtUtc = $CreatedAtUtc.ToUniversalTime().ToString('O')
        installer = [ordered]@{
            fileName = [IO.Path]::GetFileName($Installer)
            sha256 = $installerHash
            authenticodeStatus = $signatureStatus
        }
    }

    $utf8WithoutBom = [Text.UTF8Encoding]::new($false)
    $metadataJson = $metadata | ConvertTo-Json -Depth 4
    [IO.File]::WriteAllText(
        $MetadataPath,
        $metadataJson + [Environment]::NewLine,
        $utf8WithoutBom)
    [IO.File]::WriteAllText(
        $ChecksumPath,
        "$installerHash *$([IO.Path]::GetFileName($Installer))" + [Environment]::NewLine,
        [Text.Encoding]::ASCII)

    return $metadata
}
