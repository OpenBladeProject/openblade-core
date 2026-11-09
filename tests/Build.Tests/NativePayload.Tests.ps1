[CmdletBinding()]
param([string]$Payload, [string]$DeveloperTools)

Set-StrictMode -Version 3.0
$ErrorActionPreference = 'Stop'
$repository = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
. (Join-Path $repository 'build/PublishPayloadValidation.ps1')
. (Join-Path $repository 'build/NativePayload.ps1')
. (Join-Path $repository 'build/BuildIdentity.ps1')
. (Join-Path $repository 'build/CiBuildScope.ps1')

function Assert-NativeThrows([scriptblock]$Action, [string]$Pattern) {
    $caught = $null
    try { & $Action | Out-Null } catch { $caught = $_.Exception.Message }
    if (-not $caught -or $caught -notlike $Pattern) { throw "Expected '$Pattern', got '$caught'." }
}

function Set-FixtureInteger([byte[]]$Bytes, [int]$Offset, [uint32]$Value) {
    [BitConverter]::GetBytes($Value).CopyTo($Bytes, $Offset)
}

$testRoot = Join-Path ([IO.Path]::GetTempPath()) ('OpenBlade.NativePayload.Tests.' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $testRoot | Out-Null
try {
    # A data-only PE fixture. It is never loaded or executed.
    $image = [byte[]]::new(2048)
    $image[0] = 0x4D; $image[1] = 0x5A
    Set-FixtureInteger $image 60 128
    Set-FixtureInteger $image 128 0x4550
    $image[132] = 0x64; $image[133] = 0x86; $image[134] = 1
    $image[148] = 240; $image[151] = 0x20
    $image[152] = 0x0B; $image[153] = 0x02
    Set-FixtureInteger $image 260 16
    Set-FixtureInteger $image 272 0x1000
    Set-FixtureInteger $image 276 40
    Set-FixtureInteger $image 404 0x1000
    Set-FixtureInteger $image 408 1024
    Set-FixtureInteger $image 412 512
    Set-FixtureInteger $image 524 0x1040
    [Text.Encoding]::ASCII.GetBytes("KERNEL32.dll`0").CopyTo($image, 576)
    $fixture = Join-Path $testRoot 'fixture.dll'
    [IO.File]::WriteAllBytes($fixture, $image)
    $info = Get-OpenBladeNativePeInfo $fixture
    if ($info.Machine -ne 'x64' -or ($info.Imports -join ',') -cne 'kernel32.dll') { throw 'PE architecture/import parsing failed.' }

    Set-FixtureInteger $image 260 13
    [IO.File]::WriteAllBytes($fixture, $image)
    Assert-NativeThrows { Get-OpenBladeNativePeInfo $fixture } '*declared header bounds*'
    Set-FixtureInteger $image 260 16
    $image[148] = 224
    [IO.File]::WriteAllBytes($fixture, $image)
    Assert-NativeThrows { Get-OpenBladeNativePeInfo $fixture } '*declared header bounds*'
    $image[148] = 240
    foreach ($dependency in @('api-ms-win-core-synch-l1-2-0.dll', 'ntdll.dll')) {
        [Array]::Clear($image, 576, 128)
        [Text.Encoding]::ASCII.GetBytes($dependency + "`0").CopyTo($image, 576)
        [IO.File]::WriteAllBytes($fixture, $image)
        $info = Get-OpenBladeNativePeInfo $fixture
        if (($info.Imports -join ',') -cne $dependency) { throw 'Reviewed Windows import was rejected.' }
    }
    foreach ($dependency in @('api-ms-win-crt-runtime-l1-1-0.dll', 'advapi32.dll', "kernel32.dll`n")) {
        [Array]::Clear($image, 576, 128)
        [Text.Encoding]::ASCII.GetBytes($dependency + "`0").CopyTo($image, 576)
        [IO.File]::WriteAllBytes($fixture, $image)
        Assert-NativeThrows { Get-OpenBladeNativePeInfo $fixture } '*unreviewed runtime dependency*'
    }
    [Array]::Clear($image, 576, 128)
    [Text.Encoding]::ASCII.GetBytes("KERNEL32.dll`0").CopyTo($image, 576)

    $image[132] = 0x4C; $image[133] = 0x01
    [IO.File]::WriteAllBytes($fixture, $image)
    Assert-NativeThrows { Get-OpenBladeNativePeInfo $fixture } '*Windows x64*'
    $image[132] = 0x64; $image[133] = 0x86
    [Text.Encoding]::ASCII.GetBytes("VCRUNTIME140.dll`0").CopyTo($image, 576)
    [IO.File]::WriteAllBytes($fixture, $image)
    Assert-NativeThrows { Get-OpenBladeNativePeInfo $fixture } '*unreviewed runtime dependency*'
    [Text.Encoding]::ASCII.GetBytes("KERNEL32.dll`0").CopyTo($image, 576)
    Set-FixtureInteger $image 368 0x1000
    [IO.File]::WriteAllBytes($fixture, $image)
    Assert-NativeThrows { Get-OpenBladeNativePeInfo $fixture } '*delay imports*'
    Set-FixtureInteger $image 368 0
    Set-FixtureInteger $image 524 ([uint32]::MaxValue)
    [IO.File]::WriteAllBytes($fixture, $image)
    Assert-NativeThrows { Get-OpenBladeNativePeInfo $fixture } '*Invalid native PE RVA*'
    [IO.File]::WriteAllBytes($fixture, [byte[]]::new(100))
    Assert-NativeThrows { Get-OpenBladeNativePeInfo $fixture } '*Invalid native PE image*'

    # Same Git source revision with LF and CRLF working bytes must identify the
    # same source inputs. This tiny data-only repository never builds a DLL.
    $sourceFixture = Join-Path $testRoot 'source'
    New-Item -ItemType Directory -Path (Join-Path $sourceFixture 'rust') -Force | Out-Null
    for ($index = 0; $index -lt 12; $index++) {
        [IO.File]::WriteAllText((Join-Path $sourceFixture "rust/input$index.txt"), "fixture`n")
    }
    $properties = Join-Path $sourceFixture 'Directory.Build.props'
    [IO.File]::WriteAllText($properties, "<Project />`n")
    & git -C $sourceFixture init -q -b build/native-source-fixture
    if ($LASTEXITCODE -ne 0) { throw 'Source fixture initialization failed.' }
    & git -C $sourceFixture config core.autocrlf false
    if ($LASTEXITCODE -ne 0) { throw 'Source fixture configuration failed.' }
    & git -C $sourceFixture add .
    if ($LASTEXITCODE -ne 0) { throw 'Source fixture staging failed.' }
    & git -C $sourceFixture -c user.name='OpenBlade Tests' -c user.email='tests@example.invalid' -c commit.gpgsign=false commit -qm fixture
    if ($LASTEXITCODE -ne 0) { throw 'Source fixture commit failed.' }
    $lfInputs = @(Get-OpenBladeNativeInputs -Repository $sourceFixture)
    & git -C $sourceFixture config core.autocrlf true
    if ($LASTEXITCODE -ne 0) { throw 'Source fixture CRLF configuration failed.' }
    [IO.File]::WriteAllText($properties, "<Project />`r`n")
    $crlfInputs = @(Get-OpenBladeNativeInputs -Repository $sourceFixture)
    if ((ConvertTo-Json -InputObject $lfInputs -Compress) -cne (ConvertTo-Json -InputObject $crlfInputs -Compress)) {
        throw 'Native source identity depends on checkout line endings.'
    }
    [IO.File]::AppendAllText($properties, 'changed')
    Assert-NativeThrows { Get-OpenBladeNativeInputs -Repository $sourceFixture } '*Commit native build inputs*'

    foreach ($path in @('rust/crates/openblade-ffi/src/lib.rs', 'build/NativePayload.ps1', 'build/Build-NativeDevelopment.ps1', 'build/Prepare-NativeToolchain.ps1')) {
        $plan = Get-OpenBladeCiPlan -Repository $repository -ChangedPath @($path)
        if (-not $plan.RunInstaller) { throw "$path must select real .NET installer validation." }
    }
    foreach ($workflow in @('ci.yml', 'pr-version.yml')) {
        $text = Get-Content -LiteralPath (Join-Path $repository ".github/workflows/$workflow") -Raw
        $pullRequest = [regex]::Match($text, '(?ms)^  pull_request:\r?\n(?<body>.*?)(?=^  \S|\z)').Groups['body'].Value
        if (-not $pullRequest -or $pullRequest.Contains('branches:')) { throw "$workflow must validate stacked PR bases." }
    }

    # Packaging must reuse its native payload and selected configuration. Stub only
    # dotnet so this contract test never loads the data-only DLL fixture.
    $shadowPayload = Join-Path $testRoot 'shadow'
    New-Item -ItemType Directory -Path $shadowPayload | Out-Null
    $shadowLibrary = Join-Path $shadowPayload 'openblade_ffi.dll'
    [IO.File]::WriteAllBytes($shadowLibrary, [byte[]]::new(1))
    $shadowManifest = Join-Path $shadowPayload 'OpenBlade.Native.provenance.json'
    [IO.File]::WriteAllText($shadowManifest, '{}')
    $shadowInvocation = @{ Arguments = @(); LibraryEnvironment = $null }
    function dotnet {
        $shadowInvocation.Arguments = @($args)
        $shadowInvocation.LibraryEnvironment = $env:OPENBLADE_NATIVE_LIBRARY
        $global:LASTEXITCODE = 0
    }
    $previousShadowLibrary = $env:OPENBLADE_NATIVE_LIBRARY
    try {
        $env:OPENBLADE_NATIVE_LIBRARY = 'preserved-test-value'
        & (Join-Path $repository 'rust/Compare-Shadow.ps1') -LibraryPath $shadowLibrary -Configuration Debug -NativePayloadDirectory $shadowPayload | Out-Null
        $configurationIndex = [Array]::IndexOf($shadowInvocation.Arguments, '--configuration')
        if ($configurationIndex -lt 0 -or $shadowInvocation.Arguments[$configurationIndex + 1] -cne 'Debug' -or
            '-p:OpenBladeNativeDevelopmentBuild=false' -cnotin $shadowInvocation.Arguments -or
            "-p:OpenBladeNativePayloadDirectory=$shadowPayload" -cnotin $shadowInvocation.Arguments -or
            $shadowInvocation.LibraryEnvironment -cne $shadowLibrary -or
            $env:OPENBLADE_NATIVE_LIBRARY -cne 'preserved-test-value') {
            throw 'Packaging shadow comparison did not reuse the selected payload/configuration or restore its environment.'
        }
        & (Join-Path $repository 'rust/Compare-Shadow.ps1') -LibraryPath $shadowLibrary | Out-Null
        $configurationIndex = [Array]::IndexOf($shadowInvocation.Arguments, '--configuration')
        if ($configurationIndex -lt 0 -or $shadowInvocation.Arguments[$configurationIndex + 1] -cne 'Release' -or
            @($shadowInvocation.Arguments | Where-Object { $_ -like '-p:OpenBladeNativePayloadDirectory=*' -or $_ -like '-p:OpenBladeNativeDevelopmentBuild=*' }).Count -ne 0) {
            throw 'Ordinary shadow comparison lost its development-build defaults.'
        }
        Remove-Item -LiteralPath $shadowManifest
        Assert-NativeThrows {
            & (Join-Path $repository 'rust/Compare-Shadow.ps1') -LibraryPath $shadowLibrary -NativePayloadDirectory $shadowPayload
        } '*existing native payload*'
    }
    finally {
        Remove-Item Function:dotnet
        $env:OPENBLADE_NATIVE_LIBRARY = $previousShadowLibrary
    }

    # The installer lane supplies a real compiled payload. The contract lane can
    # inspect malformed PE bytes without installing Rust, MSVC, or product files.
    if ($Payload) {
        $manifest = Get-Content -LiteralPath (Join-Path $Payload 'OpenBlade.Native.provenance.json') -Raw | ConvertFrom-Json
        $identity = [pscustomobject]@{ Version = $manifest.productVersion; FileVersion = $manifest.fileVersion }
        Assert-OpenBladeNativePayload -Payload $Payload -Repository $repository -BuildIdentity $identity
        if ($DeveloperTools) {
            Assert-OpenBladeNativePayload -Payload $DeveloperTools -Repository $repository -BuildIdentity $identity
            foreach ($nativeFile in @('openblade_ffi.dll', 'OpenBlade.Native.provenance.json', 'OpenBlade.Native.NOTICE.txt')) {
                if ((Get-FileHash -LiteralPath (Join-Path $Payload $nativeFile) -Algorithm SHA256).Hash -cne
                    (Get-FileHash -LiteralPath (Join-Path $DeveloperTools $nativeFile) -Algorithm SHA256).Hash) {
                    throw "Developer tools must carry the same native DLL, provenance and notices: $nativeFile"
                }
            }
        }
        $copy = Join-Path $testRoot 'payload'
        New-Item -ItemType Directory -Path $copy | Out-Null
        foreach ($file in @('openblade_ffi.dll', 'OpenBlade.Native.provenance.json', 'OpenBlade.Native.NOTICE.txt')) {
            Copy-Item -LiteralPath (Join-Path $Payload $file) -Destination $copy
        }
        $manifestPath = Join-Path $copy 'OpenBlade.Native.provenance.json'
        $original = [IO.File]::ReadAllBytes($manifestPath)
        foreach ($property in @('sha256', 'unsignedSha256', 'sourceRevision', 'fileVersion', 'target', 'noticeSha256')) {
            $changed = [Text.Encoding]::UTF8.GetString($original) | ConvertFrom-Json
            $changed.$property = 'wrong'
            [IO.File]::WriteAllText($manifestPath, ($changed | ConvertTo-Json -Depth 8))
            Assert-NativeThrows { Assert-OpenBladeNativePayload -Payload $copy -Repository $repository -BuildIdentity $identity } '*does not match*'
        }
        [IO.File]::WriteAllBytes($manifestPath, $original)
        $changed = [Text.Encoding]::UTF8.GetString($original) | ConvertFrom-Json
        $changed.unsignedSha256 = '0' * 64
        [IO.File]::WriteAllText($manifestPath, ($changed | ConvertTo-Json -Depth 8))
        Assert-NativeThrows {
            Assert-OpenBladeNativePayload -Payload $copy -Repository $repository -BuildIdentity $identity -ExpectedUnsignedSha256 $manifest.unsignedSha256
        } '*does not match*'
        if ((Get-AuthenticodeSignature -LiteralPath (Join-Path $copy 'openblade_ffi.dll')).Status -eq 'NotSigned') {
            Assert-NativeThrows { Assert-OpenBladeNativePayload -Payload $copy -Repository $repository -BuildIdentity $identity } '*unsigned build hash*'
        }
        [IO.File]::WriteAllBytes($manifestPath, $original)
        $changed = [Text.Encoding]::UTF8.GetString($original) | ConvertFrom-Json
        $changed.inputs[0].gitBlobId = '0' * 40
        [IO.File]::WriteAllText($manifestPath, ($changed | ConvertTo-Json -Depth 8))
        Assert-NativeThrows { Assert-OpenBladeNativePayload -Payload $copy -Repository $repository -BuildIdentity $identity } '*source provenance does not match*'
        [IO.File]::WriteAllBytes($manifestPath, $original)
        $notice = Join-Path $copy 'OpenBlade.Native.NOTICE.txt'
        [IO.File]::AppendAllText($notice, 'tampered')
        Assert-NativeThrows { Assert-OpenBladeNativePayload -Payload $copy -Repository $repository -BuildIdentity $identity } '*notice hash*'
        Copy-Item -LiteralPath (Join-Path $Payload 'OpenBlade.Native.NOTICE.txt') -Destination $notice -Force
        $library = Join-Path $copy 'openblade_ffi.dll'
        $libraryBytes = [IO.File]::ReadAllBytes($library)
        $libraryBytes[$libraryBytes.Length - 1] = $libraryBytes[$libraryBytes.Length - 1] -bxor 1
        [IO.File]::WriteAllBytes($library, $libraryBytes)
        Assert-NativeThrows { Assert-OpenBladeNativePayload -Payload $copy -Repository $repository -BuildIdentity $identity } '*does not match*'
    }
    Write-Host 'Native packaging contracts passed (PE architecture, imports, source/hash/notice admission, stacked CI).'
}
finally {
    # This exact test-owned directory is always under the OS temporary root.
    $resolvedTestRoot = [IO.Path]::GetFullPath($testRoot)
    $temporaryPrefix = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\') + '\'
    if (-not $resolvedTestRoot.StartsWith($temporaryPrefix, [StringComparison]::OrdinalIgnoreCase)) { throw 'Unsafe native test cleanup path.' }
    Remove-Item -LiteralPath $testRoot -Recurse -Force
}
