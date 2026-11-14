[CmdletBinding()]
param()

Set-StrictMode -Version 3.0
$ErrorActionPreference = 'Stop'

# Use real, offline Cargo metadata in disposable copies; never mutate the checkout.
$originalLocation = Get-Location
$temporaryRoot = [IO.Path]::GetFullPath([IO.Path]::GetTempPath())
$probe = Join-Path $temporaryRoot ('openblade-rust-check-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $probe | Out-Null
try {
    Push-Location (Split-Path -Parent $PSScriptRoot)
    try { & (Join-Path $PSScriptRoot 'Check-Workspace.ps1') }
    finally { Pop-Location }
    if ((Get-Location).Path -ne $originalLocation.Path) { throw 'The checker changed caller location.' }

    foreach ($file in @('Cargo.toml', 'Cargo.lock', 'rust-toolchain.toml', 'Check-Workspace.ps1')) {
        Copy-Item -LiteralPath (Join-Path $PSScriptRoot $file) -Destination $probe
    }
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'crates') -Destination $probe -Recurse

    Push-Location $probe
    try {
        $metadataJson = cargo metadata --locked --offline --no-deps --format-version 1
        if ($LASTEXITCODE -ne 0) { throw 'Test workspace metadata failed.' }
        $testedVersion = (($metadataJson -join "`n") | ConvertFrom-Json).packages[0].rust_version
    }
    finally { Pop-Location }

    $cases = @(
        @{ Path = 'Cargo.toml'; Before = '[profile.dev]'; After = '[profile.custom]'; Error = 'Required profile.dev policy*' },
        @{ Path = 'Cargo.toml'; Before = 'panic = "unwind"'; After = 'panic = "abort"'; Error = 'Required profile.* policy*' },
        @{ Path = 'crates/openblade-ffi/Cargo.toml'; Before = 'unsafe_op_in_unsafe_fn = "deny"'; After = 'unsafe_op_in_unsafe_fn = "allow"'; Error = 'Required lints.rust policy*' },
        @{ Path = 'crates/openblade-ffi/Cargo.toml'; Before = 'unsafe_op_in_unsafe_fn = "deny"'; After = ''; Error = 'Required lints.rust policy*' },
        @{ Path = 'Cargo.toml'; Before = 'rust-version = "' + $testedVersion + '"'; After = 'rust-version = "1.85.0"'; Error = 'MSRV must equal*' },
        @{ Path = 'Cargo.toml'; Before = 'unsafe_code = "forbid"'; After = 'unsafe_code = "allow"'; Error = 'Required workspace.lints.rust policy*' },
        @{ Path = 'crates/openblade-types/Cargo.toml'; Before = "[lints]`nworkspace = true"; After = ''; Error = 'Required lints policy*' },
        @{ Path = 'crates/openblade-types/src/lib.rs'; Before = '#![no_std]'; After = ''; Error = 'The portable crate must retain no_std*' },
        @{ Path = 'crates/openblade-protocol/Cargo.toml'; Before = '[dev-dependencies]'; After = '[build-dependencies]'; Error = 'Unreviewed dependency kind*' },
        @{ Path = 'crates/openblade-protocol/Cargo.toml'; Before = 'name = "openblade-protocol"'; After = "name = 'openblade-protocol'`nautotests = false"; Error = 'Reviewed integration tests are missing*' },
        @{ Path = 'crates/openblade-types/Cargo.toml'; Before = '[lints]'; After = "[lib]`ncrate-type = ['cdylib']`n`n[lints]"; Error = 'Reviewed library outputs are required*' },
        @{ Path = 'crates/openblade-protocol/Cargo.toml'; Before = '=1.0.149'; After = '^1.0.149'; Error = 'Unreviewed dependency kind*' },
        @{ Path = 'crates/openblade-protocol/Cargo.toml'; Before = "features = ['derive']"; After = "features = ['derive', 'rc']"; Error = 'Unreviewed fixture-reader features*' },
        @{ Path = 'crates/openblade-protocol/Cargo.toml'; Before = "serde_json = '=1.0.149'"; After = "serde_json = { version = '=1.0.149', default-features = false }"; Error = 'Unreviewed dependency kind*' },
        @{ Path = 'crates/openblade-protocol/Cargo.toml'; Before = "serde_json = '=1.0.149'"; After = ''; Error = 'Fixture-reader dev dependencies changed*' },
        @{ Path = 'crates/openblade-protocol/Cargo.toml'; Before = '[dev-dependencies]'; After = "[[bin]]`nname = 'unexpected'`npath = 'src/lib.rs'`n`n[dev-dependencies]"; Error = 'Unreviewed target*' },
        @{ Path = 'crates/openblade-protocol/Cargo.toml'; Before = '[dev-dependencies]'; After = "[[test]]`nname = 'unexpected'`npath = 'src/lib.rs'`n`n[dev-dependencies]"; Error = 'Unreviewed target*' },
        @{ Path = 'crates/openblade-domain/Cargo.toml'; Before = 'openblade-types.workspace = true'; After = "openblade-types.workspace = true`nopenblade-protocol.workspace = true"; Error = 'Production dependency boundary changed*' },
        @{ Path = 'crates/openblade-domain/Cargo.toml'; Before = '[dev-dependencies]'; After = '[build-dependencies]'; Error = 'Unreviewed dependency kind*' },
        @{ Path = 'crates/openblade-domain/src/lib.rs'; Before = '#![no_std]'; After = ''; Error = 'The portable crate must retain no_std*' },
        @{ Path = 'crates/openblade-domain/Cargo.toml'; Before = 'name = "openblade-domain"'; After = "name = 'openblade-domain'`nautotests = false"; Error = 'Reviewed integration tests are missing*' },
        @{ Path = 'crates/openblade-domain/Cargo.toml'; Before = '=1.0.149'; After = '^1.0.149'; Error = 'Unreviewed dependency kind*' },
        @{ Path = 'crates/openblade-domain/Cargo.toml'; Before = "features = ['derive']"; After = "features = ['derive', 'rc']"; Error = 'Unreviewed fixture-reader features*' },
        @{ Path = 'crates/openblade-domain/Cargo.toml'; Before = "serde_json = '=1.0.149'"; After = ''; Error = 'Fixture-reader dev dependencies changed*' },
        @{ Path = 'crates/openblade-domain/Cargo.toml'; Before = '[dev-dependencies]'; After = "[[test]]`nname = 'unexpected'`npath = 'src/lib.rs'`n`n[dev-dependencies]"; Error = 'Unreviewed target*' },
        @{ Path = 'crates/openblade-types/Cargo.toml'; Before = '[lints]'; After = "[dependencies]`nserde_json = '=1.0.149'`n`n[lints]"; Error = 'Unreviewed dependency kind*' }
    )
    $windowsManifest = 'crates/openblade-platform-windows/Cargo.toml'
    $cases += @(
        @{ Path = $windowsManifest; Before = "target.'cfg(windows)'.dependencies"; After = 'dependencies'; Error = 'Unreviewed dependency kind*' },
        @{ Path = $windowsManifest; Before = 'Win32_Graphics_Gdi'; After = 'Win32_Graphics_Gdi", "Win32_System_Registry'; Error = 'Conditional, optional or renamed dependencies*' },
        @{ Path = $windowsManifest; Before = 'unsafe_code = "deny"'; After = 'unsafe_code = "allow"'; Error = 'Required lints.rust policy*' }
    )

    # The std CLI is a leaf: only its exact reviewed binary and registry roots.
    $cliManifest = 'crates/openblade-status-cli/Cargo.toml'
    $cases += @(
        @{ Path = $cliManifest; Before = '[lints]'; After = "[features]`nextra = []`n`n[lints]"; Error = 'CLI package features are not admitted*' },
        @{ Path = $cliManifest; Before = "[lints]`nworkspace = true"; After = ''; Error = 'Required lints policy*' },
        @{ Path = $cliManifest; Before = '[dependencies]'; After = '[dev-dependencies]'; Error = 'Unreviewed dependency kind*' },
        @{ Path = $cliManifest; Before = '[dependencies]'; After = '[build-dependencies]'; Error = 'Unreviewed dependency kind*' },
        @{ Path = $cliManifest; Before = '=1.54.1'; After = '^1.54.1'; Error = 'Unreviewed dependency kind*' },
        @{ Path = $cliManifest; Before = '"signal", "macros"'; After = '"signal", "macros", "rt-multi-thread"'; Error = 'Unreviewed CLI dependency features*' },
        @{ Path = $cliManifest; Before = '"signal", "macros"'; After = '"macros"'; Error = 'Unreviewed CLI dependency features*' },
        @{ Path = $cliManifest; Before = 'version = "=1.54.1"'; After = 'version = "=1.54.1", default-features = false'; Error = 'Unreviewed dependency kind*' },
        @{ Path = $cliManifest; Before = 'serde_json = "=1.0.149"'; After = ''; Error = 'CLI dependencies changed*' },
        @{ Path = $cliManifest; Before = '[dependencies]'; After = "[dependencies]`nopenblade-types = { path = '../openblade-types' }"; Error = 'Production dependency boundary changed*' },
        @{ Path = $cliManifest; Before = '[lints]'; After = "[dev-dependencies]`nserde_json = '=1.0.149'`n`n[lints]"; Error = 'Unreviewed dependency kind*' },
        @{ Path = $cliManifest; Before = '[lints]'; After = "[[bin]]`nname = 'unexpected'`npath = 'src/main.rs'`n`n[lints]"; Error = 'The CLI requires only its reviewed*' },
        @{ Path = $cliManifest; Before = '[lints]'; After = "[lib]`npath = 'src/main.rs'`n`n[lints]"; Error = 'The CLI requires only its reviewed*' },
        @{ Path = $cliManifest; Before = '[lints]'; After = "[[test]]`nname = 'unexpected'`npath = 'src/main.rs'`n`n[lints]"; Error = 'The CLI requires only its reviewed*' },
        @{ Path = 'crates/openblade-types/Cargo.toml'; Before = '[lints]'; After = "[dependencies]`ntokio = { version = '=1.54.1', features = ['rt'] }`n`n[lints]"; Error = 'Unreviewed dependency kind*' },
        @{ Path = 'crates/openblade-types/Cargo.toml'; Before = '[lints]'; After = "[dependencies]`nopenblade-status-cli = { path = '../openblade-status-cli' }`n`n[lints]"; Error = 'Production dependency boundary changed*' }
    )
    # Protect all three inner crates, rather than only the first member.
    foreach ($crate in @('openblade-protocol', 'openblade-domain')) {
        $cases += @{ Path = "crates/$crate/Cargo.toml"; Before = "[lints]`nworkspace = true"; After = ''; Error = 'Required lints policy*' }
    }
    $ffiManifest = [IO.File]::ReadAllText((Join-Path $probe 'crates/openblade-ffi/Cargo.toml'))
    $crateOutputLine = [regex]::Match($ffiManifest, '(?m)^crate-type\s*=.*$').Value
    if (-not $crateOutputLine) { throw 'FFI crate output declaration not found.' }
    $cases += @{ Path = 'crates/openblade-ffi/Cargo.toml'; Before = $crateOutputLine; After = 'crate-type = ["rlib", "cdylib"]'; Error = 'Reviewed library outputs are required*' }
    $cases += @{ Path = 'crates/openblade-ffi/Cargo.toml'; Before = $crateOutputLine; After = 'crate-type = ["rlib", "cdylib", "staticlib", "lib"]'; Error = 'Reviewed library outputs are required*' }
    $cases += @{ Path = 'crates/openblade-ffi/Cargo.toml'; Before = '[lib]'; After = "[[bin]]`nname = 'unexpected'`npath = 'src/lib.rs'`n`n[lib]"; Error = 'Unreviewed target*' }
    $workspaceManifest = [IO.File]::ReadAllText((Join-Path $probe 'Cargo.toml'))
    $releaseProfile = [regex]::Match($workspaceManifest, '(?m)^\[profile\.release\]\r?\n[^\[]*').Value
    if (-not $releaseProfile.Contains('panic = "unwind"')) { throw 'Release panic policy not found.' }
    $cases += @{ Path = 'Cargo.toml'; Before = $releaseProfile; After = $releaseProfile.Replace('panic = "unwind"', 'panic = "abort"'); Error = 'Required profile.release policy*' }
    foreach ($case in $cases) {
        $path = Join-Path $probe $case.Path
        $saved = [IO.File]::ReadAllText($path)
        if (-not $saved.Contains($case.Before)) { throw "Negative test input not found: $($case.Path)" }
        [IO.File]::WriteAllText($path, $saved.Replace($case.Before, $case.After), [Text.UTF8Encoding]::new($false))
        $savedLock = [IO.File]::ReadAllText((Join-Path $probe 'Cargo.lock'))
        Push-Location $probe
        try {
            cargo generate-lockfile --offline
            if ($LASTEXITCODE -ne 0) { throw 'Negative test lockfile generation failed.' }
        }
        finally { Pop-Location }
        $rejected = $false
        try { & (Join-Path $probe 'Check-Workspace.ps1') }
        catch {
            if ($_.Exception.Message -notlike $case.Error) { throw }
            $rejected = $true
        }
        finally {
            [IO.File]::WriteAllText($path, $saved, [Text.UTF8Encoding]::new($false))
            [IO.File]::WriteAllText((Join-Path $probe 'Cargo.lock'), $savedLock, [Text.UTF8Encoding]::new($false))
        }
        if (-not $rejected) { throw "Workspace policy drift was accepted: $($case.Path)" }
    }

    # Exercise external-package name/source admission using real resolved metadata.
    # Mutate one field in a snapshot rather than acquire an unrelated registry.
    Push-Location $probe
    try {
        $snapshotJson = cargo metadata --locked --offline --format-version 1
        if ($LASTEXITCODE -ne 0) { throw 'External-package test metadata failed.' }
    }
    finally { Pop-Location }
    foreach ($externalCase in @(
        @{ Field = 'name'; Value = 'unreviewed-parser' },
        @{ Field = 'source'; Value = 'registry+https://unreviewed.invalid/index' }
    )) {
        $snapshot = ($snapshotJson -join "`n") | ConvertFrom-Json
        $external = @($snapshot.packages | Where-Object { $null -ne $_.source })[0]
        $external.($externalCase.Field) = $externalCase.Value
        $fixtureMetadataJson = $snapshot | ConvertTo-Json -Depth 100 -Compress
        $rejected = $false
        function cargo {
            if (($args -join ' ') -cne 'metadata --locked --offline --format-version 1') {
                throw 'Unexpected Cargo command during metadata snapshot test.'
            }
            $fixtureMetadataJson
        }
        try { & (Join-Path $probe 'Check-Workspace.ps1') }
        catch {
            if ($_.Exception.Message -notlike 'Unreviewed external package:*') { throw }
            $rejected = $true
        }
        finally { Remove-Item Function:cargo }
        if (-not $rejected) { throw "Unreviewed external package $($externalCase.Field) was accepted." }
    }
    # Header comments must end at their newline, leaving assignments in the body.
    foreach ($entry in @(
        @{ Path = 'Cargo.toml'; Header = '[workspace.lints.rust]' },
        @{ Path = 'crates/openblade-types/Cargo.toml'; Header = '[lints]' },
        @{ Path = 'crates/openblade-protocol/Cargo.toml'; Header = '[lints]' },
        @{ Path = 'crates/openblade-domain/Cargo.toml'; Header = '[lints]' },
        @{ Path = 'crates/openblade-ffi/Cargo.toml'; Header = '[lints.rust]' },
        @{ Path = 'crates/openblade-status-cli/Cargo.toml'; Header = '[lints]' },
        @{ Path = 'Cargo.toml'; Header = '[profile.dev]' },
        @{ Path = 'Cargo.toml'; Header = '[profile.release]' }
    )) {
        $path = Join-Path $probe $entry.Path
        $text = [IO.File]::ReadAllText($path).Replace($entry.Header, $entry.Header + ' # retain workspace policies')
        [IO.File]::WriteAllText($path, $text, [Text.UTF8Encoding]::new($false))
    }
    & (Join-Path $probe 'Check-Workspace.ps1')

    # Equivalent TOML spelling must be handled by rustup rather than a YAML regex.
    $toolchain = Join-Path $probe 'rust-toolchain.toml'
    $text = [IO.File]::ReadAllText($toolchain).Replace('channel = "' + $testedVersion + '"', "channel   = '$testedVersion' # exact stable release")
    [IO.File]::WriteAllText($toolchain, $text, [Text.UTF8Encoding]::new($false))
    Push-Location $probe
    try {
        rustup toolchain install --no-self-update
        if ($LASTEXITCODE -ne 0) { throw 'Rustup failed to read valid toolchain TOML.' }
        & (Join-Path $probe 'Check-Workspace.ps1')
    }
    finally { Pop-Location }
    Write-Output 'Workspace guard tests passed: caller location, compiler policies, MSRV, production/dev features and roots, CLI isolation and targets, external package admission, test targets and equivalent TOML.'
}
finally {
    # Only remove the unique directory this invocation created under temporary storage.
    $resolvedProbe = [IO.Path]::GetFullPath($probe)
    if ((Split-Path -Parent $resolvedProbe).TrimEnd('\', '/') -ne $temporaryRoot.TrimEnd('\', '/') -or
        (Split-Path -Leaf $resolvedProbe) -notmatch '^openblade-rust-check-[0-9a-f]{32}$') {
        throw 'Unexpected temporary cleanup path.'
    }
    Remove-Item -LiteralPath $resolvedProbe -Recurse -Force
}
