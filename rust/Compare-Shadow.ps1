[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [ValidateNotNullOrEmpty()]
    [string]$LibraryPath
)

Set-StrictMode -Version 3.0
$ErrorActionPreference = 'Stop'

# PURE developer validation only: load an existing Windows x64 artifact and fixtures.
# The normal .NET build also builds its required native payload. No devices or installed products are used.
if ([Environment]::OSVersion.Platform -ne [PlatformID]::Win32NT -or [Runtime.InteropServices.RuntimeInformation]::ProcessArchitecture -ne 'X64') {
    throw 'Shadow comparison requires a Windows x64 PowerShell process.'
}
if ($LibraryPath -notmatch '^(?:[A-Za-z]:\\|\\\\[^\\]+\\[^\\]+\\)' -or
    -not [string]::Equals($LibraryPath, [IO.Path]::GetFullPath($LibraryPath), [StringComparison]::Ordinal) -or
    [IO.Path]::GetFileName($LibraryPath) -cne 'openblade_ffi.dll' -or
    -not (Test-Path -LiteralPath $LibraryPath -PathType Leaf)) {
    throw 'LibraryPath must be a normalized absolute path to an existing openblade_ffi.dll.'
}

$previousLibrary = $env:OPENBLADE_NATIVE_LIBRARY
try {
    $env:OPENBLADE_NATIVE_LIBRARY = $LibraryPath
    $repository = Split-Path -Parent $PSScriptRoot
    $project = Join-Path $repository 'tests/OpenBlade.Protocol.Tests/OpenBlade.Protocol.Tests.csproj'
    dotnet test $project --configuration Release -p:OpenBladeNativeShadow=true --filter 'FullyQualifiedName~NativeShadowComparisonTests'
    if ($LASTEXITCODE -ne 0) { throw 'PURE native shadow comparison failed.' }
    Write-Output 'PURE shadow comparison passed: 9 encoding and 20 response fixtures compared with the frozen independent managed baseline.'
}
finally {
    $env:OPENBLADE_NATIVE_LIBRARY = $previousLibrary
}
