Set-StrictMode -Version 3.0
$ErrorActionPreference = 'Stop'

$script:OpenBladeVcRedistUri =
    'https://aka.ms/vs/18/release/14.50.35719/VC_redist.x64.exe'
$script:OpenBladeVcRedistSha256 =
    '8995548DFFFCDE7C49987029C764355612BA6850EE09A7B6F0FDDC85BDC5C280'

function Get-OpenBladeDotNetRuntimePrerequisite {
    return [pscustomobject]@{
        Version = '10.0.12'
        FileName = 'windowsdesktop-runtime-10.0.12-win-x64.exe'
        Uri = 'https://builds.dotnet.microsoft.com/dotnet/WindowsDesktop/10.0.12/windowsdesktop-runtime-10.0.12-win-x64.exe'
        Sha512 = '0b907e9312867172a4eb82f4b5ab3f7c2d25e27d8349546d77eeb5d5b8cbecab9ebebfed189b13e9669dc578d892756c548366da539d47b3ddac5efcb7ae72fe'
    }
}

function Assert-OpenBladeDotNetRuntimePrerequisite {
    param([Parameter(Mandatory)][string]$Path)

    $prerequisite = Get-OpenBladeDotNetRuntimePrerequisite
    $hash = (Get-FileHash -LiteralPath $Path -Algorithm SHA512).Hash
    if (-not [string]::Equals($hash, $prerequisite.Sha512, [StringComparison]::OrdinalIgnoreCase)) {
        throw 'The .NET Desktop Runtime is not the pinned Microsoft release.'
    }

    $signature = Get-AuthenticodeSignature -LiteralPath $Path
    $microsoftPublisher = $null -ne $signature.SignerCertificate -and (
        $signature.SignerCertificate.Subject.StartsWith('CN=.NET, O=Microsoft Corporation,', [StringComparison]::Ordinal) -or
        $signature.SignerCertificate.Subject.StartsWith('CN=Microsoft Corporation,', [StringComparison]::Ordinal))
    if (($signature.Status -ne [Management.Automation.SignatureStatus]::Valid) -or
        (-not $microsoftPublisher)) {
        throw 'The .NET Desktop Runtime does not have a valid Microsoft signature.'
    }
}

function Copy-OpenBladeDotNetRuntime {
    param(
        [string]$SourcePath,
        [Parameter(Mandatory)][string]$DestinationDirectory
    )

    $prerequisite = Get-OpenBladeDotNetRuntimePrerequisite
    New-Item -ItemType Directory -Path $DestinationDirectory -Force | Out-Null
    $destination = Join-Path $DestinationDirectory $prerequisite.FileName
    if ([string]::IsNullOrWhiteSpace($SourcePath)) {
        Invoke-WebRequest -Uri $prerequisite.Uri -OutFile $destination
    }
    else {
        Copy-Item -LiteralPath $SourcePath -Destination $destination -ErrorAction Stop
    }
    Assert-OpenBladeDotNetRuntimePrerequisite -Path $destination
    return $destination
}

function Copy-OpenBladeVcRedist {
    param(
        [string]$SourcePath,
        [Parameter(Mandatory)][string]$DestinationDirectory
    )

    New-Item -ItemType Directory -Path $DestinationDirectory -Force | Out-Null
    $destination = Join-Path $DestinationDirectory 'VC_redist.x64.exe'
    if ([string]::IsNullOrWhiteSpace($SourcePath)) {
        Invoke-WebRequest -Uri $script:OpenBladeVcRedistUri -OutFile $destination
    }
    else {
        if (-not (Test-Path -LiteralPath $SourcePath -PathType Leaf)) {
            throw "The Visual C++ Redistributable is missing: $SourcePath"
        }
        Copy-Item -LiteralPath $SourcePath -Destination $destination
    }

    $hash = (Get-FileHash -LiteralPath $destination -Algorithm SHA256).Hash
    if (-not [string]::Equals(
            $hash,
            $script:OpenBladeVcRedistSha256,
            [StringComparison]::OrdinalIgnoreCase)) {
        throw "The Visual C++ Redistributable hash '$hash' is not the pinned release."
    }

    $signature = Get-AuthenticodeSignature -LiteralPath $destination
    if (($signature.Status -ne [Management.Automation.SignatureStatus]::Valid) -or
        ($null -eq $signature.SignerCertificate) -or
        (-not $signature.SignerCertificate.Subject.StartsWith(
            'CN=Microsoft Corporation,',
            [StringComparison]::Ordinal))) {
        throw "The Visual C++ Redistributable does not have a valid Microsoft signature."
    }

    return $destination
}
