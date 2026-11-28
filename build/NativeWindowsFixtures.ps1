Set-StrictMode -Version 3.0

function Get-OpenBladeNativeFixturePythonRuntime {
    try { $command = Get-Command python -CommandType Application -ErrorAction Stop | Select-Object -First 1 }
    catch { throw 'Python 3.11 or newer is required for native fixture checks; no Python executable was found.' }
    # Keep double quotes out of the probe so Windows PowerShell 5.1 passes
    # the complete Python expression intact through native argument handling.
    $probeCode = "import hashlib,json,sys; print(json.dumps(dict(versionInfo=list(sys.version_info[:3]),executable=sys.executable,fileDigest=callable(getattr(hashlib,'file_digest',None)))))"
    $output = & $command.Source -I -S -c $probeCode
    if ($LASTEXITCODE -ne 0) { throw 'Python 3.11 or newer is required; the runtime probe failed.' }
    try {
        $probe = ($output -join [Environment]::NewLine) | ConvertFrom-Json
        $version = [Version]::new([int]$probe.versionInfo[0], [int]$probe.versionInfo[1], [int]$probe.versionInfo[2])
        if ($version -lt [Version]'3.11' -or $probe.fileDigest -ne $true) {
            throw 'unsupported runtime'
        }
        if (-not [IO.Path]::IsPathRooted([string]$probe.executable) -or
            -not (Test-Path -LiteralPath $probe.executable -PathType Leaf)) { throw 'missing executable' }
    }
    catch { throw 'Python 3.11 or newer with hashlib.file_digest is required for native fixture checks.' }
    try {
        $sha256 = (Get-FileHash -LiteralPath $probe.executable -Algorithm SHA256 -ErrorAction Stop).Hash.ToLowerInvariant()
    }
    catch {
        throw [InvalidOperationException]::new(
            "Python prerequisite failed while hashing resolved executable '$($probe.executable)': $($_.Exception.Message)",
            $_.Exception)
    }
    return [pscustomobject]@{
        Executable = [IO.Path]::GetFullPath([string]$probe.executable)
        VersionInfo = @($probe.versionInfo)
        Sha256 = $sha256
        ProbeCommand = @($command.Source, '-I', '-S', '-c', $probeCode)
    }
}

function Get-OpenBladeNativeFixturePrerequisiteProjects {
    param([Parameter(Mandatory)][string]$Repository)
    $manifest = Get-Content -Raw -LiteralPath (Join-Path $Repository 'build/NativeWindowsFixtureManifest.json') | ConvertFrom-Json
    $expected = @('tests/OpenBlade.Service.Tests/OpenBlade.Service.Tests.csproj',
        'tests/OpenBlade.Contracts.Tests/OpenBlade.Contracts.Tests.csproj',
        'tests/OpenBlade.Deployment.Tests/OpenBlade.Deployment.Tests.csproj')
    if (@($manifest.managedProjects).Count -ne $expected.Count -or
        (Compare-Object -ReferenceObject $expected -DifferenceObject @($manifest.managedProjects))) {
        throw 'Unreviewed managed fixture prerequisite scope.'
    }
    return $expected
}

function Get-OpenBladeNativeFixtureDiskPolicy {
    param([Parameter(Mandatory)][long]$AvailableBytes)
    if ($AvailableBytes -lt 25GB) { return 'Stop' }
    if ($AvailableBytes -lt 50GB) { return 'Warn' }
    return 'Ready'
}

# The 25/50 GiB gate protects the developer host, whose C: also holds the
# shared Cargo cache. GitHub-hosted runners are discarded after each job, so a
# full disk there fails only that job. GitHub sets both values itself; a
# self-hosted runner reports 'self-hosted' and keeps the gate.
function Test-OpenBladeDisposableHostedRunner {
    param(
        [AllowNull()][string]$GitHubActions = $env:GITHUB_ACTIONS,
        [AllowNull()][string]$RunnerEnvironment = $env:RUNNER_ENVIRONMENT)
    return $GitHubActions -ceq 'true' -and $RunnerEnvironment -ceq 'github-hosted'
}

