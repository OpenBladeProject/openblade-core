$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$scriptPath = Join-Path $repoRoot 'build\Build-PadlessHyperBoostLifecycleValidation.ps1'
$publishPath = Join-Path $repoRoot 'build\Publish.ps1'
$serviceProjectPath = Join-Path $repoRoot 'src\OpenBlade.Service\OpenBlade.Service.csproj'

$script = Get-Content -LiteralPath $scriptPath -Raw
$publish = Get-Content -LiteralPath $publishPath -Raw
$serviceProject = Get-Content -LiteralPath $serviceProjectPath -Raw

$requiredScriptFragments = @(
    'git -C $repoRoot rev-parse HEAD',
    'git -C $repoRoot status --porcelain',
    'validation output directory must be outside the source repository',
    'validation output directory must be empty',
    '-p:OpenBladePadlessHyperBoostLifecycleValidation=true',
    '-p:SourceRevision=$revision',
    'OpenBlade.Service.dll',
    'Rz09_0581_02e0_4_00_HyperBoostWithoutCoolingPadAdmission.json',
    'Rz09_0528_02c6_2_02_PadlessLifecyclePreparation.json',
    "[ValidateSet('RZ09-0581', 'RZ09-0528')]",
    "$Model = 'RZ09-0581'",
    'Resolve-OpenBladeBuildIdentity',
    '-Channel Nightly',
    '-p:IncludeSourceRevisionInInformationalVersion=false',
    'serviceAssemblySha256',
    'admissionManifestSha256',
    'padless-hyperboost-lifecycle-validation-build.json'
)
foreach ($fragment in $requiredScriptFragments) {
    if ($script.IndexOf($fragment, [StringComparison]::Ordinal) -lt 0) {
        throw "Validation build script is missing required contract: $fragment"
    }
}

if ($publish.IndexOf(
        '--property:OpenBladePadlessHyperBoostLifecycleValidation=false',
        [StringComparison]::Ordinal) -lt 0) {
    throw 'Production publishing does not explicitly disable the validation build marker.'
}

$requiredProjectFragments = @(
    '<OpenBladePadlessHyperBoostLifecycleValidation Condition=',
    '<AssemblyMetadata Include="OpenBladePadlessHyperBoostLifecycleValidation"',
    '<AssemblyMetadata Include="OpenBladeValidationSourceRevision"',
    '^[0-9a-fA-F]{40}$'
)
foreach ($fragment in $requiredProjectFragments) {
    if ($serviceProject.IndexOf($fragment, [StringComparison]::Ordinal) -lt 0) {
        throw "Service project is missing validation-build contract: $fragment"
    }
}

Write-Host 'Pad-less HyperBoost lifecycle validation build tests passed.'
