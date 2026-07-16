[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [ValidateNotNullOrEmpty()]
    [string]$SessionPath,

    [ValidateRange(1000, 60000)]
    [int]$TimeoutMilliseconds = 10000,

    [switch]$ForceFallback,

    [Parameter(DontShow)]
    [switch]$SignalWorker
)

$ErrorActionPreference = 'Stop'

function Get-CaptureOwnership {
    param([Parameter(Mandatory)][string]$Path)

    $resolvedPath = (Resolve-Path -LiteralPath $Path -ErrorAction Stop).Path
    $session = Get-Content -LiteralPath $resolvedPath -Raw | ConvertFrom-Json
    $captureProcessId = [uint32]$session.processId
    $processGroupId = [uint32]$session.processGroupId
    if ($session.schemaVersion -ne 1 -or $captureProcessId -eq 0 -or
        $processGroupId -eq 0 -or $processGroupId -ne $captureProcessId) {
        throw 'Capture ownership session does not describe a nonzero PID-rooted process group.'
    }

    $executablePath = [IO.Path]::GetFullPath([string]$session.executablePath)
    if ([IO.Path]::GetFileName($executablePath) -ine 'USBPcapCMD.exe') {
        throw "Capture ownership session names an unexpected executable: $executablePath"
    }

    $process = Get-Process -Id $captureProcessId -ErrorAction Stop
    try {
        if ($process.ProcessName -ine 'USBPcapCMD') {
            throw "Refusing to signal non-USBPcap process $captureProcessId ($($process.ProcessName))."
        }
        if ([IO.Path]::GetFullPath($process.Path) -ine $executablePath) {
            throw "USBPcap process $captureProcessId does not match the owned executable path."
        }
        if ($process.StartTime.ToUniversalTime().Ticks -ne [long]$session.processStartTimeUtcTicks) {
            throw "USBPcap process $captureProcessId does not match the owned process start time."
        }
    }
    catch {
        $process.Dispose()
        throw
    }

    [pscustomobject]@{
        SessionPath = $resolvedPath
        ProcessId = $captureProcessId
        ProcessGroupId = $processGroupId
        OutputPath = [IO.Path]::GetFullPath([string]$session.outputPath)
        Process = $process
    }
}

if ($SignalWorker) {
    if (-not ('OpenBlade.UsbPcapConsoleSignal' -as [type])) {
        Add-Type -TypeDefinition @'
using System;
using System.ComponentModel;
using System.Runtime.InteropServices;

namespace OpenBlade
{
    public static class UsbPcapConsoleSignal
    {
        private const uint CTRL_BREAK_EVENT = 1;
        private const int ErrorInvalidParameter = 87;

        [UnmanagedFunctionPointer(CallingConvention.Winapi)]
        private delegate bool HandlerRoutine(uint controlType);

        private static readonly HandlerRoutine IgnoreControlEvent = controlType => true;

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool FreeConsole();

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool AttachConsole(uint processId);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool SetConsoleCtrlHandler(HandlerRoutine handlerRoutine, bool add);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GenerateConsoleCtrlEvent(uint controlEvent, uint processGroupId);

        public static void SendBreak(uint processId, uint processGroupId)
        {
            if (processId == 0 || processGroupId == 0 || processGroupId != processId)
            {
                throw new ArgumentException("The USBPcap process must be the root of a nonzero process group.");
            }

            if (!FreeConsole())
            {
                int detachError = Marshal.GetLastWin32Error();
                if (detachError != ErrorInvalidParameter)
                {
                    throw new Win32Exception(detachError, "Could not detach the signal worker console.");
                }
            }

            if (!AttachConsole(processId))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not attach to the USBPcap console.");
            }

            bool handlerInstalled = false;
            try
            {
                if (!SetConsoleCtrlHandler(IgnoreControlEvent, true))
                {
                    throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not protect the signal worker from console events.");
                }
                handlerInstalled = true;

                if (!GenerateConsoleCtrlEvent(CTRL_BREAK_EVENT, processGroupId))
                {
                    throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not send Ctrl+Break to the USBPcap process group.");
                }
            }
            finally
            {
                if (handlerInstalled)
                {
                    SetConsoleCtrlHandler(IgnoreControlEvent, false);
                }
                FreeConsole();
                GC.KeepAlive(IgnoreControlEvent);
            }
        }
    }
}
'@
    }

    $ownership = $null
    $forced = $false
    try {
        $ownership = Get-CaptureOwnership -Path $SessionPath
        try {
            [OpenBlade.UsbPcapConsoleSignal]::SendBreak(
                $ownership.ProcessId,
                $ownership.ProcessGroupId)
            if (-not $ownership.Process.WaitForExit($TimeoutMilliseconds)) {
                throw "USBPcap process $($ownership.ProcessId) did not stop after targeted Ctrl+Break."
            }
        }
        catch {
            if (-not $ForceFallback) {
                throw
            }

            Write-Warning 'Targeted USBPcap shutdown failed. Using Stop-Process -Force may lose buffered capture data.'
            if (-not $ownership.Process.HasExited) {
                Stop-Process -Id $ownership.ProcessId -Force -ErrorAction Stop
                [void]$ownership.Process.WaitForExit($TimeoutMilliseconds)
                if (-not $ownership.Process.HasExited) {
                    throw "USBPcap process $($ownership.ProcessId) did not stop after the explicit force fallback."
                }
            }
            $forced = $true
        }

        if (-not $forced) {
            if (-not (Test-Path -LiteralPath $ownership.OutputPath -PathType Leaf)) {
                throw "USBPcap exited without creating the expected PCAP: $($ownership.OutputPath)"
            }
            if ((Get-Item -LiteralPath $ownership.OutputPath).Length -eq 0) {
                throw "USBPcap exited but the expected PCAP is empty: $($ownership.OutputPath)"
            }
        }

        $stopMode = if ($forced) { 'Forced' } else { 'Graceful' }
        [Console]::Out.WriteLine("OPENBLADE_USBPCAP_STOP_MODE=$stopMode")
        exit 0
    }
    catch {
        Write-Error $_
        exit 1
    }
    finally {
        if ($null -ne $ownership) {
            $ownership.Process.Dispose()
        }
    }
}

