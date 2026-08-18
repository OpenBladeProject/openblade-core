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
    throw 'The validation Service must be built from a clean exact commit.'
}

$resolvedOutput = [System.IO.Path]::GetFullPath($OutputDirectory)
New-Item -ItemType Directory -Force -Path $resolvedOutput | Out-Null
& dotnet publish (Join-Path $repoRoot 'src\OpenBlade.Service\OpenBlade.Service.csproj') `
    -c Release `
    --no-restore `
    -p:NuGetAudit=false `
    -p:OpenBladePadlessHyperBoostLifecycleValidation=true `
    -p:SourceRevision=$revision `
    -o $resolvedOutput
if ($LASTEXITCODE -ne 0) {
    throw "Validation Service publish failed with exit code $LASTEXITCODE."
}

$serviceAssembly = Join-Path $resolvedOutput 'OpenBlade.Service.dll'
$admissionManifest = Join-Path $repoRoot `
    'src\OpenBlade.Protocol\Fixtures\Rz09_0581_02e0_4_00_HyperBoostWithoutCoolingPadAdmission.json'
if (-not (Test-Path -LiteralPath $serviceAssembly -PathType Leaf) -or
    -not (Test-Path -LiteralPath $admissionManifest -PathType Leaf)) {
    throw 'The validation Service or admission manifest is missing.'
}

$manifest = [ordered]@{
    schemaVersion = 1
    validationRunId = [guid]::NewGuid()
    sourceRevision = $revision
    serviceAssemblySha256 = (Get-FileHash -LiteralPath $serviceAssembly -Algorithm SHA256).Hash
    admissionManifestSha256 = (Get-FileHash -LiteralPath $admissionManifest -Algorithm SHA256).Hash
}
$manifestPath = Join-Path $resolvedOutput 'padless-hyperboost-lifecycle-validation-build.json'
$manifest | ConvertTo-Json | Set-Content -LiteralPath $manifestPath -Encoding UTF8
Write-Host "Validation Service: $serviceAssembly"
Write-Host "Validation manifest: $manifestPath"