function Assert-OpenBladeNativeFixtureDiskSpace {
    param(
        [scriptblock]$ReadAvailableBytes = { ([IO.DriveInfo]::new('C:\')).AvailableFreeSpace },
        [bool]$DisposableHostedRunner = (Test-OpenBladeDisposableHostedRunner))
    $free = [long](& $ReadAvailableBytes)
    if ($DisposableHostedRunner) {
        Write-Host ('C: has {0:N1} GiB free. The developer-host 25 GiB gate does not apply to this GitHub-hosted runner.' -f ($free / 1GB))
        return
    }
    $policy = Get-OpenBladeNativeFixtureDiskPolicy $free
    if ($policy -eq 'Stop') { throw 'C: is below 25 GiB; stop before building fixtures.' }
    if ($policy -eq 'Warn') {
        # This is a fresh invocation: it has not created a completed build or
        # disposable output. Existing shared-cache and temp entries are not ours
        # to reclaim. Report that boundary, recheck C:, and follow the 25-GiB gate.
        Write-Warning 'C: is below 50 GiB. This fresh run has no safely attributable finished outputs to reclaim; no shared cache or unrelated scratch was deleted.'
        $free = [long](& $ReadAvailableBytes)
        if ((Get-OpenBladeNativeFixtureDiskPolicy $free) -eq 'Stop') {
            throw 'C: is below 25 GiB after recheck; stop before building fixtures.'
        }
    }
}

function Get-OpenBladeNativeFixtureEnvironmentNames {
    @('OPENBLADE_NATIVE_THERMAL_RESPONSES', 'OPENBLADE_NATIVE_BATTERY_RESPONSES',
      'OPENBLADE_NATIVE_BATTERY_PUBLICATIONS', 'OPENBLADE_NATIVE_TFC_CORRECTION_FRAMES',
      'OPENBLADE_NATIVE_EDGE_FRAMES', 'OPENBLADE_NATIVE_EDGE_FIXTURES',
      'OPENBLADE_EDGE_MANAGED_ROOT', 'OPENBLADE_EDGE_V79_INPUT_ROOT',
      'OPENBLADE_HISTORY_MANAGED_FIXTURE_ROOT', 'OPENBLADE_THERMAL_FIXTURE_ROOT',
      'OPENBLADE_THERMAL_FIXTURE_SID', 'OPENBLADE_FIXTURE_CONFIGURATION',
      'CARGO_TARGET_DIR', 'CARGO_NET_OFFLINE', 'RUSTUP_OFFLINE', 'RUSTUP_TOOLCHAIN')
}

function Assert-OpenBladeFixturePlainPath {
    param([Parameter(Mandatory)][string]$Path)
    if (-not [IO.Path]::IsPathFullyQualified($Path) -or $Path.StartsWith('\\')) {
        throw 'An absolute local NTFS fixture path is required.'
    }
    $resolved = [IO.Path]::GetFullPath($Path)
    $drive = [IO.DriveInfo]::new([IO.Path]::GetPathRoot($resolved))
    if ($drive.DriveFormat -ne 'NTFS') { throw 'Fixtures require NTFS.' }
    $current = $resolved
    while ($current) {
        if (Test-Path -LiteralPath $current) {
            if ((Get-Item -LiteralPath $current -Force).Attributes -band [IO.FileAttributes]::ReparsePoint) {
                throw "Fixture ancestry contains a reparse point: $current"
            }
        }
        $parent = [IO.Path]::GetDirectoryName($current)
        if ($parent -eq $current) { break }
        $current = $parent
    }
}

function New-OpenBladePrivateFixtureDirectory {
    param([Parameter(Mandatory)][string]$Path, [Parameter(Mandatory)][string]$Sid)
    if (Test-Path -LiteralPath $Path) { throw "Fixture path already exists: $Path" }
    $security = [Security.AccessControl.DirectorySecurity]::new()
    $identity = [Security.Principal.SecurityIdentifier]::new($Sid)
    $security.SetOwner($identity)
    $security.SetAccessRuleProtection($true, $false)
    $rule = [Security.AccessControl.FileSystemAccessRule]::new($identity,
        [Security.AccessControl.FileSystemRights]::FullControl,
        [Security.AccessControl.InheritanceFlags]'ContainerInherit,ObjectInherit',
        [Security.AccessControl.PropagationFlags]::None, [Security.AccessControl.AccessControlType]::Allow)
    $security.AddAccessRule($rule)
    [IO.FileSystemAclExtensions]::Create([IO.DirectoryInfo]::new($Path), $security)
}

function Invoke-OpenBladeNativeFixtureScope {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$Repository,
        [ValidateSet('Debug', 'Release')][string]$Configuration = 'Debug',
        [ValidateSet('PrepareOnly', 'VerifySelected')][string]$Mode = 'PrepareOnly',
        [string]$OutputRoot = (Join-Path ([IO.Path]::GetTempPath()) 'openblade-native-fixtures'),
        [scriptblock]$Action = {}
    )
    $ErrorActionPreference = 'Stop'
    $pythonRuntime = Get-OpenBladeNativeFixturePythonRuntime
    if (-not $IsWindows -or [Runtime.InteropServices.RuntimeInformation]::ProcessArchitecture -ne 'X64') {
        throw 'Native fixture scope requires Windows x64 PowerShell 7.'
    }
    Assert-OpenBladeNativeFixtureDiskSpace
    $absoluteOutput = [IO.Path]::GetFullPath($OutputRoot)
    $allowedParents = @('C:\tmp', [IO.Path]::GetTempPath(), $env:RUNNER_TEMP) | Where-Object { $_ }
    if (-not @($allowedParents | Where-Object {
        $base = [IO.Path]::GetFullPath($_).TrimEnd('\')
        $absoluteOutput.Equals($base, [StringComparison]::OrdinalIgnoreCase) -or
            $absoluteOutput.StartsWith($base + '\', [StringComparison]::OrdinalIgnoreCase)
    }).Count) { throw 'Fixture output must be beneath the explicit runner temporary directory.' }
    Assert-OpenBladeFixturePlainPath $OutputRoot
    $saved = @{}
    foreach ($name in Get-OpenBladeNativeFixtureEnvironmentNames) {
        $saved[$name] = [Environment]::GetEnvironmentVariable($name, 'Process')
    }
    foreach ($name in @('OPENBLADE_NATIVE_THERMAL_RESPONSES', 'OPENBLADE_NATIVE_BATTERY_RESPONSES',
        'OPENBLADE_NATIVE_BATTERY_PUBLICATIONS', 'OPENBLADE_NATIVE_TFC_CORRECTION_FRAMES', 'OPENBLADE_NATIVE_EDGE_FRAMES')) {
        if ($saved[$name]) { throw "Inherited corpus is forbidden: $name" }
    }
    [IO.Directory]::CreateDirectory($OutputRoot) | Out-Null
    $run = Join-Path ([IO.Path]::GetFullPath($OutputRoot)) ('run-' + [Guid]::NewGuid().ToString('N'))
    if (Test-Path -LiteralPath $run) { throw 'Fixture run root already exists.' }
    [IO.Directory]::CreateDirectory($run) | Out-Null
    $driver = Join-Path $Repository 'build/native_windows_fixtures.py'
    $python = $pythonRuntime.Executable
    $lock = $null
    try {
        $pythonRuntime | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $run 'python-runtime.json') -Encoding utf8
        # Test setup boundary only. Exactly one current-process identity query;
        # neither this SID nor injected peers establish real IPC authentication.
        $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
        try { $sid = $identity.User.Value } finally { $identity.Dispose() }
        if ($sid -notmatch '^S-1-5-21-\d+-\d+-\d+-\d+$') { throw 'An own-user SID is required.' }
        foreach ($leaf in @('edge-producer', 'edge-suite', 'history', 'native-history')) {
            New-OpenBladePrivateFixtureDirectory (Join-Path $run $leaf) $sid
        }
        foreach ($leaf in @('protected-storage', 'battery-corrections', 'owner-history', 'owner-journal', 'owner-journal-foreign') + @(0..17 | ForEach-Object { 'history-cut-{0:d2}' -f $_ })) {
            New-OpenBladePrivateFixtureDirectory (Join-Path $run ('native-history/' + $leaf)) $sid
        }
        $env:OPENBLADE_THERMAL_FIXTURE_SID = $sid
        $env:OPENBLADE_THERMAL_FIXTURE_ROOT = Join-Path $run 'native-history'
        $env:OPENBLADE_HISTORY_MANAGED_FIXTURE_ROOT = Join-Path $run 'history'
        $env:OPENBLADE_NATIVE_EDGE_FIXTURES = '1'
        $env:OPENBLADE_EDGE_V79_INPUT_ROOT = Join-Path $Repository 'tools/openblade-windows-host/tests/fixtures'
        $env:OPENBLADE_FIXTURE_CONFIGURATION = $Configuration
        $env:CARGO_TARGET_DIR = 'C:\build-cache\cargo\openblade-core'
        $env:CARGO_NET_OFFLINE = 'true'
        $env:RUSTUP_OFFLINE = '1'
        $env:RUSTUP_TOOLCHAIN = '1.99.1'
        [IO.Directory]::CreateDirectory($env:CARGO_TARGET_DIR) | Out-Null
        # Cross-process exclusive lease for this stage. Never run two tuples in parallel.
        $lock = [IO.File]::Open((Join-Path $env:CARGO_TARGET_DIR '.openblade-build.lock'),
            [IO.FileMode]::OpenOrCreate, [IO.FileAccess]::ReadWrite, [IO.FileShare]::None)
        & $python $driver --repository $Repository --root $run --mode $Mode
        if ($LASTEXITCODE -ne 0) { throw "Native fixture preparation failed ($LASTEXITCODE). Evidence: $run" }
        $receipt = Get-Content -Raw -LiteralPath (Join-Path $run 'receipt.json') | ConvertFrom-Json
        foreach ($property in $receipt.environment.PSObject.Properties) {
            if ($property.Name -notin (Get-OpenBladeNativeFixtureEnvironmentNames)) { throw 'Unknown fixture environment export.' }
            [Environment]::SetEnvironmentVariable($property.Name, [string]$property.Value, 'Process')
        }
        & $python $driver --repository $Repository --root $run --mode Check
        if ($LASTEXITCODE -ne 0) { throw 'Prepared fixture input verification failed.' }
        $receiptHash = (Get-FileHash -LiteralPath (Join-Path $run 'receipt.json') -Algorithm SHA256).Hash
        $invocation = @{ action = $Action.ToString(); startedUtc = [DateTime]::UtcNow.ToString('o'); inputs = $receipt.inputs; completed = $false }
        try { & $Action; $invocation.completed = $true } catch { $invocation.error = $_.ToString(); throw } finally {
            $invocation | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $run 'managed-scope.json') -Encoding utf8
            if ((Get-FileHash -LiteralPath (Join-Path $run 'receipt.json') -Algorithm SHA256).Hash -ne $receiptHash) { throw 'Fixture receipt changed during managed invocation.' }
            & $python $driver --repository $Repository --root $run --mode Check
            if ($LASTEXITCODE -ne 0) { throw 'Fixture inputs changed during managed invocation.' }
        }
        Write-Host "Verified native fixture inputs: $run"
    }
    finally {
        if ($lock) { $lock.Dispose() }
        foreach ($name in $saved.Keys) { [Environment]::SetEnvironmentVariable($name, $saved[$name], 'Process') }
        # Preserve small logs, corpora and unique failure evidence. Cleanup owns only
        # this run's created protected fixture directories, after all consumers stop.
        $removed = @()
        foreach ($leaf in @('edge-producer', 'edge-suite', 'history', 'native-history', 'managed-ffi-payload')) {
            $path = Join-Path $run $leaf
            Assert-OpenBladeFixturePlainPath $path
            if (Test-Path -LiteralPath $path) { Remove-Item -LiteralPath $path -Recurse -Force; $removed += $path }
        }
        @{ root = $run; removed = $removed; retained = 'Small logs, TRX, receipt, source hashes and raw corpora only; no build output.' } |
            ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $run 'cleanup.json') -Encoding utf8
    }
}
