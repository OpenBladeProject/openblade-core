[CmdletBinding()]
param([string]$Payload)

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

    foreach ($path in @('rust/crates/openblade-ffi/src/lib.rs', 'build/NativePayload.ps1')) {
        $plan = Get-OpenBladeCiPlan -Repository $repository -ChangedPath @($path)
        if (-not $plan.RunInstaller) { throw "$path must select real .NET installer validation." }
    }
    foreach ($workflow in @('ci.yml', 'pr-version.yml')) {
        $text = Get-Content -LiteralPath (Join-Path $repository ".github/workflows/$workflow") -Raw
        $pullRequest = [regex]::Match($text, '(?ms)^  pull_request:\r?\n(?<body>.*?)(?=^  \S|\z)').Groups['body'].Value
        if (-not $pullRequest -or $pullRequest.Contains('branches:')) { throw "$workflow must validate stacked PR bases." }
    }

    # The installer lane supplies a real compiled payload. The contract lane can
    # inspect malformed PE bytes without installing Rust, MSVC, or product files.
    if ($Payload) {
        $manifest = Get-Content -LiteralPath (Join-Path $Payload 'OpenBlade.Native.provenance.json') -Raw | ConvertFrom-Json
        $identity = [pscustomobject]@{ Version = $manifest.productVersion; FileVersion = $manifest.fileVersion }
        Assert-OpenBladeNativePayload -Payload $Payload -Repository $repository -BuildIdentity $identity
        $copy = Join-Path $testRoot 'payload'
        New-Item -ItemType Directory -Path $copy | Out-Null
        foreach ($file in @('openblade_ffi.dll', 'OpenBlade.Native.provenance.json', 'OpenBlade.Native.NOTICE.txt')) {
            Copy-Item -LiteralPath (Join-Path $Payload $file) -Destination $copy
        }
        $manifestPath = Join-Path $copy 'OpenBlade.Native.provenance.json'
        $original = [IO.File]::ReadAllBytes($manifestPath)
        foreach ($property in @('sha256', 'sourceRevision', 'fileVersion', 'target', 'noticeSha256')) {
            $changed = [Text.Encoding]::UTF8.GetString($original) | ConvertFrom-Json
            $changed.$property = 'wrong'
            [IO.File]::WriteAllText($manifestPath, ($changed | ConvertTo-Json -Depth 8))
            Assert-NativeThrows { Assert-OpenBladeNativePayload -Payload $copy -Repository $repository -BuildIdentity $identity } '*does not match*'
        }
        [IO.File]::WriteAllBytes($manifestPath, $original)
        $changed = [Text.Encoding]::UTF8.GetString($original) | ConvertFrom-Json
        $changed.inputs[0].sha256 = '0' * 64
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
