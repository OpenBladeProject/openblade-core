Set-StrictMode -Version 3.0
$ErrorActionPreference = 'Stop'

function Get-OpenBladePawnIoPrerequisite {
    return [pscustomobject]@{
        Version = '2.2.0'
        FileName = 'PawnIO_setup.exe'
        # The release download route can return 504 before redirecting. This is
        # the same immutable official 2.2.0 asset, requested as binary content.
        Uri = 'https://api.github.com/repos/namazso/PawnIO.Setup/releases/assets/374375077'
        Sha256 = '1F519A22E47187F70A1379A48CA604981C4FCF694F4E65B734AAA74A9FBA3032'
        Publisher = 'E=admin@namazso.eu, CN=namazso.eu, O=namazso, L=Debrecen, C=HU'
    }
}

function Assert-OpenBladePawnIoPrerequisite {
    param([Parameter(Mandatory)][string]$Path)
    $pin = Get-OpenBladePawnIoPrerequisite
    if ((Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash -ne $pin.Sha256) {
        throw 'PawnIO is not the pinned unmodified official 2.2.0 installer.'
    }
    $signature = Get-AuthenticodeSignature -LiteralPath $Path
    if ($signature.Status -ne [Management.Automation.SignatureStatus]::Valid -or
        $null -eq $signature.SignerCertificate -or
        $signature.SignerCertificate.Subject -cne $pin.Publisher) {
        throw 'PawnIO does not have the verified official publisher signature.'
    }
}

function Copy-OpenBladePawnIoPrerequisite {
    param(
        [string]$SourcePath,
        [Parameter(Mandatory)][string]$DestinationDirectory,
        [string]$CacheDirectory = (Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) 'OpenBlade\BuildCache\Prerequisites')
    )
    $pin = Get-OpenBladePawnIoPrerequisite
    [void][IO.Directory]::CreateDirectory($DestinationDirectory)
    if ([string]::IsNullOrWhiteSpace($SourcePath)) {
        $entry = Join-Path $CacheDirectory $pin.Sha256.Substring(0, 16)
        $cached = Join-Path $entry $pin.FileName
        $valid = $false
        if (Test-Path -LiteralPath $cached -PathType Leaf) {
            try { Assert-OpenBladePawnIoPrerequisite $cached; $valid = $true } catch { Write-Verbose $_ }
        }
        if (-not $valid) {
            [void][IO.Directory]::CreateDirectory($entry)
            $download = Join-Path $entry ([Guid]::NewGuid().ToString('N') + '.download')
            try {
                # The official asset endpoint intermittently returns 503. Retry only
                # transient acquisition failures, at most four 60-second requests.
                # Integrity and publisher failures below are never retried.
                $retryDelays = @(5, 15, 30)
                for ($attempt = 0; ; $attempt++) {
                    try {
                        Invoke-WebRequest -Uri $pin.Uri -OutFile $download -UseBasicParsing -TimeoutSec 60 -Headers @{
                            Accept = 'application/octet-stream'
                            'X-GitHub-Api-Version' = '2022-11-28'
                        }
                        break
                    }
                    catch {
                        $response = $_.Exception.PSObject.Properties['Response']
                        $status = if ($null -ne $response -and $null -ne $response.Value) {
                            $response.Value.PSObject.Properties['StatusCode']
                        } else { $null }
                        $transient = ($null -ne $status -and [int]$status.Value -in @(408, 429, 500, 502, 503, 504)) -or
                            ($_.Exception -is [Net.WebException] -and $_.Exception.Status -eq [Net.WebExceptionStatus]::Timeout)
                        if (-not $transient -or $attempt -ge $retryDelays.Count) { throw }
                        if (Test-Path -LiteralPath $download) { Remove-Item -LiteralPath $download -Force }
                        Write-Warning "PawnIO download attempt $($attempt + 1) failed temporarily; retrying in $($retryDelays[$attempt]) seconds."
                        Start-Sleep -Seconds $retryDelays[$attempt]
                    }
                }
                Assert-OpenBladePawnIoPrerequisite $download
                if ([IO.File]::Exists($cached)) { [IO.File]::Replace($download, $cached, [NullString]::Value) }
                else { [IO.File]::Move($download, $cached) }
            }
            finally { if (Test-Path -LiteralPath $download) { Remove-Item -LiteralPath $download -Force } }
        }
        $SourcePath = $cached
    }
    $destination = Join-Path $DestinationDirectory $pin.FileName
    Copy-Item -LiteralPath $SourcePath -Destination $destination -ErrorAction Stop
    Assert-OpenBladePawnIoPrerequisite $destination
    return $destination
}

