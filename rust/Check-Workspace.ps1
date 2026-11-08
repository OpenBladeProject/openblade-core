[CmdletBinding()]
param()

Set-StrictMode -Version 3.0
$ErrorActionPreference = 'Stop'

# Cargo metadata owns TOML parsing; these checks retain explicit compiler policies.
function Assert-ManifestSetting {
    param([string]$Path, [string]$Section, [string]$Assignment)

    $text = Get-Content -LiteralPath $Path -Raw
    $sectionPattern = '(?ms)^\s*\[' + [regex]::Escape($Section) + '\][^\S\r\n]*(?:#[^\r\n]*)?\r?\n(?<body>.*?)(?=^\s*\[|\z)'
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
        throw 'The workspace requires an exact stable toolchain version.'
    }
    $testedVersion = $Matches[1]
    Assert-ManifestSetting 'Cargo.toml' 'workspace.lints.rust' `
        '(?m)^\s*unsafe_code\s*=\s*["'']forbid["'']\s*(?:#[^\r\n]*)?$'

    $expectedDependencies = @{
        'openblade-types' = @()
        'openblade-protocol' = @('openblade-types')
        'openblade-domain' = @('openblade-types')
        'openblade-ffi' = @('openblade-domain', 'openblade-protocol', 'openblade-types')
    }
    # Only the protocol fixture reader may introduce registry roots, through dev
    # dependencies. Cargo.lock pins this reviewed parsing/proc-macro closure.
    $expectedDevDependencies = @{ serde = '=1.0.228'; serde_json = '=1.0.149' }
    $allowedExternal = @(
        'serde', 'serde_core', 'serde_derive', 'serde_json', 'itoa', 'memchr', 'zmij',
        'proc-macro2', 'quote', 'syn', 'unicode-ident'
    )
    $expectedTests = @('report_parity', 'report_properties')

    $metadataJson = cargo metadata --locked --offline --format-version 1
    if ($LASTEXITCODE -ne 0) { throw 'Cargo metadata failed (run cargo fetch --locked first).' }
    $metadata = ($metadataJson -join "`n") | ConvertFrom-Json
    if (@($metadata.workspace_members).Count -ne $expectedDependencies.Count) {
        throw 'Only the four reviewed workspace crates are admitted.'
    }
    $localPackages = @($metadata.packages | Where-Object { $null -eq $_.source })
    if ($localPackages.Count -ne $expectedDependencies.Count) {
        throw 'Only the four reviewed local packages are admitted.'
    }
    foreach ($package in $metadata.packages) {
        if ($null -ne $package.source) {
            if ($package.name -notin $allowedExternal -or
                $package.source -cne 'registry+https://github.com/rust-lang/crates.io-index') {
                throw "Unreviewed external package: $($package.name)"
            }
            continue
        }
        if (-not $expectedDependencies.ContainsKey($package.name) -or
            $package.id -notin $metadata.workspace_members) {
            throw "Unreviewed workspace package: $($package.name)"
        }
        if ($package.rust_version -cne $testedVersion) {
            throw "MSRV must equal the tested toolchain $testedVersion`: $($package.name)"
        }
        Assert-ManifestSetting $package.manifest_path 'lints' `
            '(?m)^\s*workspace\s*=\s*true\s*(?:#[^\r\n]*)?$'
        $libraries = @($package.targets | Where-Object { ($_.kind -join ',') -ceq 'lib' })
        if ($libraries.Count -ne 1 -or ($libraries[0].crate_types -join ',') -cne 'lib') {
            throw "Exactly one ordinary Rust library is required: $($package.name)"
        }
        if ($package.name -ne 'openblade-ffi') {
            $source = Get-Content -LiteralPath $libraries[0].src_path -Raw
            if ($source -notmatch '(?m)^\s*#!\[no_std\]\s*$') {
                throw "The portable crate must retain no_std: $($package.name)"
            }
        }
        $normal = @()
        $dev = @()
        foreach ($dependency in $package.dependencies) {
            if ($dependency.optional -or $null -ne $dependency.target -or $null -ne $dependency.rename) {
                throw "Conditional, optional or renamed dependencies are not admitted: $($package.name)"
            }
            if ($null -eq $dependency.kind -and $null -ne $dependency.PSObject.Properties['path'] -and $null -eq $dependency.source) {
                $normal += $dependency.name
            }
            elseif ($package.name -ceq 'openblade-protocol' -and $dependency.kind -ceq 'dev' -and
                $null -eq $dependency.PSObject.Properties['path'] -and
                $dependency.source -ceq 'registry+https://github.com/rust-lang/crates.io-index' -and
                $expectedDevDependencies.ContainsKey($dependency.name) -and
                $dependency.req -ceq $expectedDevDependencies[$dependency.name] -and
                $dependency.uses_default_features) {
                $features = @($dependency.features | Sort-Object) -join ','
                $expectedFeatures = if ($dependency.name -ceq 'serde') { 'derive' } else { '' }
                if ($features -cne $expectedFeatures) {
                    throw "Unreviewed fixture-reader features: $($dependency.name)"
                }
                $dev += $dependency.name
            }
            else {
                throw "Unreviewed dependency kind, source or version: $($package.name)/$($dependency.name)"
            }
        }
        $expected = @($expectedDependencies[$package.name] | Sort-Object) -join ','
        if ((@($normal | Sort-Object) -join ',') -cne $expected) {
            throw "Production dependency boundary changed for $($package.name): $($normal -join ',')"
        }
        $expectedDev = if ($package.name -ceq 'openblade-protocol') {
            @($expectedDevDependencies.Keys | Sort-Object) -join ','
        } else { '' }
        if ((@($dev | Sort-Object) -join ',') -cne $expectedDev) {
            throw "Fixture-reader dev dependencies changed for $($package.name)"
        }
        $tests = @()
        foreach ($target in $package.targets) {
            if (($target.kind -join ',') -ceq 'lib') { continue }
            if ($package.name -cne 'openblade-protocol' -or
                ($target.kind -join ',') -cne 'test' -or
                ($target.crate_types -join ',') -cne 'bin' -or
                $target.name -cnotin $expectedTests) {
                throw "Unreviewed target: $($package.name)/$($target.name)"
            }
            $tests += $target.name
        }
        $expectedTestNames = if ($package.name -ceq 'openblade-protocol') { $expectedTests -join ',' } else { '' }
        if ((@($tests | Sort-Object) -join ',') -cne $expectedTestNames) {
            throw "Reviewed integration tests are missing: $($package.name)"
        }
    }
    Write-Output 'Workspace boundaries passed: four local libraries, inward production edges, reviewed test-only parsing, compiler policies and MSRV.'
}
finally {
    Pop-Location
}
