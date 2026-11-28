# Phase 5j native SCM fixture in a disposable Windows VM

This one-shot runbook exercises the private Rust host through the native Windows Service Control Manager and the existing C# tray service client. Use an independently authorized, disposable x64 Windows VM with an English Windows UI, a fixed NTFS C: drive, PowerShell 7, the pinned Rust/MSVC tools, and the repository's .NET SDK and x64 runtime installed under `C:\Program Files\dotnet`. Start with a clean VM snapshot, no OpenBlade installation, no Razer hardware passed through, and no fixture artifacts from a prior run. Use an already elevated, interactive Administrator console for the VM section. The managed peer runs in session 0 as LocalSystem through one temporary scheduled task.

The run uses the fixed `OpenBladeRustPhase5jVm` service, scratch root and executable name, and the private `OpenBlade.Rust.ReadOnly.Dev.phase5j-vm` pipe. The profile, battery observations and renderer are scripted. Keep any resulting evidence private.

## Build before changing SCM

From the repository root on the reviewed Phase 5j checkout:

```powershell
Push-Location rust
try {
    cargo build --locked --release --target x86_64-pc-windows-msvc --features vm-scm-fixture --bin openblade-windows-scm-vm --manifest-path ../tools/openblade-windows-host/Cargo.toml
    if ($LASTEXITCODE -ne 0) { throw 'Rust fixture build failed.' }
} finally {
    Pop-Location
}
dotnet build tools/openblade-windows-host/vm-client/OpenBlade.VmFixture.csproj -c Release
if ($LASTEXITCODE -ne 0) { throw 'Managed fixture build failed.' }
```

Use the exact checkout revision and Rust binary SHA-256 from the separately reviewed artifact record. Record the managed build provenance as well. If using a different VM for the build, transfer only the reviewed fixture outputs and record and check their hashes before staging.

## Run one cycle in the authorized VM

Run the following as one block from the same checkout. START_PENDING and STOP_PENDING have bounded observation windows. Avoid stepping through the block interactively. The input markers are created once with the Setup-style protected leaf ACL. The script verifies the process and pipe separately from SCM state. It requires STOP_PENDING within 15 seconds of launching the peer, then allows up to 12 seconds for renderer acknowledgement. This fits inside the shared 30-second release deadline, which starts when the pending Off request drains its frame. The task starts the protected client DLL directly via the absolute, machine-installed .NET runtime path.

```powershell
$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $false
$root = 'C:\OpenBladeRustPhase5jVm'
$name = 'OpenBladeRustPhase5jVm'
$task = 'OpenBladeRustPhase5jVmClient'
$image = Join-Path $root 'OpenBladeRustPhase5jVm.exe'
$stagedClient = Join-Path $root 'client'
$dotnet = 'C:\Program Files\dotnet\dotnet.exe'
$sc = 'C:\Windows\System32\sc.exe'
$hostBuild = (Resolve-Path 'tools/openblade-windows-host/target/x86_64-pc-windows-msvc/release/openblade-windows-scm-vm.exe').Path
$clientBuild = (Resolve-Path 'tools/openblade-windows-host/vm-client/bin/Release/net10.0-windows').Path
$adminClient = Join-Path $clientBuild 'OpenBlade.VmFixture.exe'
$privateEvidence = [Collections.Generic.List[string]]::new()
$expectedHostHash = Read-Host 'Enter the Rust image SHA-256 from the reviewed artifact record'
if ($expectedHostHash -notmatch '^[0-9A-Fa-f]{64}$') { throw 'Expected a reviewed SHA-256 digest.' }

$identity = [Security.Principal.WindowsIdentity]::GetCurrent()
try {
    $principal = [Security.Principal.WindowsPrincipal]::new($identity)
    if (-not $IsWindows -or $identity.IsSystem -or
        -not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator) -or
        $env:GITHUB_ACTIONS -eq 'true') {
        throw 'Use an elevated non-SYSTEM console in the separately authorized disposable VM.'
    }
} finally {
    $identity.Dispose()
}
if ((Test-Path -LiteralPath $root) -or (Get-ScheduledTask -TaskName $task -ErrorAction SilentlyContinue)) {
    throw 'Fixture directory or scheduled task already exists. Stop and use a clean VM.'
}

Add-Type -TypeDefinition @"
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
public static class Phase5jProcess {
    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern SafeProcessHandle OpenProcess(uint access, bool inheritHandle, uint pid);
    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern uint WaitForSingleObject(SafeProcessHandle process, uint timeout);
}
"@

function Invoke-Fixture([string[]]$FixtureArgs) {
    & $adminClient @FixtureArgs
    if ($LASTEXITCODE -ne 0) { throw "Managed fixture failed in $($FixtureArgs[0])." }
}

function Invoke-Sc([string[]]$ScArgs) {
    $output = & $sc @ScArgs
    if ($LASTEXITCODE -ne 0) { throw "sc.exe $($ScArgs[0]) failed ($LASTEXITCODE): $output" }
    $output
}

function Assert-NoFixtureService {
    $output = & $sc query $name
    if ($LASTEXITCODE -ne 1060) { throw "Expected SCM absence (1060): $output" }
}

function Wait-For([scriptblock]$Probe, [int]$Seconds, [string]$Failure) {
    $clock = [Diagnostics.Stopwatch]::StartNew()
    do {
        $result = & $Probe
        if ($result) { return $result }
        Start-Sleep -Milliseconds 100
    } while ($clock.Elapsed.TotalSeconds -lt $Seconds)
    throw $Failure
}

function Read-State([int]$State, [int]$Checkpoint = -1) {
    $value = (Invoke-Sc @('queryex', $name)) -join "`n"
    if ($value -notmatch "(?m)^\s*STATE\s*:\s*$State\s") { return }
    if ($Checkpoint -ge 0 -and $value -notmatch "(?m)^\s*CHECKPOINT\s*:\s*0x$Checkpoint\s*$") { return }
    $value
}

