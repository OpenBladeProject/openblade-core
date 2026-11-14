[CmdletBinding()]
param(
    [ValidateSet('PrepareOnly', 'VerifySelected')][string]$Mode = 'VerifySelected',
    [ValidateSet('Debug', 'Release')][string]$Configuration = 'Debug',
    [string]$OutputRoot = (Join-Path ([IO.Path]::GetTempPath()) 'openblade-native-fixtures')
)
$ErrorActionPreference = 'Stop'
$repository = Split-Path -Parent $PSScriptRoot
. (Join-Path $PSScriptRoot 'NativeWindowsFixtures.ps1')
Invoke-OpenBladeNativeFixtureScope -Repository $repository -Configuration $Configuration -Mode $Mode -OutputRoot $OutputRoot
