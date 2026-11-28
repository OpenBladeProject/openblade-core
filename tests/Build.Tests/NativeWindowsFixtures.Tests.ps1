[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'
$repository = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
. (Join-Path $repository 'build/NativeWindowsFixtures.ps1')
$pythonRuntime = Get-OpenBladeNativeFixturePythonRuntime
$python = $pythonRuntime.Executable
& $python -B (Join-Path $repository 'build/test_native_windows_fixtures.py')
if ($LASTEXITCODE -ne 0) { throw "Native Windows fixture harness contracts failed: $LASTEXITCODE" }

# These cases run on GitHub-hosted CI too, so they pin the developer-host gate
# explicitly instead of inheriting the runner's environment.
foreach ($case in @(
    @{ Bytes = 24GB; Policy = 'Stop'; Reads = 1 },
    @{ Bytes = 25GB; Policy = 'Warn'; Reads = 2 },
    @{ Bytes = 49GB; Policy = 'Warn'; Reads = 2 },
    @{ Bytes = 50GB; Policy = 'Ready'; Reads = 1 }
)) {
    if ((Get-OpenBladeNativeFixtureDiskPolicy $case.Bytes) -ne $case.Policy) { throw 'Disk threshold policy mismatch.' }
    $script:fixtureDiskReadCount = 0
    $script:fixtureDiskBytes = $case.Bytes
    $threw = $false
    try {
        $messages = @(Assert-OpenBladeNativeFixtureDiskSpace -DisposableHostedRunner $false -ReadAvailableBytes {
            $script:fixtureDiskReadCount++
            return $script:fixtureDiskBytes
        } 3>&1)
    }
    catch { $threw = $true }
    if ($threw -ne ($case.Policy -eq 'Stop') -or $script:fixtureDiskReadCount -ne $case.Reads) { throw 'Disk threshold admission mismatch.' }
    if ($case.Policy -eq 'Warn' -and @($messages).Count -ne 1) { throw 'Low disk warning was not reported.' }
}
$script:fixtureDiskReadCount = 0
$threw = $false
try {
    $null = Assert-OpenBladeNativeFixtureDiskSpace -DisposableHostedRunner $false -ReadAvailableBytes {
        $script:fixtureDiskReadCount++
        if ($script:fixtureDiskReadCount -eq 1) { return 30GB }
        return 24GB
    } 3>&1
}
catch { $threw = $true }
if (-not $threw -or $script:fixtureDiskReadCount -ne 2) { throw 'Disk recheck must stop below 25 GiB.' }
# A disposable GitHub-hosted runner reports its free space once and proceeds.
foreach ($bytes in @(1GB, 24GB, 49GB)) {
    $script:fixtureDiskReadCount = 0
    $script:fixtureDiskBytes = $bytes
    $messages = @(Assert-OpenBladeNativeFixtureDiskSpace -DisposableHostedRunner $true -ReadAvailableBytes {
        $script:fixtureDiskReadCount++
        return $script:fixtureDiskBytes
    } 3>&1)
    if ($script:fixtureDiskReadCount -ne 1 -or @($messages).Count -ne 0) { throw 'Hosted runner disk report mismatch.' }
}
# Only GitHub's own hosted-runner values exempt a run. A self-hosted runner, a
# missing value or a differently cased value keeps the developer-host gate.
foreach ($case in @(
    @{ Actions = 'true'; Runner = 'github-hosted'; Hosted = $true },
    @{ Actions = 'true'; Runner = 'self-hosted'; Hosted = $false },
    @{ Actions = 'true'; Runner = $null; Hosted = $false },
    @{ Actions = $null; Runner = 'github-hosted'; Hosted = $false },
    @{ Actions = 'True'; Runner = 'github-hosted'; Hosted = $false },
    @{ Actions = 'true'; Runner = 'GitHub-Hosted'; Hosted = $false },
    @{ Actions = $null; Runner = $null; Hosted = $false }
)) {
    if ((Test-OpenBladeDisposableHostedRunner -GitHubActions $case.Actions -RunnerEnvironment $case.Runner) -ne $case.Hosted) {
        throw "Hosted runner classification mismatch: GITHUB_ACTIONS='$($case.Actions)' RUNNER_ENVIRONMENT='$($case.Runner)'."
    }
}
$savedActions = $env:GITHUB_ACTIONS
$savedRunner = $env:RUNNER_ENVIRONMENT
try {
    $env:GITHUB_ACTIONS = 'true'
    $env:RUNNER_ENVIRONMENT = 'self-hosted'
    $threw = $false
    try { $null = Assert-OpenBladeNativeFixtureDiskSpace -ReadAvailableBytes { 24GB } 3>&1 }
    catch { $threw = $true }
    if (-not $threw) { throw 'A self-hosted runner must keep the developer-host disk gate.' }
    $env:RUNNER_ENVIRONMENT = 'github-hosted'
    $null = Assert-OpenBladeNativeFixtureDiskSpace -ReadAvailableBytes { 24GB } 3>&1
}
finally {
    $env:GITHUB_ACTIONS = $savedActions
    $env:RUNNER_ENVIRONMENT = $savedRunner
}
Write-Host 'Fixture disk threshold contracts passed.'

# These command shims only probe the runtime guard; they never invoke fixture
# setup, identity, builds or an unsupported real interpreter.
& {
    function Get-Command { throw 'missing runtime' }
    $failed = $false
    try { Get-OpenBladeNativeFixturePythonRuntime | Out-Null }
    catch { $failed = $_.Exception.Message.Contains('3.11') -and $_.Exception.Message.Contains('found') }
    if (-not $failed) { throw 'Missing Python must fail the prerequisite guard.' }
    $proposedRoot = Join-Path ([IO.Path]::GetTempPath()) ('fixture-python-guard-' + [Guid]::NewGuid().ToString('N'))
    $failed = $false
    try { Invoke-OpenBladeNativeFixtureScope -Repository $repository -OutputRoot $proposedRoot -Action { throw 'Unexpected consumer execution.' } }
    catch { $failed = $_.Exception.Message.Contains('3.11') }
    if (-not $failed -or (Test-Path -LiteralPath $proposedRoot)) { throw 'Missing runtime must stop scope entry before root creation.' }
}
& {
    function Get-Command { return [pscustomobject]@{ Source = 'Invoke-UnsupportedFixturePythonProbe' } }
    function Invoke-UnsupportedFixturePythonProbe {
        if (($args[0..2] -join '|') -ne '-I|-S|-c') { throw 'Unexpected runtime probe command.' }
        $global:LASTEXITCODE = 0
        return '{"versionInfo":[3,10,99],"executable":"unused.exe","fileDigest":false}'
    }
    $failed = $false
    try { Get-OpenBladeNativeFixturePythonRuntime | Out-Null }
    catch { $failed = $_.Exception.Message.Contains('3.11') }
    if (-not $failed) { throw 'Unsupported Python must fail the prerequisite guard.' }
    $proposedRoot = Join-Path ([IO.Path]::GetTempPath()) ('fixture-python-guard-' + [Guid]::NewGuid().ToString('N'))
    $failed = $false
    try { Invoke-OpenBladeNativeFixtureScope -Repository $repository -OutputRoot $proposedRoot -Action { throw 'Unexpected consumer execution.' } }
    catch { $failed = $_.Exception.Message.Contains('3.11') }
    if (-not $failed -or (Test-Path -LiteralPath $proposedRoot)) { throw 'Unsupported runtime must stop scope entry before root creation.' }
}
& {
    $script:fixtureHashStopObserved = $false
    function Get-FileHash {
        param($LiteralPath, $Algorithm, $ErrorAction)
        if ($ErrorAction -ne 'Stop' -or $Algorithm -ne 'SHA256' -or
            $LiteralPath -ne $pythonRuntime.Executable) { throw 'Unexpected executable hash boundary.' }
        $script:fixtureHashStopObserved = $true
        throw [IO.IOException]::new('Injected executable hash I/O failure.')
    }
    $failed = $false
    try { Get-OpenBladeNativeFixturePythonRuntime | Out-Null }
    catch {
        $failed = $_.Exception.Message.Contains('Python prerequisite failed while hashing resolved executable') -and
            $_.Exception.Message.Contains('Injected executable hash I/O failure.') -and
            $_.Exception.InnerException.Message.Contains('Injected executable hash I/O failure.')
    }
    if (-not $failed -or -not $script:fixtureHashStopObserved) { throw 'Executable hash failure must retain its prerequisite step and inner detail.' }
    $proposedRoot = Join-Path ([IO.Path]::GetTempPath()) ('fixture-python-hash-guard-' + [Guid]::NewGuid().ToString('N'))
    $failed = $false
    try { Invoke-OpenBladeNativeFixtureScope -Repository $repository -OutputRoot $proposedRoot -Action { throw 'Unexpected consumer execution.' } }
    catch { $failed = $_.Exception.Message.Contains('hashing resolved executable') -and $_.Exception.Message.Contains('Injected executable hash I/O failure.') }
    if (-not $failed -or (Test-Path -LiteralPath $proposedRoot)) { throw 'Executable hash failure must stop scope entry before root creation.' }
}
if ([Version]::new([int]$pythonRuntime.VersionInfo[0], [int]$pythonRuntime.VersionInfo[1]) -lt [Version]'3.11' -or
    $pythonRuntime.Sha256 -ne (Get-FileHash -LiteralPath $pythonRuntime.Executable -Algorithm SHA256).Hash.ToLowerInvariant()) {
    throw 'Supported Python probe must bind the actual executable and version.'
}
Write-Host 'Python prerequisite contracts passed (missing, unsupported, hash I/O failure, supported path/hash).'
