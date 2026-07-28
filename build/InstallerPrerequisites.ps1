Set-StrictMode -Version 3.0
$ErrorActionPreference = 'Stop'

$script:OpenBladeVcRedistUri =
    'https://aka.ms/vs/18/release/14.50.35719/VC_redist.x64.exe'
$script:OpenBladeVcRedistSha256 =
    '8995548DFFFCDE7C49987029C764355612BA6850EE09A7B6F0FDDC85BDC5C280'

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