Assert-NoFixtureService
$sourceHash = (Get-FileHash -LiteralPath $hostBuild -Algorithm SHA256).Hash
if ($sourceHash -ine $expectedHostHash) { throw 'The built Rust image differs from the reviewed artifact.' }
$owner = $null
try {
    Invoke-Fixture @('prepare')
    Invoke-Fixture @('pipe-absent')
    Invoke-Fixture @('stage', $hostBuild, $expectedHostHash)
    if ((Get-FileHash -LiteralPath $image -Algorithm SHA256).Hash -cne $sourceHash) {
        throw 'Staged Rust image differs from the reviewed build.'
    }
    if (((Get-Item -LiteralPath $clientBuild).Attributes -band [IO.FileAttributes]::ReparsePoint) -or
        (Get-ChildItem -LiteralPath $clientBuild -Recurse -Force |
            Where-Object { $_.Attributes -band [IO.FileAttributes]::ReparsePoint } |
            Select-Object -First 1)) {
        throw 'The managed build contains a reparse point.'
    }
    # The prepare step creates the client directory with its protected owner and ACL.
    Get-ChildItem -LiteralPath $clientBuild -Force | ForEach-Object {
        Copy-Item -LiteralPath $_.FullName -Destination $stagedClient -Recurse
    }
    Invoke-Fixture @('seal-client')
    foreach ($builtFile in (Get-ChildItem -LiteralPath $clientBuild -Recurse -Force -File)) {
        $relative = [IO.Path]::GetRelativePath($clientBuild, $builtFile.FullName)
        $stagedFile = Join-Path $stagedClient $relative
        if ((Get-FileHash -LiteralPath $builtFile.FullName -Algorithm SHA256).Hash -cne
            (Get-FileHash -LiteralPath $stagedFile -Algorithm SHA256).Hash) {
            throw "Staged managed file differs from the reviewed build: $relative"
        }
    }
    # The fixture verifies explicit protected ACLs and ownership on the entire staged managed tree.
    $clientDll = Join-Path $stagedClient 'OpenBlade.VmFixture.dll'

    Invoke-Sc @('create', $name, 'type=', 'own', 'start=', 'demand', 'binPath=', $image, 'obj=', 'LocalSystem')
    $config = (Invoke-Sc @('qc', $name)) -join "`n"
    if ($config -notmatch "(?m)^\s*BINARY_PATH_NAME\s*:\s*$([regex]::Escape($image))\s*$" -or
        $config -notmatch '(?m)^\s*SERVICE_START_NAME\s*:\s*LocalSystem\s*$') {
        throw 'SCM did not record the expected fixed image and LocalSystem account.'
    }
    Invoke-Sc @('start', $name)
    $starting = Wait-For { Read-State 2 2 } 10 'Missing START_PENDING checkpoint 2.'
    $privateEvidence.Add($starting)
    Invoke-Fixture @('pipe-absent')
    Invoke-Fixture @('release-start')

    $running = Wait-For { Read-State 4 } 12 'Missing RUNNING.'
    $privateEvidence.Add($running)
    if ($running -notmatch '(?m)^\s*PID\s*:\s*([1-9][0-9]*)\s*$') { throw 'Missing SCM PID.' }
    $serverPid = [uint32]$Matches[1]
    $owner = [Phase5jProcess]::OpenProcess(0x00100000, $false, $serverPid)
    if ($owner.IsInvalid -or [Phase5jProcess]::WaitForSingleObject($owner, 0) -ne 258) {
        throw 'Could not retain the live SCM process.'
    }
    Invoke-Fixture @('deny')
    $privateEvidence.Add("Separate Administrator open denied with Win32 error 5 for SCM PID $serverPid.")

    $action = New-ScheduledTaskAction -Execute $dotnet -Argument ('"' + $clientDll + '" exercise ' + $serverPid) -WorkingDirectory $stagedClient
    $runAs = New-ScheduledTaskPrincipal -UserId 'SYSTEM' -LogonType ServiceAccount -RunLevel Highest
    $settings = New-ScheduledTaskSettingsSet -ExecutionTimeLimit (New-TimeSpan -Minutes 1)
    Register-ScheduledTask -TaskName $task -Action $action -Principal $runAs -Settings $settings | Out-Null
    $peerClock = [Diagnostics.Stopwatch]::StartNew()
    Start-ScheduledTask -TaskName $task
    $accepted = Join-Path $root 'client.accepted'
    Wait-For { Test-Path -LiteralPath $accepted -PathType Leaf } 12 'Managed peer did not verify the profile commit and pending Off.'
    Invoke-Sc @('stop', $name)

    $stopping = Wait-For { Read-State 3 } 4 'Missing STOP_PENDING.'
    if ($peerClock.Elapsed.TotalSeconds -ge 15) { throw 'The managed launch-to-STOP_PENDING budget expired.' }
    $privateEvidence.Add($stopping)
    $drained = Join-Path $root 'client.drained'
    Wait-For {
        (Test-Path -LiteralPath $drained -PathType Leaf) -and (Get-ScheduledTask -TaskName $task).State -eq 'Ready'
    } 8 'The pending managed Off response did not drain before renderer release.'
    if ((Get-ScheduledTaskInfo -TaskName $task).LastTaskResult -ne 0) { throw 'The SYSTEM managed fixture failed.' }
    if (-not (Read-State 3)) { throw 'SCM left STOP_PENDING before renderer release.' }
    if ([Phase5jProcess]::WaitForSingleObject($owner, 0) -ne 258) { throw 'The SCM process exited before renderer release.' }
    Invoke-Fixture @('lease-held')
    $privateEvidence.Add('Pending response closed while the renderer gate and writer lease remained held.')
    Invoke-Fixture @('release-renderer')

    $stopped = Wait-For { Read-State 1 } 12 'Missing STOPPED.'
    $privateEvidence.Add($stopped)
    if ($stopped -notmatch '(?m)^\s*WIN32_EXIT_CODE\s*:\s*0\s' -or
        $stopped -notmatch '(?m)^\s*SERVICE_EXIT_CODE\s*:\s*0\s' -or
        $stopped -notmatch '(?m)^\s*PID\s*:\s*0\s*$') {
        throw 'STOPPED reported a nonzero exit code or process ID.'
    }
    if ([Phase5jProcess]::WaitForSingleObject($owner, 5000) -ne 0) { throw 'The retained service process did not exit.' }
    Invoke-Fixture @('pipe-absent')
    Invoke-Fixture @('verify')
    $privateEvidence.Add((Get-Content -LiteralPath $accepted -Raw))
    $privateEvidence.Add((Get-Content -LiteralPath $drained -Raw))
    $privateEvidence.Add("Rust image SHA-256 $sourceHash")

    Unregister-ScheduledTask -TaskName $task -Confirm:$false
    Invoke-Sc @('delete', $name)
    Assert-NoFixtureService
    if (Get-ScheduledTask -TaskName $task -ErrorAction SilentlyContinue) { throw 'Fixture task remains registered.' }
    if ((Resolve-Path -LiteralPath $root).Path -cne 'C:\OpenBladeRustPhase5jVm' -or
        ((Get-Item -LiteralPath $root).Attributes -band [IO.FileAttributes]::ReparsePoint) -or
        (Get-ChildItem -LiteralPath $root -Recurse -Force |
            Where-Object { $_.Attributes -band [IO.FileAttributes]::ReparsePoint } |
            Select-Object -First 1)) {
        throw 'Refusing to remove an unexpected fixture directory.'
    }
    Remove-Item -LiteralPath $root -Recurse
    if (Test-Path -LiteralPath $root) { throw 'Fixture directory remains.' }
} finally {
    if ($null -ne $owner) { $owner.Dispose() }
    $privateEvidence
}
```

On any error, stop the run and retain the observations privately. Revert the disposable VM to its pre-test snapshot to clean up any partial registration, task or staged files. If reverting is unavailable, leave the VM isolated and record the exact remaining state before arranging cleanup. Avoid resubmitting the test or reusing either release marker.

For a passing run, record the SCM checkpoints and exit codes, the retained PID, the denied different-account open, and the two managed observations. While STOP_PENDING, the pending client response closes and the configuration writer lease still rejects an independent exclusive open. Releasing the renderer lets the process exit. Afterward the managed verifier acquires the lease, checks the profile against the fixture oracle, validates the digest marker against actual bytes, and confirms the durable hashes equal the accepted readback. The service, scheduled task, pipe and scratch directory must all be gone.

This run establishes evidence for the private Windows SCM path and the scripted service lifecycle. Physical device behavior, production deployment and activation, and the installed OpenBlade service remain outside its scope.
