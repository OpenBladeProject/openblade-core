[CmdletBinding()]
param()

Set-StrictMode -Version 3.0
$ErrorActionPreference = 'Stop'

# Run from rust/ so rustup selects the checked-in toolchain override.
$expectedDependencies = @{
    'openblade-types' = @()
    'openblade-protocol' = @('openblade-types')
    'openblade-domain' = @('openblade-types')
    'openblade-ffi' = @('openblade-domain', 'openblade-protocol', 'openblade-types')
}
$metadataJson = cargo metadata --locked --offline --format-version 1
if ($LASTEXITCODE -ne 0) { throw 'Cargo metadata failed.' }
$metadata = ($metadataJson -join "`n") | ConvertFrom-Json
if (@($metadata.packages).Count -ne $expectedDependencies.Count -or
    @($metadata.workspace_members).Count -ne $expectedDependencies.Count) {
    throw 'Phase 2 admits only the four reviewed workspace crates and no external packages.'
}
foreach ($package in $metadata.packages) {
    if (-not $expectedDependencies.ContainsKey($package.name) -or $null -ne $package.source) {
        throw "Unreviewed package: $($package.name)"
    }
    $actual = @($package.dependencies | ForEach-Object {
        if ($null -eq $_.path -or $null -ne $_.kind -or $_.optional -or $null -ne $_.target) {
            throw "Only unconditional, local normal dependencies are admitted: $($package.name)"
        }
        $_.name
    } | Sort-Object)
    $expected = @($expectedDependencies[$package.name] | Sort-Object)
    if (($actual -join ',') -cne ($expected -join ',')) {
        throw "Dependency boundary changed for $($package.name): $($actual -join ',')"
    }
    if (@($package.targets).Count -ne 1 -or
        ($package.targets[0].crate_types -join ',') -ne 'lib') {
        throw "Phase 2 admits only ordinary Rust libraries: $($package.name)"
    }
}
Write-Output 'Workspace boundaries passed: four local crates, inward dependencies, ordinary libraries.'
