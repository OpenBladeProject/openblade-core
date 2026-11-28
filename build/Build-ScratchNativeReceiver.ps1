#Requires -Version 7.4
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$BuildRoot,
    [Parameter(Mandatory)][string]$ReadinessImage,
    [string]$Destination,
    [switch]$VerifyOnly
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version 3.0
$source = [IO.Path]::GetFullPath((Split-Path $PSScriptRoot))
$BuildRoot = [IO.Path]::TrimEndingDirectorySeparator([IO.Path]::GetFullPath($BuildRoot))
if ($BuildRoot.Equals($source, [StringComparison]::OrdinalIgnoreCase) -or
    $BuildRoot.StartsWith($source + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
    throw 'The scratch NativeAOT build requires an external build directory.'
}
if ($env:RUSTUP_TOOLCHAIN -ne '1.99.1' -or $env:CARGO_NET_OFFLINE -ne 'true') {
    throw 'Use the offline Rust 1.99.1 environment, including its compiler on PATH.'
}
$image = Join-Path $BuildRoot 'receiver/OpenBlade.CandidateReceipt.Scratch.exe'
$manifest = Join-Path $BuildRoot 'receiver/scratch-receiver-build.json'
$sdk = & dotnet --version
if ($LASTEXITCODE -ne 0 -or $sdk -cne '10.0.301') { throw 'The scratch build requires .NET SDK 10.0.301.' }
$configuration = 'SDK=10.0.301;Release;win-x64;NativeAOT=10.0.9;CandidateReceiptFixture=true;PackageProofFixture=true;ScratchNativeReceiver=true;EnableAotAnalyzer=false;EnableTrimAnalyzer=false'
$sourceRevision = & git -C $source rev-parse HEAD
if ($LASTEXITCODE -ne 0 -or $sourceRevision -notmatch '^[0-9a-f]{40}$') { throw 'Cannot identify the fixture source commit.' }

function Get-SourceInventory {
    # Current working bytes, including new source files. No Git object or timestamp
    # substitutes for source identity. Build outputs and graph notes are excluded.
    $files = @(& git -C $source ls-files --cached --others --exclude-standard)
    if ($LASTEXITCODE -ne 0) { throw 'Could not enumerate current fixture build inputs.' }
    $inventory = foreach ($relative in ($files | Sort-Object -Unique -CaseSensitive)) {
        if ($relative -match '^(src/|rust/|tests/OpenBlade.CandidateReceipt.Scratch/|build/)' -or
            $relative -match '^([^/]+\.(props|targets|json|config)|global.json)$') {
            $path = Join-Path $source $relative
            if (Test-Path -LiteralPath $path -PathType Leaf) {
                '{0} {1}' -f (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash, $relative
            }
        }
    }
    return ($inventory -join "`n")
}

$inputs = Get-SourceInventory
$readinessHash = (Get-FileHash -LiteralPath $ReadinessImage -Algorithm SHA256).Hash
if (-not $VerifyOnly) {
    [IO.Directory]::CreateDirectory($BuildRoot) | Out-Null
    $config = Join-Path $BuildRoot 'NuGet.offline.config'
    [IO.File]::WriteAllText($config, '<configuration><packageSources><clear /></packageSources></configuration>')
    $project = Join-Path $source 'tests/OpenBlade.CandidateReceipt.Scratch/OpenBlade.CandidateReceipt.Scratch.csproj'
    $key = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData(
        [Text.Encoding]::UTF8.GetBytes($sourceRevision + "`n" + $configuration + "`n" + $readinessHash + "`n" + $inputs)))
    $generation = Join-Path $BuildRoot $key
    # Publish the current source with fixed fixture configuration, never caller
    # supplied receiver bytes. Keep this graph separate from the managed test graph.
    & dotnet publish $project -c Release -r win-x64 --artifacts-path (Join-Path $generation 'artifacts') `
        -o (Join-Path $generation 'published') --configfile $config -p:NuGetAudit=false `
        -p:EnableAotAnalyzer=false -p:EnableTrimAnalyzer=false `
        "-p:SourceRevisionId=$sourceRevision" `
        -p:OpenBladeCandidateReceiptFixture=true -p:OpenBladePackageProofFixture=true `
        -p:OpenBladeScratchNativeReceiver=true "-p:OpenBladeCandidateReceiptRustProbe=$ReadinessImage"
    if ($LASTEXITCODE -ne 0) { throw 'Current-source scratch NativeAOT publish failed.' }
    if ((Get-SourceInventory) -cne $inputs -or
        (Get-FileHash -LiteralPath $ReadinessImage -Algorithm SHA256).Hash -cne $readinessHash) {
        throw 'Scratch build inputs changed during publish.'
    }
    [IO.Directory]::CreateDirectory((Split-Path $image)) | Out-Null
    Copy-Item -LiteralPath (Join-Path $generation 'published/OpenBlade.CandidateReceipt.Scratch.exe') -Destination $image -Force
    if ((Get-FileHash -LiteralPath (Join-Path $generation 'published/OpenBlade.CandidateReceipt.Scratch.exe')).Hash -cne
        (Get-FileHash -LiteralPath $image).Hash) { throw 'The published receiver copy differs.' }
    [ordered]@{
        schema = 2
        sourceRevision = $sourceRevision
        configuration = $configuration
        sourceInventory = $inputs
        readinessSha256 = $readinessHash
        receiverSha256 = (Get-FileHash -LiteralPath $image -Algorithm SHA256).Hash
    } | ConvertTo-Json -Depth 3 | Set-Content -LiteralPath $manifest -Encoding utf8
}
$pin = Get-Content -LiteralPath $manifest -Raw | ConvertFrom-Json
if ($pin.schema -ne 2 -or $pin.sourceRevision -cne $sourceRevision -or
    $pin.configuration -cne $configuration -or $pin.sourceInventory -cne $inputs -or
    $pin.readinessSha256 -cne $readinessHash -or
    $pin.receiverSha256 -cne (Get-FileHash -LiteralPath $image -Algorithm SHA256).Hash) {
    throw 'Scratch receiver provenance is stale or mismatched; rebuild the current source.'
}
if ($Destination) {
    foreach ($name in @('OpenBlade.CandidateReceipt.Scratch.exe', 'scratch-receiver-build.json')) {
        $from = Join-Path (Split-Path $image) $name
        $to = Join-Path $Destination $name
        if ((Get-FileHash -LiteralPath $from -Algorithm SHA256).Hash -cne
            (Get-FileHash -LiteralPath $to -Algorithm SHA256).Hash) {
            throw "Scratch fixture destination differs from the exact current build: $name"
        }
    }
}
