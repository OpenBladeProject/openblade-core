#Requires -Version 7.4
[CmdletBinding(DefaultParameterSetName = 'Verify')]
param(
    [Parameter(Mandatory)][string]$Package,
    [Parameter(Mandatory, ParameterSetName = 'Create')][string]$FixtureDirectory,
    [Parameter(Mandatory, ParameterSetName = 'Verify')][string]$FixturePublicKey,
    [Parameter(Mandatory, ParameterSetName = 'Verify')][ValidatePattern('\A[0-9A-Fa-f]{64}\z')][string]$PackageSha256
)

Set-StrictMode -Version 3.0
$ErrorActionPreference = 'Stop'

# Exact ordinary pair from SetupBackendCutoverTests.PrivateRust.cs at 93bcaa5c.
# Keep the review pins outside the archive's trust domain.
$fixtureRevision = '41a3237e81188ed0780e89a2ca2d170da7d61db0'
$roles = @(
    [ordered]@{
        role = 'Service'
        file = 'openblade-windows-host.exe'
        sha256 = '1D63C481D9238799ABE81130B6C8B558F167F902129E9E1B3091E562C8067595'
    },
    [ordered]@{
        role = 'SessionAgent'
        file = 'openblade-rust-session-agent.exe'
        sha256 = '92A31A44138E617CABCC6D7620EDD146116DF9CEBAD01DD500FC1919C18E11C4'
    }
)
$manifestName = 'rust-fixture-candidate.json'
$signatureName = 'rust-fixture-candidate.sig'
$expectedNames = @($manifestName, $signatureName) + $roles.file
$utf8 = [Text.Encoding]::UTF8
$signatureFormat = [Security.Cryptography.DSASignatureFormat]::IeeeP1363FixedFieldConcatenation
$hashAlgorithm = [Security.Cryptography.HashAlgorithmName]::SHA256

function Assert-FixtureRole($Role, [byte[]]$Bytes) {
    $hash = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($Bytes))
    if ($hash -cne $Role.sha256) {
        throw "Unexpected SHA-256 for $($Role.role) ($($Role.file))."
    }

    # Same image contract as VerifyRustRole in the signed scratch Setup tests.
    $pe = [Reflection.PortableExecutable.PEReader]::new([IO.MemoryStream]::new($Bytes, $false))
    try {
        $headers = $pe.PEHeaders
        if ($headers.CoffHeader.Machine -ne 'Amd64' -or -not $headers.IsExe -or
            $null -eq $headers.PEHeader -or $headers.PEHeader.Magic -ne 'PE32Plus' -or
            $headers.PEHeader.AddressOfEntryPoint -le 0 -or $pe.HasMetadata -or
            $headers.PEHeader.CorHeaderTableDirectory.RelativeVirtualAddress -ne 0) {
            throw "$($Role.file) must be a native Windows x64 PE32+ executable."
        }
    }
    finally { $pe.Dispose() }
}

function New-FixtureManifest($Images) {
    $inventory = foreach ($role in $roles) {
        Assert-FixtureRole $role $Images[$role.file]
        [ordered]@{
            role = $role.role
            file = $role.file
            size = $Images[$role.file].Length
            sha256 = $role.sha256
        }
    }
    [ordered]@{
        schemaVersion = 1
        kind = 'OpenBlade.WindowsRust.OfflineFixtureCandidate'
        installable = $false
        architecture = 'x64'
        target = 'x86_64-pc-windows-msvc'
        sourceRevision = $fixtureRevision
        roles = @($inventory)
    } | ConvertTo-Json -Depth 4 -Compress
}

function Read-FixtureEntry($Entry, [int]$MaximumLength) {
    if ($Entry.Length -le 0 -or $Entry.Length -gt $MaximumLength) {
        throw "Unexpected candidate entry length for $($Entry.FullName)."
    }
    $stream = $Entry.Open()
    try {
        $bytes = [byte[]]::new([int]$Entry.Length)
        $stream.ReadExactly($bytes, 0, $bytes.Length)
        if ($stream.ReadByte() -ne -1) { throw "Unexpected candidate entry length for $($Entry.FullName)." }
        return ,$bytes
    }
    finally { $stream.Dispose() }
}

