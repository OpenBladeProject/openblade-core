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

    foreach ($profile in @('profile.dev', 'profile.release')) {
        Assert-ManifestSetting 'Cargo.toml' $profile `
            '(?m)^\s*panic\s*=\s*["'']unwind["'']\s*(?:#[^\r\n]*)?$'
    }
    if ((Get-Content Cargo.toml -Raw) -match '(?m)^\s*panic\s*=\s*["'']abort["'']') {
        throw 'Panic abort is not admitted: FFI requires unwind containment.'
    }

    $expectedDependencies = @{
        'openblade-types' = @()
        'openblade-protocol' = @('openblade-types')
        'openblade-domain' = @('openblade-types')
        'openblade-ffi' = @('openblade-domain', 'openblade-protocol', 'openblade-types')
        'openblade-status-cli' = @()
        'openblade-platform-windows' = @()
    }
    # Fixture readers admit parsing only as dev dependencies. The separate CLI owns
    # its reviewed std/runtime roots; none may enter the four inner libraries.
    $expectedDevDependencies = @{ serde = '=1.0.228'; serde_json = '=1.0.149' }
    $expectedCliDependencies = @{ serde = '=1.0.228'; serde_json = '=1.0.149'; tokio = '=1.54.1' }
    $expectedCliFeatures = @{ serde = 'derive'; serde_json = ''; tokio = 'io-util,macros,net,rt,signal,time' }
    $allowedExternal = @(
        'serde', 'serde_core', 'serde_derive', 'serde_json', 'itoa', 'memchr', 'zmij',
        'proc-macro2', 'quote', 'syn', 'unicode-ident', 'bytes', 'errno', 'libc',
        'mio', 'pin-project-lite', 'signal-hook-registry', 'socket2', 'tokio',
        'tokio-macros', 'wasi', 'windows-link', 'windows-sys'
    )
    $fixtureReaderPackages = @('openblade-protocol', 'openblade-domain')
    $expectedTests = @{
        'openblade-protocol' = @('report_parity', 'report_properties')
        'openblade-domain' = @('automation_policy', 'profile_policy')
    }

    $metadataJson = cargo metadata --locked --offline --format-version 1
    if ($LASTEXITCODE -ne 0) { throw 'Cargo metadata failed (run cargo fetch --locked first).' }
    $metadata = ($metadataJson -join "`n") | ConvertFrom-Json
    if (@($metadata.workspace_members).Count -ne $expectedDependencies.Count) {
        throw 'Only the six reviewed workspace crates are admitted.'
    }
    $localPackages = @($metadata.packages | Where-Object { $null -eq $_.source })
    if ($localPackages.Count -ne $expectedDependencies.Count) {
        throw 'Only the six reviewed local packages are admitted.'
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
        $isCli = $package.name -ceq 'openblade-status-cli'
        $isFfi = $package.name -ceq 'openblade-ffi'
        $isWindowsPlatform = $package.name -ceq 'openblade-platform-windows'
        if ($isFfi -or $isWindowsPlatform) {
            # Native interop is isolated in the reviewed boundary crates.
            Assert-ManifestSetting $package.manifest_path 'lints.rust' `
                '(?m)^\s*unsafe_op_in_unsafe_fn\s*=\s*["'']deny["'']\s*(?:#[^\r\n]*)?$'
            if ($isWindowsPlatform) {
                Assert-ManifestSetting $package.manifest_path 'lints.rust' `
                    '(?m)^\s*unsafe_code\s*=\s*["'']deny["'']\s*(?:#[^\r\n]*)?$'
            }
        } else {
            Assert-ManifestSetting $package.manifest_path 'lints' `
                '(?m)^\s*workspace\s*=\s*true\s*(?:#[^\r\n]*)?$'
        }
        if ($isCli) {
            $expectedMain = Join-Path (Split-Path -Parent $package.manifest_path) 'src/main.rs'
            if (@($package.targets).Count -ne 1 -or
                ($package.targets[0].kind -join ',') -cne 'bin' -or
                ($package.targets[0].crate_types -join ',') -cne 'bin' -or
                $package.targets[0].name -cne 'openblade-status-cli' -or
                [IO.Path]::GetFullPath($package.targets[0].src_path) -cne [IO.Path]::GetFullPath($expectedMain)) {
                throw 'The CLI requires only its reviewed src/main.rs binary target.'
            }
            if (@($package.features.PSObject.Properties).Count -ne 0) {
                throw 'CLI package features are not admitted.'
            }
        } else {
            $libraries = @($package.targets | Where-Object { @($_.kind | Where-Object { $_ -in @('lib', 'rlib', 'cdylib', 'staticlib') }).Count -gt 0 })
            $expectedOutputs = if ($isFfi) { 'cdylib,rlib,staticlib' } else { 'lib' }
            if ($libraries.Count -ne 1 -or
                (@($libraries[0].crate_types | Sort-Object) -join ',') -cne $expectedOutputs -or
                (@($libraries[0].kind | Sort-Object) -join ',') -cne $expectedOutputs) {
                throw "Reviewed library outputs are required: $($package.name) ($expectedOutputs)"
            }
            if (-not ($isFfi -or $isWindowsPlatform)) {
                $source = Get-Content -LiteralPath $libraries[0].src_path -Raw
                if ($source -notmatch '(?m)^\s*#!\[no_std\]\s*$') {
                    throw "The portable crate must retain no_std: $($package.name)"
                }
            }
        }
        $normal = @()
        $dev = @()
        $cli = @()
        $windowsSdkCount = 0
        foreach ($dependency in $package.dependencies) {
            if ($isWindowsPlatform -and $dependency.name -ceq 'windows-sys' -and
                $dependency.target -ceq 'cfg(windows)' -and $null -eq $dependency.kind -and
                $null -eq $dependency.rename -and -not $dependency.optional -and
                $null -eq $dependency.PSObject.Properties['path'] -and
                $dependency.source -ceq 'registry+https://github.com/rust-lang/crates.io-index' -and
                $dependency.req -ceq '=0.61.2' -and -not $dependency.uses_default_features -and
                (@($dependency.features | Sort-Object) -join ',') -ceq 'Win32_Devices_Display,Win32_Foundation,Win32_Graphics_Gdi') {
                $windowsSdkCount++
                continue
            }
            if ($dependency.optional -or $null -ne $dependency.target -or $null -ne $dependency.rename) {
                throw "Conditional, optional or renamed dependencies are not admitted: $($package.name)"
            }
            if ($null -eq $dependency.kind -and $null -ne $dependency.PSObject.Properties['path'] -and $null -eq $dependency.source) {
                $normal += $dependency.name
            }
            elseif ($isCli -and $null -eq $dependency.kind -and
                $null -eq $dependency.PSObject.Properties['path'] -and
                $dependency.source -ceq 'registry+https://github.com/rust-lang/crates.io-index' -and
                $expectedCliDependencies.ContainsKey($dependency.name) -and
                $dependency.req -ceq $expectedCliDependencies[$dependency.name] -and
                $dependency.uses_default_features) {
                if ((@($dependency.features | Sort-Object) -join ',') -cne $expectedCliFeatures[$dependency.name]) {
                    throw "Unreviewed CLI dependency features: $($dependency.name)"
                }
                $cli += $dependency.name
            }
            elseif ($package.name -cin $fixtureReaderPackages -and $dependency.kind -ceq 'dev' -and
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
        if ($isWindowsPlatform -and $windowsSdkCount -ne 1) {
            throw 'The Windows adapter requires its reviewed Windows SDK dependency.'
        }
        $expected = @($expectedDependencies[$package.name] | Sort-Object) -join ','
        if ((@($normal | Sort-Object) -join ',') -cne $expected) {
            throw "Production dependency boundary changed for $($package.name): $($normal -join ',')"
        }
        $expectedDev = if ($package.name -cin $fixtureReaderPackages) {
            @($expectedDevDependencies.Keys | Sort-Object) -join ','
        } else { '' }
        if ((@($dev | Sort-Object) -join ',') -cne $expectedDev) {
            throw "Fixture-reader dev dependencies changed for $($package.name)"
        }
        $expectedCli = if ($isCli) { @($expectedCliDependencies.Keys | Sort-Object) -join ',' } else { '' }
        if ((@($cli | Sort-Object) -join ',') -cne $expectedCli) {
            throw "CLI dependencies changed for $($package.name)"
        }
        if ($isCli) { continue }
        $tests = @()
        foreach ($target in $package.targets) {
            if ($target -eq $libraries[0]) { continue }
            if (-not $expectedTests.ContainsKey($package.name) -or
                ($target.kind -join ',') -cne 'test' -or
                ($target.crate_types -join ',') -cne 'bin' -or
                $target.name -cnotin $expectedTests[$package.name]) {
                throw "Unreviewed target: $($package.name)/$($target.name)"
            }
            $tests += $target.name
        }
        $expectedTestNames = if ($expectedTests.ContainsKey($package.name)) {
            @($expectedTests[$package.name] | Sort-Object) -join ','
        } else { '' }
        if ((@($tests | Sort-Object) -join ',') -cne $expectedTestNames) {
            throw "Reviewed integration tests are missing: $($package.name)"
        }
    }
    Write-Output 'Workspace boundaries passed: four local libraries with inward production edges, isolated status CLI and Windows adapter, reviewed parsing/runtime roots, compiler policies and MSRV.'
}
finally {
    Pop-Location
}
