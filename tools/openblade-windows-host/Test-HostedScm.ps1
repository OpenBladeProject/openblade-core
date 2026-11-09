# This smoke test changes SCM state only inside the disposable hosted job.
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $false

if (-not $IsWindows -or $env:GITHUB_ACTIONS -cne 'true' -or
    $env:RUNNER_ENVIRONMENT -cne 'github-hosted' -or $env:GITHUB_EVENT_NAME -cne 'pull_request') {
    throw 'SCM smoke is restricted to GitHub-hosted Windows pull request jobs.'
}

$name = 'OpenBladeRustReadOnlyDev'
$account = 'NT AUTHORITY\LocalService'
$pipeName = 'OpenBlade.Rust.ReadOnly.Dev.scm'
$sc = Join-Path $env:SystemRoot 'System32/sc.exe'
$deadline = [TimeSpan]::FromSeconds(15)

Add-Type -TypeDefinition @'
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
namespace OpenBlade.ScmSmoke {
    public static class PipeProbe {
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, ExactSpelling = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool WaitNamedPipeW(string name, uint timeout);

        public static int Error(string name) =>
            WaitNamedPipeW(@"\\.\pipe\" + name, 500) ? 0 : Marshal.GetLastWin32Error();

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern SafeProcessHandle OpenProcess(uint access, bool inheritHandle, uint pid);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern uint WaitForSingleObject(SafeProcessHandle process, uint timeout);
    }
}
'@

function Invoke-Scm([string[]]$Arguments) {
    $output = & $sc @Arguments
    if ($LASTEXITCODE -ne 0) { throw "sc.exe $($Arguments[0]) failed ($LASTEXITCODE): $output" }
}

function Assert-NoService {
    $output = & $sc query $name
    if ($LASTEXITCODE -ne 1060) {
        throw "Expected the development service to be absent (1060), got $($LASTEXITCODE): $output"
    }
}

function Assert-PipeState([int]$Expected) {
    $result = [OpenBlade.ScmSmoke.PipeProbe]::Error($pipeName)
    if ($result -ne $Expected) { throw "Expected pipe probe result $Expected, got $result." }
}

function Get-ServiceEvidence {
    Get-CimInstance Win32_Service -Filter "Name='$name'" -OperationTimeoutSec 5
}

function Stop-SmokeService {
    $controller.Refresh()
    if ($controller.Status -eq 'StartPending') {
        $timer = [Diagnostics.Stopwatch]::StartNew()
        do {
            Start-Sleep -Milliseconds 100
            $controller.Refresh()
        } while ($controller.Status -eq 'StartPending' -and $timer.Elapsed -lt $deadline)
    }
    if ($controller.Status -notin @('Stopped', 'StopPending')) { $controller.Stop($false) }
    $controller.WaitForStatus('Stopped', $deadline)
}

Assert-NoService
Assert-PipeState 2 # ERROR_FILE_NOT_FOUND. Access denied or busy is not absence.

$source = Get-Item -LiteralPath (Join-Path $PSScriptRoot 'target/x86_64-pc-windows-msvc/debug/openblade-windows-host.exe')
if ($source.PSIsContainer -or ($source.Attributes -band [IO.FileAttributes]::ReparsePoint)) {
    throw 'Expected the freshly built development executable.'
}
$sourceHash = (Get-FileHash -LiteralPath $source.FullName -Algorithm SHA256).Hash
$staging = Join-Path $env:ProgramData ('OpenBlade-Rust-SCM-' + [Guid]::NewGuid().ToString('N'))
$binary = Join-Path $staging $source.Name
$command = '"' + $binary + '" --scm'
$createdDirectory = $false
$createdService = $false
$controller = $null
try {
    New-Item -Path $staging -ItemType Directory | Out-Null
    $createdDirectory = $true
    Copy-Item -LiteralPath $source.FullName -Destination $binary
    if ((Get-FileHash -LiteralPath $binary -Algorithm SHA256).Hash -cne $sourceHash) {
        throw 'Staged binary hash differs from the tested build.'
    }
    Invoke-Scm @('create', $name, 'type=', 'own', 'start=', 'demand', 'binPath=', $command, 'obj=', $account)
    $createdService = $true
    $service = Get-ServiceEvidence
    if ($service.PathName -cne $command -or $service.StartName -ine $account -or $service.State -ne 'Stopped') {
        throw 'SCM did not record the expected binary, account and stopped state.'
    }
    Write-Host "Registered $name under $account. SHA-256 $sourceHash"
    $controller = [ServiceProcess.ServiceController]::new($name)

    $claim = [IO.Pipes.NamedPipeServerStream]::new(
        $pipeName, [IO.Pipes.PipeDirection]::InOut, 1,
        [IO.Pipes.PipeTransmissionMode]::Byte, [IO.Pipes.PipeOptions]::CurrentUserOnly)
    try {
        Assert-PipeState 0
        # The start RPC can succeed before ServiceMain reports a failure.
        try { $controller.Start() } catch { Write-Host $_.Exception.Message }
        $timer = [Diagnostics.Stopwatch]::StartNew()
        do {
            $failure = Get-ServiceEvidence
            if ($failure.State -eq 'Running') { throw 'SCM reported RUNNING with an occupied pipe.' }
            if ($failure.State -eq 'Stopped' -and $failure.ExitCode -eq 31 -and $failure.ProcessId -eq 0) { break }
            Start-Sleep -Milliseconds 100
        } while ($timer.Elapsed -lt $deadline)
        if ($failure.State -ne 'Stopped' -or $failure.ExitCode -ne 31 -or $failure.ProcessId -ne 0) {
            throw "Expected occupied-pipe startup failure (31), got $($failure.State)/$($failure.ExitCode)."
        }
        Assert-PipeState 0
        Write-Host 'Occupied pipe caused the expected native startup failure.'
    } finally {
        $claim.Dispose()
    }
    Assert-PipeState 2

    foreach ($cycle in 1..2) {
        $controller.Start()
        $controller.WaitForStatus('Running', $deadline)
        $running = Get-ServiceEvidence
        if ($running.State -ne 'Running' -or $running.ProcessId -eq 0 -or $running.ExitCode -ne 0) {
            throw "Unexpected RUNNING state on cycle $cycle."
        }
        Assert-PipeState 0
        Write-Host "Cycle $cycle reported RUNNING with PID $($running.ProcessId) and a listening pipe."

        $client = [IO.Pipes.NamedPipeClientStream]::new(
            '.', $pipeName, [IO.Pipes.PipeDirection]::InOut,
            [IO.Pipes.PipeOptions]::None, [Security.Principal.TokenImpersonationLevel]::Identification)
        try {
            try { $client.Connect(500) } catch {
                if ($_.Exception.GetBaseException() -isnot [UnauthorizedAccessException]) { throw }
                Write-Host "Cycle $cycle denied the runner's different-account pipe open."
            }
            if ($client.IsConnected) { throw 'The runner unexpectedly opened the LocalService pipe.' }
        } finally {
            $client.Dispose()
        }

        # Keep a SYNCHRONIZE-only handle to the exact owner across STOP.
        $process = [OpenBlade.ScmSmoke.PipeProbe]::OpenProcess(0x00100000, $false, $running.ProcessId)
        try {
            if ($process.IsInvalid) { throw "Cannot retain the SCM process handle on cycle $cycle." }
            Stop-SmokeService
            $stopped = Get-ServiceEvidence
            if ($stopped.State -ne 'Stopped' -or $stopped.ProcessId -ne 0 -or $stopped.ExitCode -ne 0) {
                throw "Unexpected STOPPED state on cycle $cycle."
            }
            Assert-PipeState 2
            if ([OpenBlade.ScmSmoke.PipeProbe]::WaitForSingleObject($process, 5000) -ne 0) {
                throw "The original SCM process did not exit on cycle $cycle."
            }
        } finally {
            $process.Dispose()
        }
        Write-Host "Cycle $cycle reported STOPPED with exit code 0, retired the pipe and exited."
    }
} finally {
    try {
        if ($createdService) {
            try {
                if ($null -ne $controller) { Stop-SmokeService }
            } finally {
                if ($null -ne $controller) { $controller.Dispose() }
                Invoke-Scm @('delete', $name)
                Assert-NoService
                Write-Host 'Development service registration removed.'
            }
        }
    } finally {
        if ($createdDirectory) {
            if (Test-Path -LiteralPath $binary) { Remove-Item -LiteralPath $binary }
            Remove-Item -LiteralPath $staging
        }
    }
}
