[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string]$OutputDirectory
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
$revision = (& git -C $repoRoot rev-parse HEAD).Trim()
if ($revision -notmatch '^[0-9a-f]{40}$') {
    throw 'The exact source revision could not be determined.'
}
if (-not [string]::IsNullOrWhiteSpace((& git -C $repoRoot status --porcelain))) {
    throw 'The thermal external-drift validation build must come from a clean exact commit.'
}

$resolvedRepoRoot = [System.IO.Path]::GetFullPath($repoRoot).TrimEnd(
    [System.IO.Path]::DirectorySeparatorChar,
    [System.IO.Path]::AltDirectorySeparatorChar)
$resolvedOutput = [System.IO.Path]::GetFullPath($OutputDirectory).TrimEnd(
    [System.IO.Path]::DirectorySeparatorChar,
    [System.IO.Path]::AltDirectorySeparatorChar)
$repoPrefix = $resolvedRepoRoot + [System.IO.Path]::DirectorySeparatorChar
if ($resolvedOutput.Equals($resolvedRepoRoot, [System.StringComparison]::OrdinalIgnoreCase) -or
    $resolvedOutput.StartsWith($repoPrefix, [System.StringComparison]::OrdinalIgnoreCase)) {
    throw 'The validation output directory must be outside the source repository.'
}
if (Test-Path -LiteralPath $resolvedOutput) {
    if (-not (Test-Path -LiteralPath $resolvedOutput -PathType Container)) {
        throw 'The validation output path must be a directory.'
    }
    if (Get-ChildItem -LiteralPath $resolvedOutput -Force | Select-Object -First 1) {
        throw 'The validation output directory must be empty.'
    }
}
else {
    New-Item -ItemType Directory -Path $resolvedOutput | Out-Null
}

$serviceOutput = Join-Path $resolvedOutput 'service'
$toolOutput = Join-Path $resolvedOutput 'firmware-smoke'
& dotnet publish (Join-Path $repoRoot 'src\OpenBlade.Service\OpenBlade.Service.csproj') `
    -c Release `
    --no-restore `
    -p:NuGetAudit=false `
    -p:OpenBladeThermalExternalDriftValidation=true `
    -p:SourceRevision=$revision `
    -o $serviceOutput
if ($LASTEXITCODE -ne 0) {
    throw "Validation Service publish failed with exit code $LASTEXITCODE."
}
& dotnet publish (Join-Path $repoRoot 'src\OpenBlade.FirmwareSmoke\OpenBlade.FirmwareSmoke.csproj') `
    -c Release `
    --no-restore `
    -p:NuGetAudit=false `
    -p:SourceRevision=$revision `
    -o $toolOutput
if ($LASTEXITCODE -ne 0) {
    throw "Validation FirmwareSmoke publish failed with exit code $LASTEXITCODE."
}

$serviceAssembly = Join-Path $serviceOutput 'OpenBlade.Service.dll'
$protocolAssembly = Join-Path $serviceOutput 'OpenBlade.Protocol.dll'
$fixture = Join-Path $repoRoot `
    'src\OpenBlade.Protocol\Fixtures\Rz09_0581_02e0_3_01_BatteryProduction.json'
$tool = Join-Path $toolOutput 'OpenBlade.FirmwareSmoke.exe'
foreach ($required in @($serviceAssembly, $protocolAssembly, $fixture, $tool)) {
    if (-not (Test-Path -LiteralPath $required -PathType Leaf)) {
        throw "The validation artifact is missing: $required"
    }
}

$manifest = [ordered]@{
    schemaVersion = 1
    validationRunId = [guid]::NewGuid()
    sourceRevision = $revision
    serviceAssemblySha256 = (Get-FileHash -LiteralPath $serviceAssembly -Algorithm SHA256).Hash
    protocolAssemblySha256 = (Get-FileHash -LiteralPath $protocolAssembly -Algorithm SHA256).Hash
    productionFixtureSha256 = (Get-FileHash -LiteralPath $fixture -Algorithm SHA256).Hash
}
$manifestPath = Join-Path $resolvedOutput 'thermal-external-drift-validation-build.json'
$manifest | ConvertTo-Json | Set-Content -LiteralPath $manifestPath -Encoding UTF8
Write-Host "Validation Service: $serviceAssembly"
Write-Host "Validation FirmwareSmoke: $tool"
Write-Host "Validation manifest: $manifestPath"
