$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$scriptPath = Join-Path $repoRoot 'build\Build-ThermalExternalDriftValidation.ps1'
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
    '-BuildChannel LocalDev',
    '-LocalDevVersion $LocalDevVersion',
    '-SourceRevision $revision',
    '-ThermalExternalDriftValidation',
    "artifacts\win-x64\OpenBlade\payload",
    "artifacts\win-x64\OpenBlade-Setup.exe",
    'OpenBlade-Setup.exe',
    'OpenBlade.Service.dll',
    'OpenBlade.Protocol.dll',
    'Rz09_0581_02e0_3_01_BatteryProduction.json',
    'serviceAssemblySha256',
    'protocolAssemblySha256',
    'productionFixtureSha256',
    'localDevVersion',
    'installerSha256',
    'firmwareSmokeSha256',
    'thermal-external-drift-validation-build.json'
)
foreach ($fragment in $requiredScriptFragments) {
    if ($script.IndexOf($fragment, [StringComparison]::Ordinal) -lt 0) {
        throw "Validation build script is missing required contract: $fragment"
    }
}

foreach ($publishFragment in @(
        "`$buildIdentity.Channel -ne 'LocalDev'",
        'Thermal external-drift validation installers are restricted to the LocalDev channel.',
        '--property:OpenBladeThermalExternalDriftValidation=true',
        '--property:OpenBladeThermalExternalDriftValidation=false',
        '$payload ($productionPublishArguments + $serviceValidationArguments)')) {
    if ($publish.IndexOf($publishFragment, [StringComparison]::Ordinal) -lt 0) {
        throw "Coordinated publishing is missing validation boundary: $publishFragment"
    }
}

$requiredProjectFragments = @(
    '<OpenBladeThermalExternalDriftValidation Condition=',
    '<AssemblyMetadata Include="OpenBladeThermalExternalDriftValidation"',
    '<AssemblyMetadata Include="OpenBladeValidationSourceRevision"',
    'Thermal external-drift validation builds require SourceRevision',
    '^[0-9a-fA-F]{40}$'
)
foreach ($fragment in $requiredProjectFragments) {
    if ($serviceProject.IndexOf($fragment, [StringComparison]::Ordinal) -lt 0) {
        throw "Service project is missing validation-build contract: $fragment"
    }
}

Write-Host 'Thermal external-drift validation build tests passed.'
