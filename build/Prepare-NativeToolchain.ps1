[CmdletBinding()]
param()

Set-StrictMode -Version 3.0
$ErrorActionPreference = 'Stop'
$repository = Split-Path -Parent $PSScriptRoot
$pin = [regex]::Match((Get-Content (Join-Path $repository 'rust/rust-toolchain.toml') -Raw), '(?m)^channel = "([0-9.]+)"$').Groups[1].Value
if (-not $pin) { throw 'Missing exact Rust toolchain pin.' }
& rustup toolchain install $pin --profile minimal
if ($LASTEXITCODE -ne 0) { throw 'Pinned Rust toolchain setup failed.' }
& cargo "+$pin" fetch --locked --manifest-path (Join-Path $repository 'rust/Cargo.toml')
if ($LASTEXITCODE -ne 0) { throw 'Locked native inputs could not be fetched.' }
