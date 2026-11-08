[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')]
    [string] $Configuration = 'Debug',
    [switch] $UseExistingNativePayload
)
$ErrorActionPreference = 'Stop'
if ($env:OS -ne 'Windows_NT') {
    throw 'Production HID actor activation probes require Windows; all HID resources are test doubles.'
}
$root = Split-Path -Parent $PSScriptRoot
$project = Join-Path $root 'tests/OpenBlade.NativeFailureProbe/OpenBlade.NativeFailureProbe.csproj'
$buildArguments = @('build', $project, '-c', $Configuration, '-p:NuGetAudit=false')
if ($UseExistingNativePayload) {
    $buildArguments += '-p:OpenBladeNativeDevelopmentBuild=false'
}
& dotnet @buildArguments
if ($LASTEXITCODE -ne 0) {
    throw "Production activation probe build failed: $LASTEXITCODE"
}
$probe = Join-Path $root "tests/OpenBlade.NativeFailureProbe/bin/$Configuration/net10.0-windows/OpenBlade.Protocol.Tests.dll"
foreach ($scenario in @('batch-encode', 'query-validate', 'policy-failure')) {
    # A fresh process owns the deliberately disposed singleton for each scenario.
    & dotnet $probe $scenario
    if ($LASTEXITCODE -ne 0) {
        throw "Production activation probe $scenario failed: $LASTEXITCODE"
    }
}
