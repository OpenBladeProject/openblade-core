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
        $metadataJson = cargo metadata --locked --offline --format-version 1
        if ($LASTEXITCODE -ne 0) { throw 'Test workspace metadata failed.' }
        $testedVersion = (($metadataJson -join "`n") | ConvertFrom-Json).packages[0].rust_version
    }
    finally { Pop-Location }

    $cases = @(
        @{ Path = 'Cargo.toml'; Before = 'rust-version = "' + $testedVersion + '"'; After = 'rust-version = "1.85.0"'; Error = 'MSRV must equal*' },
        @{ Path = 'Cargo.toml'; Before = 'unsafe_code = "forbid"'; After = 'unsafe_code = "allow"'; Error = 'Required workspace.lints.rust policy*' },
        @{ Path = 'crates/openblade-types/Cargo.toml'; Before = "[lints]`nworkspace = true"; After = ''; Error = 'Required lints policy*' },
        @{ Path = 'crates/openblade-types/src/lib.rs'; Before = '#![no_std]'; After = ''; Error = 'The portable crate must retain no_std*' }
    )
    foreach ($case in $cases) {
        $path = Join-Path $probe $case.Path
        $saved = [IO.File]::ReadAllText($path)
        if (-not $saved.Contains($case.Before)) { throw "Negative test input not found: $($case.Path)" }
        [IO.File]::WriteAllText($path, $saved.Replace($case.Before, $case.After), [Text.UTF8Encoding]::new($false))
        $rejected = $false
        try { & (Join-Path $probe 'Check-Workspace.ps1') }
        catch {
            if ($_.Exception.Message -notlike $case.Error) { throw }
            $rejected = $true
        }
        finally { [IO.File]::WriteAllText($path, $saved, [Text.UTF8Encoding]::new($false)) }
        if (-not $rejected) { throw "Workspace policy drift was accepted: $($case.Path)" }
    }

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
    Write-Output 'Workspace guard tests passed: root invocation, policy drift, MSRV and equivalent toolchain TOML.'
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
