[CmdletBinding()]
param()

Set-StrictMode -Version 3.0
$ErrorActionPreference = 'Stop'

# These small policy checks intentionally cover the scaffold's manifest sections.
function Assert-ManifestSetting {
    param([string]$Path, [string]$Section, [string]$Assignment)

    $text = Get-Content -LiteralPath $Path -Raw
    $sectionPattern = '(?ms)^\s*\[' + [regex]::Escape($Section) + '\][^\S\r\n]*(?:#.*)?\r?\n(?<body>.*?)(?=^\s*\[|\z)'
    $body = [regex]::Match($text, $sectionPattern).Groups['body'].Value
    if ($body -notmatch $Assignment) {
        throw "Required $Section policy is missing from $Path."
    }
}

# Resolve the workspace independently of the caller and restore their location.
Push-Location $PSScriptRoot
try {
    $activeToolchain = rustup show active-toolchain
    if ($LASTEXITCODE -ne 0) { throw 'Rustup toolchain selection failed.' }
    if ($activeToolchain -notmatch '^([0-9]+\.[0-9]+\.[0-9]+)-') {
        throw 'Phase 2 requires an exact stable toolchain version.'
    }
    $testedVersion = $Matches[1]
    Assert-ManifestSetting 'Cargo.toml' 'workspace.lints.rust' `
        '(?m)^\s*unsafe_code\s*=\s*["'']forbid["'']\s*(?:#.*)?$'

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
        if ($package.rust_version -cne $testedVersion) {
            throw "MSRV must equal the tested toolchain $testedVersion`: $($package.name)"
        }
        Assert-ManifestSetting $package.manifest_path 'lints' `
            '(?m)^\s*workspace\s*=\s*true\s*(?:#.*)?$'
        if ($package.name -ne 'openblade-ffi') {
            $source = Get-Content -LiteralPath $package.targets[0].src_path -Raw
            if ($source -notmatch '(?m)^\s*#!\[no_std\]\s*$') {
                throw "The portable crate must retain no_std: $($package.name)"
            }
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
    Write-Output 'Workspace boundaries passed: four local crates, inward dependencies, compiler policies and MSRV.'
}
finally {
    Pop-Location
}