$maximumImageLength = 32MB
$packagePath = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($Package)
$key = [Security.Cryptography.ECDsa]::Create([Security.Cryptography.ECCurve+NamedCurves]::nistP256)
try {
    $images = @{}
    if ($PSCmdlet.ParameterSetName -eq 'Create') {
        if (Test-Path -LiteralPath $packagePath) { throw 'Candidate destination already exists.' }
        foreach ($role in $roles) {
            $inputPath = Join-Path $FixtureDirectory $role.file
            $inputFile = Get-Item -LiteralPath $inputPath -Force
            if ($inputFile -isnot [IO.FileInfo]) {
                throw "Expected a bounded fixture executable: $inputPath"
            }
            $stream = [IO.File]::Open($inputFile.FullName, 'Open', 'Read', 'Read')
            try {
                $length = $stream.Length
                if ($length -le 0 -or $length -gt $maximumImageLength) {
                    throw "Expected a bounded fixture executable: $inputPath"
                }
                $bytes = [byte[]]::new([int]$length)
                $stream.ReadExactly($bytes, 0, $bytes.Length)
                if ($stream.ReadByte() -ne -1) {
                    throw "Expected a bounded fixture executable: $inputPath"
                }
                $images[$role.file] = $bytes
            }
            finally { $stream.Dispose() }
        }
        $manifest = $utf8.GetBytes((New-FixtureManifest $images))
        $entries = @{
            $manifestName = $manifest
            $signatureName = $key.SignData($manifest, $hashAlgorithm, $signatureFormat)
        } + $images

        # Write beside the destination and publish with one non-overwriting move.
        $scratch = Join-Path ([IO.Path]::GetDirectoryName($packagePath)) ([Guid]::NewGuid().ToString('N') + '.tmp')
        $archive = [IO.Compression.ZipFile]::Open($scratch, [IO.Compression.ZipArchiveMode]::Create)
        try {
            try {
                foreach ($name in $expectedNames) {
                    $entry = $archive.CreateEntry($name, [IO.Compression.CompressionLevel]::NoCompression)
                    $entry.LastWriteTime = [DateTimeOffset]::new(2000, 1, 1, 0, 0, 0, [TimeSpan]::Zero)
                    $stream = $entry.Open()
                    try { $stream.Write($entries[$name], 0, $entries[$name].Length) }
                    finally { $stream.Dispose() }
                }
            }
            finally { $archive.Dispose() }
            $packageHash = (Get-FileHash -LiteralPath $scratch -Algorithm SHA256).Hash
            [IO.File]::Move($scratch, $packagePath)
        }
        finally { [IO.File]::Delete($scratch) }

        @{
            Package = $packagePath
            PackageSha256 = $packageHash
            FixturePublicKey = [Convert]::ToBase64String($key.ExportSubjectPublicKeyInfo())
        }
        return
    }

    $publicKey = [Convert]::FromBase64String($FixturePublicKey)
    $read = 0
    $key.ImportSubjectPublicKeyInfo($publicKey, [ref]$read)
    if ($read -ne $publicKey.Length -or $key.ExportParameters($false).Curve.Oid.Value -ne '1.2.840.10045.3.1.7') {
        throw 'Expected exactly one P-256 fixture public key.'
    }

    # Hash and read the same open file. The detached receipt also binds ZIP
    # structure and bytes outside the signed inventory.
    $packageStream = [IO.File]::Open($packagePath, 'Open', 'Read', 'Read')
    try {
        if ([Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($packageStream)) -ne $PackageSha256) {
            throw 'Candidate package SHA-256 differs from the detached receipt.'
        }
        $packageStream.Position = 0
        $archive = [IO.Compression.ZipArchive]::new($packageStream, 'Read', $true)
        try {
            $entries = [Collections.Generic.Dictionary[string, IO.Compression.ZipArchiveEntry]]::new([StringComparer]::Ordinal)
            foreach ($entry in $archive.Entries) {
                if ($entry.FullName -cnotin $expectedNames -or -not $entries.TryAdd($entry.FullName, $entry)) {
                    throw "Unexpected or duplicate candidate entry '$($entry.FullName)'."
                }
            }
            foreach ($name in $expectedNames) {
                if (-not $entries.ContainsKey($name)) { throw "Missing candidate entry '$name'." }
            }
            $manifest = Read-FixtureEntry $entries[$manifestName] 4096
            $signature = Read-FixtureEntry $entries[$signatureName] 64
            if ($signature.Length -ne 64 -or -not $key.VerifyData($manifest, $signature, $hashAlgorithm, $signatureFormat)) {
                throw 'Candidate fixture signature did not verify.'
            }

            foreach ($role in $roles) {
                $images[$role.file] = Read-FixtureEntry $entries[$role.file] $maximumImageLength
            }
            $expectedManifest = $utf8.GetBytes((New-FixtureManifest $images))
            if ([Convert]::ToBase64String($manifest) -cne [Convert]::ToBase64String($expectedManifest)) {
                throw 'Signed candidate manifest differs from the reviewed fixture identity.'
            }
            $utf8.GetString($manifest) | ConvertFrom-Json
        }
        finally { $archive.Dispose() }
    }
    finally { $packageStream.Dispose() }
}
finally { $key.Dispose() }
