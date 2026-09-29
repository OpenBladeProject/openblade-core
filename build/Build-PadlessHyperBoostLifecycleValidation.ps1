[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string]$OutputDirectory,

    # The exact model whose reviewed readiness manifest the service will bind; never aliased across models.
    [ValidateSet('RZ09-0581', 'RZ09-0528')]
    [string]$Model = 'RZ09-0581'
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
$admissionManifest = Join-Path $repoRoot $(switch ($Model) {
    'RZ09-0581' { 'src\OpenBlade.Protocol\Fixtures\Rz09_0581_02e0_4_00_HyperBoostWithoutCoolingPadAdmission.json' }
    'RZ09-0528' { 'src\OpenBlade.Protocol\Fixtures\Rz09_0528_02c6_2_02_PadlessLifecyclePreparation.json' }
})
if (-not (Test-Path -LiteralPath $serviceAssembly -PathType Leaf) -or
    -not (Test-Path -LiteralPath $admissionManifest -PathType Leaf)) {
    throw 'The validation Service or admission manifest is missing.'
}

$manifest = [ordered]@{
    schemaVersion = 1
    model = $Model
    validationRunId = [guid]::NewGuid()
    sourceRevision = $revision
    serviceAssemblySha256 = (Get-FileHash -LiteralPath $serviceAssembly -Algorithm SHA256).Hash
    admissionManifestSha256 = (Get-FileHash -LiteralPath $admissionManifest -Algorithm SHA256).Hash
}
$manifestPath = Join-Path $resolvedOutput 'padless-hyperboost-lifecycle-validation-build.json'
$manifest | ConvertTo-Json | Set-Content -LiteralPath $manifestPath -Encoding UTF8
Write-Host "Validation Service: $serviceAssembly"
Write-Host "Validation manifest: $manifestPath"