function Add-CpuTemperaturePublishPayload {
    param([Parameter(Mandatory)][string]$Helper, [Parameter(Mandatory)][string]$Payload)
    $required = @('OpenBlade.CpuTemperature.exe', 'OpenBlade.CpuTemperature.dll',
        'OpenBlade.CpuTemperature.deps.json', 'OpenBlade.CpuTemperature.runtimeconfig.json', 'LibreHardwareMonitorLib.dll')
    foreach ($name in $required) {
        if (-not (Test-Path -LiteralPath (Join-Path $Helper $name) -PathType Leaf)) {
            throw "CPU temperature helper publish is missing $name."
        }
    }
    $files = @(Get-ChildItem -LiteralPath $Helper -Recurse -File -Force)
    $prefix = [IO.Path]::GetFullPath($Helper).TrimEnd('\') + '\'
    # Validate every collision before copying any helper file. Shared dependencies
    # must remain byte-identical to the main service/guardian payload.
    foreach ($file in $files) {
        if (($file.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) { throw 'CPU helper contains a reparse point.' }
        $relative = $file.FullName.Substring($prefix.Length)
        $destination = Join-Path $Payload $relative
        if ((Test-Path -LiteralPath $destination) -and
            (Get-FileHash -LiteralPath $destination).Hash -ne (Get-FileHash -LiteralPath $file.FullName).Hash) {
            throw "CPU temperature helper shared dependency differs: $relative"
        }
    }
    foreach ($file in $files) {
        $destination = Join-Path $Payload $file.FullName.Substring($prefix.Length)
        if (-not (Test-Path -LiteralPath $destination)) {
            [void][IO.Directory]::CreateDirectory((Split-Path -Parent $destination))
            Copy-Item -LiteralPath $file.FullName -Destination $destination
        }
    }
}

function Add-CpuTemperatureLicensePayload {
    param([Parameter(Mandatory)][string]$Repository, [Parameter(Mandatory)][string]$Payload)
    Add-Type -AssemblyName System.IO.Compression, System.IO.Compression.FileSystem
    Copy-Item -LiteralPath (Join-Path $Repository 'build\third-party\PawnIO-NOTICE.txt') -Destination $Payload
    $source = Join-Path $Repository 'third_party\LibreHardwareMonitor'
    if (-not (Test-Path -LiteralPath $source -PathType Container)) { throw 'The reviewed LibreHardwareMonitor corresponding source is missing.' }
    $licenseDirectory = Join-Path $Payload 'licenses'
    [void][IO.Directory]::CreateDirectory($licenseDirectory)
    $archivePath = Join-Path $licenseDirectory 'LibreHardwareMonitor-source.zip'
    $sourcePrefix = [IO.Path]::GetFullPath($source).TrimEnd('\') + '\'
    $files = @(Get-ChildItem -LiteralPath $source -Recurse -File -Force | Where-Object {
        $_.FullName.Substring($sourcePrefix.Length) -notmatch '(^|[\\/])(bin|obj|\.git)([\\/]|$)'
    })
    if ($files.Count -eq 0) { throw 'LibreHardwareMonitor corresponding source is empty.' }
    $archive = [IO.Compression.ZipFile]::Open($archivePath, [IO.Compression.ZipArchiveMode]::Create)
    try {
        foreach ($file in $files | Sort-Object FullName) {
            $relative = $file.FullName.Substring($sourcePrefix.Length).Replace('\', '/')
            [void][IO.Compression.ZipFileExtensions]::CreateEntryFromFile($archive, $file.FullName, $relative)
        }
    }
    finally { $archive.Dispose() }
}
