[CmdletBinding()]
param()

Set-StrictMode -Version 3.0
$ErrorActionPreference = 'Stop'
$repository = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
. (Join-Path $repository 'build\development-deployment\LocalDevBuildVersion.ps1')

function Assert-True {
    param(
        [Parameter(Mandatory)][bool]$Condition,
        [Parameter(Mandatory)][string]$Message
    )

    if (-not $Condition) {
        throw $Message
    }
}

$temporaryRoot = Join-Path ([IO.Path]::GetTempPath()) (
    'openblade-local-dev-build-version-' + [Guid]::NewGuid().ToString('N'))
try {
    $statePath = Join-Path $temporaryRoot 'state.txt'
    $first = Get-OpenBladeNextLocalDevBuildVersion `
        -StatePath $statePath `
        -UnixTimeMilliseconds 1800000000000
    $second = Get-OpenBladeNextLocalDevBuildVersion `
        -StatePath $statePath `
        -UnixTimeMilliseconds 1800000000000
    Assert-True ($first -match '^65535\.\d+\.\d+\.\d+$') `
        'The local-development version must use the reserved four-part range.'
    Assert-True ([Version]$second -gt [Version]$first) `
        'Successive builds at the same clock value must receive increasing Burn versions.'
    Assert-True ((Get-Content -LiteralPath $statePath -Raw) -eq '1800000000001') `
        'The local-development version sequence must be persisted before publication.'

    [IO.File]::WriteAllText($statePath, '01')
    try {
        Get-OpenBladeNextLocalDevBuildVersion `
            -StatePath $statePath `
            -UnixTimeMilliseconds 1800000000000 | Out-Null
        throw 'Malformed local-development state was accepted.'
    }
    catch {
        Assert-True (
            $_.Exception.Message -eq 'The local-development build-version state is malformed.') `
            "Unexpected malformed-state failure '$($_.Exception.Message)'."
    }
}
finally {
    if (Test-Path -LiteralPath $temporaryRoot) {
        Remove-Item -LiteralPath $temporaryRoot -Recurse -Force
    }
}

Write-Host 'Local-development build-version tests passed.'
