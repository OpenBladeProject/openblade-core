[CmdletBinding()]
param([string]$Target)

Set-StrictMode -Version 3.0
$ErrorActionPreference = 'Stop'
$repository = Split-Path -Parent $PSScriptRoot
$previousLibrary = $env:OPENBLADE_NATIVE_LIBRARY
$previousPolicyLibrary = $env:OPENBLADE_POLICY_ONLY_LIBRARY
Push-Location $PSScriptRoot
try {
    $version = rustc -vV
    if ($LASTEXITCODE -ne 0) { throw 'Cannot determine the Rust host.' }
    $hostTarget = (($version | Where-Object { $_ -like 'host: *' }) -replace '^host: ', '').Trim()
    if (-not $Target) { $Target = $hostTarget }
    $reviewedTargets = @(
        'x86_64-pc-windows-msvc',
        'x86_64-unknown-linux-gnu',
        'x86_64-apple-darwin',
        'aarch64-apple-darwin'
    )
    if ($Target -cnotin $reviewedTargets) {
        throw "Unsupported native interop target: $Target. Only the four reviewed native CI targets are supported."
    }
    if ($Target -cne $hostTarget) { throw 'Native interop tests must execute on the selected target host.' }

    # Always select x64, even inside a pre-existing x86 developer prompt.
    # Import the environment only into this process, without installing tools.
    if ($IsWindows) {
        $vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio/Installer/vswhere.exe'
        if (-not (Test-Path -LiteralPath $vswhere)) { throw 'MSVC Build Tools are required for the C header test.' }
        $installation = & $vswhere -latest -products '*' -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath
        if ($LASTEXITCODE -ne 0 -or -not $installation) { throw 'MSVC Build Tools were not found.' }
        $devcmd = Join-Path $installation 'Common7/Tools/VsDevCmd.bat'
        $environment = & $env:ComSpec /d /c "call `"$devcmd`" -no_logo -arch=x64 -host_arch=x64 >nul && set"
        if ($LASTEXITCODE -ne 0) { throw 'MSVC developer environment initialization failed.' }
        foreach ($line in $environment) {
            if ($line -match '^([^=]+)=(.*)$') { [Environment]::SetEnvironmentVariable($Matches[1], $Matches[2], 'Process') }
        }
    }

    cargo build --locked -p openblade-ffi --target $Target
    if ($LASTEXITCODE -ne 0) { throw 'Native FFI build failed.' }
    $metadataJson = cargo metadata --locked --offline --no-deps --format-version 1
    if ($LASTEXITCODE -ne 0) { throw 'Cargo metadata failed.' }
    $metadata = ($metadataJson -join "`n") | ConvertFrom-Json
    $output = Join-Path $metadata.target_directory "$Target/debug"
    if ($IsWindows) {
        $library = Join-Path $output 'openblade_ffi.dll'
        $staticLibrary = Join-Path $output 'openblade_ffi.lib'
        $linkLibrary = Join-Path $output 'openblade_ffi.dll.lib'
        $smoke = Join-Path $output 'header_smoke.exe'
    } elseif ($IsMacOS) {
        $library = Join-Path $output 'libopenblade_ffi.dylib'
        $staticLibrary = Join-Path $output 'libopenblade_ffi.a'
        $smoke = Join-Path $output 'header_smoke'
    } elseif ($IsLinux) {
        $library = Join-Path $output 'libopenblade_ffi.so'
        $staticLibrary = Join-Path $output 'libopenblade_ffi.a'
        $smoke = Join-Path $output 'header_smoke'
    } else { throw 'This native host is not validated.' }
    foreach ($artifact in @($library, $staticLibrary)) {
        if (-not (Test-Path -LiteralPath $artifact -PathType Leaf)) { throw "Missing native build output: $artifact" }
    }
    $headerDirectory = Join-Path $PSScriptRoot 'crates/openblade-ffi/include'
    $source = Join-Path $PSScriptRoot 'tests/header_smoke.c'
    if ($IsWindows) {
        & cl.exe /nologo /std:c11 /W4 /WX "/I$headerDirectory" $source "/Fo$(Join-Path $output 'header_smoke.obj')" "/Fe$smoke" /link $linkLibrary
    } else {
        & cc -std=c11 -Wall -Wextra -Werror "-I$headerDirectory" $source $library "-Wl,-rpath,$output" -o $smoke
    }
    if ($LASTEXITCODE -ne 0) { throw 'C header compile/link failed.' }
    & $smoke
    if ($LASTEXITCODE -ne 0) { throw 'C header/native ABI smoke failed.' }

    # A separate, test-only C library proves policy loading needs no codec exports.
    # It never supplies parity results and is never included in product packaging.
    $fixtureDirectory = Join-Path $output 'policy-only'
    New-Item -ItemType Directory -Path $fixtureDirectory -Force | Out-Null
    $fixtureLibrary = Join-Path $fixtureDirectory (Split-Path -Leaf $library)
    $fixtureSource = Join-Path $PSScriptRoot 'tests/policy_only.c'
    if ($IsWindows) {
        & cl.exe /nologo /std:c11 /W4 /WX /LD "/I$headerDirectory" $fixtureSource "/Fo$(Join-Path $fixtureDirectory 'policy_only.obj')" "/Fe$fixtureLibrary" /link "/IMPLIB:$(Join-Path $fixtureDirectory 'policy_only.lib')" /EXPORT:ob_policy_abi_version /EXPORT:ob_v1_apply_profile /EXPORT:ob_v1_apply_display
    } elseif ($IsMacOS) {
        & cc -std=c11 -Wall -Wextra -Werror -dynamiclib "-I$headerDirectory" $fixtureSource -o $fixtureLibrary
    } else {
        & cc -std=c11 -Wall -Wextra -Werror -shared -fPIC "-I$headerDirectory" $fixtureSource -o $fixtureLibrary
    }
    if ($LASTEXITCODE -ne 0) { throw 'Policy-only loader fixture build failed.' }
    $env:OPENBLADE_POLICY_ONLY_LIBRARY = [IO.Path]::GetFullPath($fixtureLibrary)

    $env:OPENBLADE_NATIVE_LIBRARY = [IO.Path]::GetFullPath($library)
    dotnet test (Join-Path $repository 'OpenBlade.Native.slnx') --configuration Release
    if ($LASTEXITCODE -ne 0) { throw '.NET/native integration tests failed.' }
    Write-Output "Native interop passed for $Target (C header, cdylib/staticlib outputs, .NET tests)."
}
finally {
    $env:OPENBLADE_NATIVE_LIBRARY = $previousLibrary
    $env:OPENBLADE_POLICY_ONLY_LIBRARY = $previousPolicyLibrary
    Pop-Location
}
