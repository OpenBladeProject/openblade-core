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
        'openblade-platform-windows' = @('openblade-application-reporter')
        'openblade-application-reporter' = @()
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
    # Sorted as Sort-Object orders them. The two Wdk_ features plus Security_Authorization,
    # System_IO, System_JobObjects and UI_Shell back the battery report provider (27e6f725).
    $expectedWindowsFeatures = @(
        'Wdk_Foundation', 'Wdk_Storage_FileSystem',
        'Win32_Devices_Display', 'Win32_Foundation', 'Win32_Globalization', 'Win32_Graphics_Gdi',
        'Win32_Security', 'Win32_Security_Authorization', 'Win32_Storage_FileSystem', 'Win32_System_Com',
        'Win32_System_Diagnostics_ToolHelp', 'Win32_System_IO', 'Win32_System_JobObjects',
        'Win32_System_LibraryLoader', 'Win32_System_Ole', 'Win32_System_Performance', 'Win32_System_Registry',
        'Win32_System_RemoteDesktop', 'Win32_System_Rpc', 'Win32_System_StationsAndDesktops',
        'Win32_System_SystemInformation', 'Win32_System_Threading', 'Win32_System_Variant', 'Win32_System_Wmi',
        'Win32_UI_ColorSystem', 'Win32_UI_Input_KeyboardAndMouse', 'Win32_UI_Shell', 'Win32_UI_WindowsAndMessaging'
    ) -join ','
    # openblade-domain's off-by-default thermal-history feature carries the bounded
    # history grammar for tools/openblade-windows-host, which is outside this workspace.
    # Only these exact optional roots are admitted, and no workspace crate enables them.
    $expectedThermalHistoryDependencies = @{ serde = '=1.0.228'; serde_json = '=1.0.149'; sha2 = '=0.10.9' }
    $expectedThermalHistoryFeatures = @{ serde = 'derive'; serde_json = 'arbitrary_precision'; sha2 = '' }
    $fixtureReaderPackages = @('openblade-protocol', 'openblade-domain')
    $expectedTests = @{
        # custom_performance (cbf8aacc) and temporary_charge (a882588c) pin shared native grammar.
        # completed_rejection (0e1061a0) pins the shared 07/8C completed-rejection geometry.
        'openblade-protocol' = @('completed_rejection', 'custom_performance', 'report_parity', 'report_properties',
            'temporary_charge')
        # profile_set_vectors reads the managed ProfileSet vectors; it compiles only
        # with the profile-set and thermal-history features. profile_resolution_vectors
        # reads the managed active application-profile vectors; it compiles only with
        # the profile-set feature. profile_migration_vectors reads the managed
        # store-open migration vectors; like profile_set_vectors it needs both features.
        # function_row_vectors reads the managed Windows-hook function-row vectors.
        # low_power_gate_vectors reads the managed LowPowerGate vectors and
        # power_setting_vectors the managed power-setting notice vectors.
        # thermal_convergence_vectors reads the managed convergence ledger vectors;
        # it compiles only with the thermal-convergence feature.
        'openblade-domain' = @('automation_policy', 'function_row_policy', 'function_row_vectors',
            'low_power_gate_vectors', 'power_setting_vectors', 'profile_migration_vectors', 'profile_policy',
            'profile_resolution_vectors', 'profile_set_vectors', 'recovery_replay', 'thermal_convergence_vectors')
    }

    $metadataJson = cargo metadata --locked --offline --format-version 1
    if ($LASTEXITCODE -ne 0) { throw 'Cargo metadata failed (run cargo fetch --locked first).' }
    $metadata = ($metadataJson -join "`n") | ConvertFrom-Json
    if (@($metadata.workspace_members).Count -ne $expectedDependencies.Count) {
        throw 'Only the seven reviewed workspace crates are admitted.'
    }
    $localPackages = @($metadata.packages | Where-Object { $null -eq $_.source })
    if ($localPackages.Count -ne $expectedDependencies.Count) {
        throw 'Only the seven reviewed local packages are admitted.'
    }
    # Local packages, including their optional roots and features, are checked before
    # external names: an enabled thermal-history feature must fail as a feature error.
    foreach ($package in $metadata.packages) {
        if ($null -ne $package.source) { continue }
        if (-not $expectedDependencies.ContainsKey($package.name) -or
            $package.id -notin $metadata.workspace_members) {
            throw "Unreviewed workspace package: $($package.name)"
        }
        if ($package.rust_version -cne $testedVersion) {
            throw "MSRV must equal the tested toolchain $testedVersion`: $($package.name)"
        }
        $isCli = $package.name -ceq 'openblade-status-cli'
        $isApplicationReporter = $package.name -ceq 'openblade-application-reporter'
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
            if (-not ($isFfi -or $isWindowsPlatform -or $isApplicationReporter)) {
                $source = Get-Content -LiteralPath $libraries[0].src_path -Raw
                if ($source -notmatch '(?m)^\s*#!\[no_std\]\s*$') {
                    throw "The portable crate must retain no_std: $($package.name)"
                }
            }
        }
        $normal = @()
        $dev = @()
        $cli = @()
        $thermalHistory = @()
        $windowsSdkCount = 0
        $windowsPolicyCount = 0
        foreach ($dependency in $package.dependencies) {
            if ($dependency.name -ceq 'openblade-domain' -and @($dependency.features).Count -ne 0) {
                throw "The thermal-history feature is not admitted inside the workspace: $($package.name)"
            }
            if ($isWindowsPlatform -and $dependency.name -ceq 'windows-sys' -and
                $dependency.target -ceq 'cfg(windows)' -and $null -eq $dependency.kind -and
                $null -eq $dependency.rename -and -not $dependency.optional -and
                $null -eq $dependency.PSObject.Properties['path'] -and
                $dependency.source -ceq 'registry+https://github.com/rust-lang/crates.io-index' -and
                $dependency.req -ceq '=0.61.2' -and -not $dependency.uses_default_features -and
                (@($dependency.features | Sort-Object) -join ',') -ceq $expectedWindowsFeatures) {
                $windowsSdkCount++
                continue
            }
            if ($isWindowsPlatform -and $dependency.name -ceq 'openblade-domain' -and
                $dependency.target -ceq 'cfg(windows)' -and $null -eq $dependency.kind -and
                $null -eq $dependency.rename -and -not $dependency.optional -and
                $null -ne $dependency.PSObject.Properties['path'] -and $null -eq $dependency.source -and
                $dependency.req -ceq '*' -and $dependency.uses_default_features -and
                @($dependency.features).Count -eq 0) {
                $windowsPolicyCount++
                continue
            }
            if ($package.name -ceq 'openblade-domain' -and $dependency.optional -and
                $null -eq $dependency.kind -and $null -eq $dependency.target -and
                $null -eq $dependency.rename -and $null -eq $dependency.PSObject.Properties['path'] -and
                $dependency.source -ceq 'registry+https://github.com/rust-lang/crates.io-index' -and
                $expectedThermalHistoryDependencies.ContainsKey($dependency.name) -and
                $dependency.req -ceq $expectedThermalHistoryDependencies[$dependency.name] -and
                $dependency.uses_default_features -and
                (@($dependency.features | Sort-Object) -join ',') -ceq $expectedThermalHistoryFeatures[$dependency.name]) {
                $thermalHistory += $dependency.name
                continue
            }
            $expectedTarget = if ($isWindowsPlatform -and $dependency.name -ceq 'openblade-application-reporter') {
                'cfg(windows)'
            } else { $null }
            if ($dependency.optional -or $dependency.target -cne $expectedTarget -or $null -ne $dependency.rename) {
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
        if ($isWindowsPlatform -and $windowsPolicyCount -ne 1) {
            throw 'The Windows adapter requires its reviewed Windows-only domain policy dependency.'
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
        $isDomain = $package.name -ceq 'openblade-domain'
        $expectedThermalHistory = if ($isDomain) {
            @($expectedThermalHistoryDependencies.Keys | Sort-Object) -join ','
        } else { '' }
        if ((@($thermalHistory | Sort-Object) -join ',') -cne $expectedThermalHistory) {
            throw "Thermal-history dependencies changed for $($package.name)"
        }
        if ($isDomain) {
            $domainFeatures = @($package.features.PSObject.Properties | ForEach-Object {
                $_.Name + '=' + (@($_.Value | Sort-Object) -join '+')
            }) -join ','
            # profile-set, driver-mode-history and hyperboost-exit are pure
            # std code with no dependency of their own, and
            # thermal-convergence adds only pure ledger rules over
            # thermal-history.
            if ($domainFeatures -cne 'driver-mode-history=,hyperboost-exit=,profile-set=,thermal-convergence=thermal-history,thermal-history=dep:serde+dep:serde_json+dep:sha2') {
                throw "Unreviewed openblade-domain features: $domainFeatures"
            }
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
    # Default-feature metadata lists only activated packages. The feature checks above
    # keep thermal-history off, so its sha2 closure never reaches this name check.
    foreach ($package in $metadata.packages) {
        if ($null -eq $package.source) { continue }
        if ($package.name -notin $allowedExternal -or
            $package.source -cne 'registry+https://github.com/rust-lang/crates.io-index') {
            throw "Unreviewed external package: $($package.name)"
        }
    }
    Write-Output 'Workspace boundaries passed: four local libraries with inward production edges, isolated status CLI, Windows adapter with inward reporter and domain edges, reviewed parsing/runtime roots, compiler policies and MSRV.'
}
finally {
    Pop-Location
}
