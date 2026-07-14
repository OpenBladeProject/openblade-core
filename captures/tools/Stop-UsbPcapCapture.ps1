[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [ValidateRange(1, [int]::MaxValue)]
    [int]$ProcessId
)

$ErrorActionPreference = 'Stop'

if (-not ('OpenSynapse.ConsoleControl' -as [type])) {
    Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;

namespace OpenSynapse
{
    public static class ConsoleControl
    {
        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool FreeConsole();

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool AttachConsole(uint processId);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool SetConsoleCtrlHandler(IntPtr handlerRoutine, bool add);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool GenerateConsoleCtrlEvent(uint controlEvent, uint processGroupId);
    }
}
'@
}

$process = Get-Process -Id $ProcessId -ErrorAction Stop
if ($process.ProcessName -ne 'USBPcapCMD') {
    throw "Refusing to signal non-USBPcap process $ProcessId ($($process.ProcessName))."
}

[void][OpenSynapse.ConsoleControl]::FreeConsole()
if (-not [OpenSynapse.ConsoleControl]::AttachConsole([uint32]$ProcessId)) {
    throw "Could not attach to USBPcap process $ProcessId (Win32 $([Runtime.InteropServices.Marshal]::GetLastWin32Error()))."
}

try {
    [void][OpenSynapse.ConsoleControl]::SetConsoleCtrlHandler([IntPtr]::Zero, $true)
    if (-not [OpenSynapse.ConsoleControl]::GenerateConsoleCtrlEvent(0, 0)) {
        throw "Could not send Ctrl+C to USBPcap process $ProcessId."
    }

    $process.WaitForExit(5000)
    if (-not $process.HasExited) {
        throw "USBPcap process $ProcessId did not stop after Ctrl+C."
    }
}
finally {
    [void][OpenSynapse.ConsoleControl]::FreeConsole()
    $process.Dispose()
}
