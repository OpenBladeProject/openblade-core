Set-StrictMode -Version 3.0
$ErrorActionPreference = 'Stop'

function Resolve-OpenBladeBuildIdentity {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$BaseVersion,
        [ValidateSet('Stable', 'Nightly', 'LocalDev')]
        [string]$Channel = 'Stable',
        [ValidateRange(0, 65535)]
        [int]$BuildRevision = 0,
        [string]$LocalDevVersion,
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

        if (-not [string]::IsNullOrWhiteSpace($LocalDevVersion)) {
            throw 'Stable builds must not specify a local-development version.'
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

    $normalizedSourceRevision = if ([string]::IsNullOrWhiteSpace($SourceRevision)) {
        ''
    }
    else {
        $SourceRevision.Trim().ToLowerInvariant()
    }

    if ($Channel -eq 'LocalDev') {
        if ($BuildRevision -ne 0) {
            throw 'Local-development builds use their reserved four-part version instead of a build revision.'
        }
        if ($normalizedSourceRevision -notmatch '^[0-9a-f]{40}$') {
            throw 'Local-development builds require the full 40-character Git source revision.'
        }
        if ([string]::IsNullOrWhiteSpace($LocalDevVersion) -or
            $LocalDevVersion -notmatch '^65535\.(?:0|[1-9][0-9]*)\.(?:0|[1-9][0-9]*)\.(?:0|[1-9][0-9]*)$') {
            throw 'Local-development builds require a canonical reserved 65535.x.y.z version.'
        }

        $parsedLocalDevVersion = [Version]$LocalDevVersion
        foreach ($component in @(
                $parsedLocalDevVersion.Minor,
                $parsedLocalDevVersion.Build,
                $parsedLocalDevVersion.Revision)) {
            if ($component -lt 0 -or $component -ge 65535) {
                throw 'Local-development version components after 65535 must be between 0 and 65534.'
            }
        }

        return [pscustomobject]@{
            Channel = 'LocalDev'
            BaseVersion = $normalizedBaseVersion
            Version = $LocalDevVersion
            BundleVersion = $LocalDevVersion
            AssemblyVersion = $LocalDevVersion
            FileVersion = $LocalDevVersion
            InformationalVersion = "$LocalDevVersion+$normalizedSourceRevision"
            BuildRevision = 0
            SourceRevision = $normalizedSourceRevision
        }
    }

    if ($BuildRevision -eq 0) {
        throw 'Nightly builds require a non-zero build revision.'
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
