[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$Output,
    [Parameter(Mandatory)][string]$ProductVersion,
    [Parameter(Mandatory)][string]$Configuration
)

Set-StrictMode -Version 3.0
$ErrorActionPreference = 'Stop'
$repository = Split-Path -Parent $PSScriptRoot
$outputPath = [IO.Path]::GetFullPath($Output)
New-Item -ItemType Directory -Path $outputPath -Force | Out-Null
function Get-NativeSha256([string]$Path) {
    $stream = [IO.File]::OpenRead($Path)
    $algorithm = [Security.Cryptography.SHA256]::Create()
    try { return ([BitConverter]::ToString($algorithm.ComputeHash($stream))).Replace('-', '').ToLowerInvariant() }
    finally { $algorithm.Dispose(); $stream.Dispose() }
}
Push-Location (Join-Path $repository 'rust')
try {
    $pin = [regex]::Match((Get-Content rust-toolchain.toml -Raw), '(?m)^channel = "([0-9.]+)"$').Groups[1].Value
    if (-not $pin) { throw 'Missing exact Rust toolchain pin.' }
    $version = & rustc "+$pin" -vV
    if ($LASTEXITCODE -ne 0) { throw 'The pinned Rust compiler is required for ordinary builds.' }
    $target = (($version | Where-Object { $_ -like 'host: *' }) -replace '^host: ', '').Trim()
    if ($target -cnotin @('x86_64-pc-windows-msvc', 'x86_64-unknown-linux-gnu', 'x86_64-apple-darwin', 'aarch64-apple-darwin')) {
        throw 'The native component requires a reviewed native host target.'
    }
    if ($target -eq 'x86_64-pc-windows-msvc') {
        $vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio/Installer/vswhere.exe'
        $installation = & $vswhere -latest -products '*' -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath
        if ($LASTEXITCODE -ne 0 -or -not $installation) { throw 'MSVC Build Tools with Windows SDK are required.' }
        $devcmd = Join-Path $installation 'Common7/Tools/VsDevCmd.bat'
        $environment = & $env:ComSpec /d /c "call `"$devcmd`" -no_logo -arch=x64 -host_arch=x64 >nul && set"
        if ($LASTEXITCODE -ne 0) { throw 'MSVC environment initialization failed.' }
        foreach ($line in $environment) {
            if ($line -match '^([^=]+)=(.*)$') { [Environment]::SetEnvironmentVariable($Matches[1], $Matches[2], 'Process') }
        }
        $env:CARGO_TARGET_X86_64_PC_WINDOWS_MSVC_LINKER = (Get-Command link.exe -ErrorAction Stop).Source
        $env:RUSTFLAGS = '-C target-feature=+crt-static'
    }
    $env:CARGO_ENCODED_RUSTFLAGS = $null
    $env:CARGO_PROFILE_DEV_PANIC = 'unwind'
    $env:CARGO_PROFILE_RELEASE_PANIC = 'unwind'
    $env:CARGO_TARGET_DIR = if ($env:OS -eq 'Windows_NT') { 'C:\build-cache\cargo\OpenSynapse' } else { Join-Path ([IO.Path]::GetTempPath()) 'openblade-cargo-cache' }
    $arguments = @("+$pin", 'build', '--locked', '--offline', '-p', 'openblade-ffi', '--lib', '--target', $target)
    $profile = 'debug'
    if ($Configuration -eq 'Release') { $arguments += '--release'; $profile = 'release' }
    & cargo @arguments
    if ($LASTEXITCODE -ne 0) { throw 'Required native component build failed.' }
    $name = if ($target -like '*windows*') { 'openblade_ffi.dll' }
        elseif ($target -like '*apple*') { 'libopenblade_ffi.dylib' } else { 'libopenblade_ffi.so' }
    Copy-Item -LiteralPath (Join-Path $env:CARGO_TARGET_DIR "$target/$profile/$name") -Destination (Join-Path $outputPath $name) -Force
    $revision = & git -C $repository rev-parse HEAD
    if ($LASTEXITCODE -ne 0 -or $revision -notmatch '^[0-9a-f]{40}$') { throw 'Cannot determine native source revision.' }
    # Development uses current working bytes, including uncommitted changes.
    # Release packaging retains its stricter committed-input provenance recipe.
    $inputs = @()
    foreach ($file in @(Get-ChildItem -LiteralPath (Join-Path $repository 'rust') -Recurse -File |
        Where-Object { $_.FullName -notmatch '[/\\]target[/\\]' } | Sort-Object FullName)) {
        $relativePath = $file.FullName.Substring($repository.TrimEnd('\', '/').Length + 1).Replace('\', '/')
        $inputs += [ordered]@{ path = $relativePath; sha256 = Get-NativeSha256 $file.FullName }
    }
    $manifest = [ordered]@{
        schemaVersion = 1; abiVersion = '0x00010000'; fileName = $name; target = $target
        productVersion = $ProductVersion; sourceRevision = $revision
        sha256 = Get-NativeSha256 (Join-Path $outputPath $name)
        inputs = $inputs
    }
    $text = $manifest | ConvertTo-Json -Depth 8
    $manifestPath = Join-Path $outputPath 'OpenBlade.Native.provenance.json'
    if (-not (Test-Path -LiteralPath $manifestPath) -or [IO.File]::ReadAllText($manifestPath) -cne $text) {
        [IO.File]::WriteAllText($manifestPath, $text, [Text.UTF8Encoding]::new($false))
    }
}
finally { Pop-Location }
