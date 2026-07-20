[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Release',

    [string]$UiAccessSigningCertificateThumbprint,

    [string]$UiAccessTimestampServer,

    [ValidateSet('CurrentUser', 'LocalMachine')]
    [string]$UiAccessCertificateStore = 'CurrentUser'
)

$ErrorActionPreference = 'Stop'
$repository = Split-Path -Parent $PSScriptRoot
$artifacts = [IO.Path]::GetFullPath((Join-Path $repository 'artifacts'))
. (Join-Path $PSScriptRoot 'PublishPayloadValidation.ps1')

& (Join-Path $PSScriptRoot 'Verify-Branding.ps1') -Repository $repository
if ($LASTEXITCODE -ne 0) {
    throw "OpenBlade branding verification failed with exit code $LASTEXITCODE"
}

$bundle = [IO.Path]::GetFullPath((Join-Path $artifacts 'win-x64\OpenBlade'))
$developerTools = [IO.Path]::GetFullPath((Join-Path $artifacts 'win-x64\OpenBlade.DeveloperTools'))
$stagingRoot = [IO.Path]::GetFullPath((Join-Path $artifacts ('.staging\' + [Guid]::NewGuid().ToString('N'))))
$stagingBundle = Join-Path $stagingRoot 'OpenBlade'
$stagingDeveloperTools = Join-Path $stagingRoot 'OpenBlade.DeveloperTools'
$payload = Join-Path $stagingBundle 'payload'

function Assert-SafeArtifactPath {
    param([Parameter(Mandatory)][string]$Path)

    $candidate = [IO.Path]::GetFullPath($Path).TrimEnd([IO.Path]::DirectorySeparatorChar)
    $root = $artifacts.TrimEnd([IO.Path]::DirectorySeparatorChar)
    $prefix = $root + [IO.Path]::DirectorySeparatorChar
    if (-not $candidate.StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Refusing to modify path outside the artifacts directory: $candidate"
    }
}

function Invoke-CheckedPublish {
    param(
        [Parameter(Mandatory)][string]$Project,
        [Parameter(Mandatory)][string]$Output
    )

    & dotnet publish $Project @publishArguments --output $Output
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet publish failed with exit code $LASTEXITCODE for $Project"
    }
}

function Resolve-SignToolPath {
    $pathCommand = Get-Command 'signtool.exe' -CommandType Application -ErrorAction SilentlyContinue
    if ($null -ne $pathCommand) {
        return $pathCommand.Source
    }

    $programFilesX86 = ${env:ProgramFiles(x86)}
    if ([string]::IsNullOrWhiteSpace($programFilesX86)) {
        $programFilesX86 = Join-Path $env:SystemDrive 'Program Files (x86)'
    }

    $windowsKitsBin = Join-Path $programFilesX86 'Windows Kits\10\bin'
    if (Test-Path -LiteralPath $windowsKitsBin -PathType Container) {
        $versionDirectories = @(Get-ChildItem -LiteralPath $windowsKitsBin -Directory |
            Where-Object { $_.Name -match '^\d+\.\d+\.\d+\.\d+$' } |
            Sort-Object { [version]$_.Name } -Descending)

        foreach ($versionDirectory in $versionDirectories) {
            foreach ($architecture in @('x64', 'x86', 'arm64')) {
                $candidate = Join-Path $versionDirectory.FullName (Join-Path $architecture 'signtool.exe')
                if (Test-Path -LiteralPath $candidate -PathType Leaf) {
                    return $candidate
                }
            }
        }
    }

    throw 'signtool.exe was not found. Install the Windows SDK or add signtool.exe to PATH before publishing the UIAccess input agent.'
}

function Publish-SignedUiAccessInputAgent {
    param([Parameter(Mandatory)][string]$Output)

    if ([string]::IsNullOrWhiteSpace($UiAccessSigningCertificateThumbprint)) {
        if (-not [string]::IsNullOrWhiteSpace($UiAccessTimestampServer)) {
            throw 'A UIAccess timestamp server was supplied without a signing certificate thumbprint.'
        }
        return
    }

    $thumbprint = $UiAccessSigningCertificateThumbprint.Replace(' ', '').ToUpperInvariant()
    if ($thumbprint -notmatch '^[0-9A-F]{40}$') {
        throw 'The UIAccess signing certificate thumbprint must be exactly 40 hexadecimal characters.'
    }
    if ([string]::IsNullOrWhiteSpace($UiAccessTimestampServer)) {
        throw 'A trusted HTTPS RFC 3161 timestamp server is required for the UIAccess input agent.'
    }
    $timestamp = $null
    if (-not [Uri]::TryCreate($UiAccessTimestampServer, [UriKind]::Absolute, [ref]$timestamp) `
        -or -not [string]::Equals($timestamp.Scheme, 'https', [StringComparison]::OrdinalIgnoreCase)) {
        throw 'The UIAccess timestamp server must be an absolute HTTPS URI.'
    }

    Invoke-CheckedPublish `
        (Join-Path $repository 'src\OpenBlade.InputAgent\OpenBlade.InputAgent.csproj') `
        $Output

    $executable = Join-Path $Output 'OpenBlade.InputAgent.exe'
    if (-not (Test-Path -LiteralPath $executable -PathType Leaf)) {
        throw "OpenBlade UIAccess input-agent publish is missing: $executable"
    }

    $signTool = Resolve-SignToolPath
    $arguments = @('sign')
    if ([string]::Equals($UiAccessCertificateStore, 'LocalMachine', [StringComparison]::Ordinal)) {
        $arguments += '/sm'
    }
    $arguments += @(
        '/sha1', $thumbprint,
        '/fd', 'SHA256',
        '/tr', $timestamp.AbsoluteUri,
        '/td', 'SHA256',
        $executable
    )
    & $signTool @arguments
    if ($LASTEXITCODE -ne 0) {
        throw "signtool failed with exit code $LASTEXITCODE for $executable"
    }

    $signature = Get-AuthenticodeSignature -LiteralPath $executable
    if ($signature.Status -ne [System.Management.Automation.SignatureStatus]::Valid `
        -or $null -eq $signature.SignerCertificate) {
        throw "The UIAccess input-agent Authenticode signature is not valid: $($signature.StatusMessage)"
    }
}

Assert-SafeArtifactPath -Path $bundle
Assert-SafeArtifactPath -Path $developerTools
Assert-SafeArtifactPath -Path $stagingRoot

$publishArguments = @(
    '--configuration', $Configuration,
    '--runtime', 'win-x64',
    '--self-contained', 'true',
    '--property:PublishTrimmed=false'
)

try {
    New-Item -ItemType Directory -Path $payload -Force | Out-Null

    Invoke-CheckedPublish (Join-Path $repository 'src\OpenBlade.Service\OpenBlade.Service.csproj') $payload
    Invoke-CheckedPublish (Join-Path $repository 'src\OpenBlade.SessionAgent\OpenBlade.SessionAgent.csproj') $payload
    Publish-SignedUiAccessInputAgent -Output $payload
    Invoke-CheckedPublish (Join-Path $repository 'src\OpenBlade.Tray\OpenBlade.Tray.csproj') $payload
    Invoke-CheckedPublish (Join-Path $repository 'src\OpenBlade.Capture\OpenBlade.Capture.csproj') $stagingDeveloperTools
    Invoke-CheckedPublish (Join-Path $repository 'src\OpenBlade.Setup\OpenBlade.Setup.csproj') $stagingBundle

    $captureExecutable = Join-Path $stagingDeveloperTools 'OpenBlade.Capture.exe'
    if (-not (Test-Path -LiteralPath $captureExecutable -PathType Leaf)) {
        throw "OpenBlade developer-tools artifact is missing: $captureExecutable"
    }

    Assert-LightingValidationProtocolPair `
        -ServicePayload $payload `
        -DeveloperTools $stagingDeveloperTools

    Complete-ValidatedBundleReplacement `
        -StagingBundle $stagingBundle `
        -Bundle $bundle `
        -PermittedRoot $artifacts

    if (Test-Path -LiteralPath $developerTools) {
        Remove-ValidatedPublishPath -Path $developerTools -PermittedRoot $artifacts
    }
    New-Item -ItemType Directory -Path (Split-Path -Parent $developerTools) -Force | Out-Null
    Move-Item -LiteralPath $stagingDeveloperTools -Destination $developerTools
}
finally {
    if (Test-Path -LiteralPath $stagingRoot) {
        Remove-ValidatedPublishPath -Path $stagingRoot -PermittedRoot $artifacts
    }
}

Write-Host "Self-contained production bundle created at $bundle"
Write-Host "Self-contained developer tools created at $developerTools"
