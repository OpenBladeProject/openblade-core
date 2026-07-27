[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$repositoryRoot = Split-Path -Parent $PSScriptRoot
$projects = @(
    'tests\OpenBlade.Contracts.Tests\OpenBlade.Contracts.Tests.csproj',
    'tests\OpenBlade.Session.Tests\OpenBlade.Session.Tests.csproj',
    'tests\OpenBlade.Protocol.Tests\OpenBlade.Protocol.Tests.csproj'
)

try {
    $env:OPENBLADE_UPDATE_PUBLIC_API_BASELINES = '1'
    $env:OPENBLADE_REPOSITORY_ROOT = $repositoryRoot
    foreach ($project in $projects) {
        & dotnet test (Join-Path $repositoryRoot $project) `
            -p:NuGetAudit=false `
            --no-restore `
            --filter 'FullyQualifiedName~PublicApiBaselineTests'
        if ($LASTEXITCODE -ne 0) {
            throw "Failed to update the public API baseline for '$project'."
        }
    }
}
finally {
    Remove-Item Env:OPENBLADE_UPDATE_PUBLIC_API_BASELINES -ErrorAction SilentlyContinue
    Remove-Item Env:OPENBLADE_REPOSITORY_ROOT -ErrorAction SilentlyContinue
}

foreach ($project in $projects) {
    & dotnet test (Join-Path $repositoryRoot $project) `
        -p:NuGetAudit=false `
        --no-restore `
        --filter 'FullyQualifiedName~PublicApiBaselineTests'
    if ($LASTEXITCODE -ne 0) {
        throw "The regenerated public API baseline does not match '$project'."
    }
}