$resolvedSessionPath = (Resolve-Path -LiteralPath $SessionPath -ErrorAction Stop).Path
$ownership = Get-CaptureOwnership -Path $resolvedSessionPath
try {
    $hostExecutable = (Get-Process -Id $PID -ErrorAction Stop).Path
    $startInfo = [Diagnostics.ProcessStartInfo]::new()
    $startInfo.FileName = $hostExecutable
    $startInfo.UseShellExecute = $false
    $startInfo.CreateNoWindow = $true
    $startInfo.RedirectStandardOutput = $true
    $startInfo.RedirectStandardError = $true
    $startInfo.WorkingDirectory = Split-Path -Parent $resolvedSessionPath
    $escapedScriptPath = $PSCommandPath.Replace("'", "''")
    $escapedSessionPath = $resolvedSessionPath.Replace("'", "''")
    $workerCommand = "& '$escapedScriptPath' -SessionPath '$escapedSessionPath' " +
        "-TimeoutMilliseconds $TimeoutMilliseconds -SignalWorker"
    if ($ForceFallback) {
        $workerCommand += ' -ForceFallback'
    }
    $encodedWorkerCommand = [Convert]::ToBase64String([Text.Encoding]::Unicode.GetBytes($workerCommand))
    $startInfo.Arguments = "-NoLogo -NoProfile -NonInteractive -ExecutionPolicy Bypass -EncodedCommand $encodedWorkerCommand"

    $worker = [Diagnostics.Process]::Start($startInfo)
    if ($null -eq $worker) {
        throw 'Could not start the isolated USBPcap signal worker.'
    }
    try {
        $standardOutput = $worker.StandardOutput.ReadToEndAsync()
        $standardError = $worker.StandardError.ReadToEndAsync()
        $worker.WaitForExit()
        $workerExitCode = $worker.ExitCode
        $workerOutput = $standardOutput.GetAwaiter().GetResult().Trim()
        $workerError = $standardError.GetAwaiter().GetResult().Trim()
    }
    finally {
        $worker.Dispose()
    }

    if ($workerExitCode -ne 0) {
        $diagnostic = @($workerOutput, $workerError) |
            Where-Object { -not [string]::IsNullOrWhiteSpace($_) } |
            Select-Object -Unique
        $suffix = if ($diagnostic.Count -gt 0) { "`n$($diagnostic -join "`n")" } else { '' }
        throw "USBPcap signal worker failed with exit code $workerExitCode.$suffix"
    }

    $stopModeMatch = [Text.RegularExpressions.Regex]::Match(
        $workerOutput,
        '(?m)^OPENBLADE_USBPCAP_STOP_MODE=(Graceful|Forced)$')
    if (-not $stopModeMatch.Success) {
        throw 'USBPcap signal worker succeeded without returning a valid stop mode.'
    }
    $stopMode = $stopModeMatch.Groups[1].Value
    if ($stopMode -eq 'Forced') {
        Write-Warning 'USBPcap required Stop-Process -Force; buffered capture data may have been lost.'
    }

    Remove-Item -LiteralPath $resolvedSessionPath -Force
    [pscustomobject]@{
        ProcessId = $ownership.ProcessId
        OutputPath = $ownership.OutputPath
        StopMode = $stopMode
    }
}
finally {
    $ownership.Process.Dispose()
}
