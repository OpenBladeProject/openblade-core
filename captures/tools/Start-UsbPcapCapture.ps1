[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [ValidateNotNullOrEmpty()]
    [string]$ExecutablePath,

    [Parameter(Mandatory)]
    [ValidateNotNull()]
    [string[]]$ArgumentList,

    [Parameter(Mandatory)]
    [ValidateNotNullOrEmpty()]
    [string]$OutputPath,

    [Parameter(Mandatory)]
    [ValidateNotNullOrEmpty()]
    [string]$SessionPath
)

$ErrorActionPreference = 'Stop'

if (-not ('OpenBlade.UsbPcapProcessLauncher' -as [type])) {
    Add-Type -TypeDefinition @'
using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;

namespace OpenBlade
{
    public static class UsbPcapProcessLauncher
    {
        private const uint CREATE_NEW_PROCESS_GROUP = 0x00000200;

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct StartupInfo
        {
            public int Size;
            public IntPtr Reserved;
            public IntPtr Desktop;
            public IntPtr Title;
            public uint X;
            public uint Y;
            public uint XSize;
            public uint YSize;
            public uint XCountChars;
            public uint YCountChars;
            public uint FillAttribute;
            public uint Flags;
            public short ShowWindow;
            public short Reserved2;
            public IntPtr Reserved2Pointer;
            public IntPtr StandardInput;
            public IntPtr StandardOutput;
            public IntPtr StandardError;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct ProcessInformation
        {
            public IntPtr Process;
            public IntPtr Thread;
            public uint ProcessId;
            public uint ThreadId;
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool CreateProcessW(
            string applicationName,
            StringBuilder commandLine,
            IntPtr processAttributes,
            IntPtr threadAttributes,
            [MarshalAs(UnmanagedType.Bool)] bool inheritHandles,
            uint creationFlags,
            IntPtr environment,
            string currentDirectory,
            ref StartupInfo startupInfo,
            out ProcessInformation processInformation);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool CloseHandle(IntPtr handle);

        public static uint Start(string executablePath, string[] arguments, string currentDirectory)
        {
            StringBuilder commandLine = new StringBuilder(QuoteArgument(executablePath));
            foreach (string argument in arguments)
            {
                commandLine.Append(' ');
                commandLine.Append(QuoteArgument(argument));
            }

            StartupInfo startupInfo = new StartupInfo
            {
                Size = Marshal.SizeOf<StartupInfo>()
            };

            ProcessInformation processInformation;
            if (!CreateProcessW(
                executablePath,
                commandLine,
                IntPtr.Zero,
                IntPtr.Zero,
                false,
                CREATE_NEW_PROCESS_GROUP,
                IntPtr.Zero,
                currentDirectory,
                ref startupInfo,
                out processInformation))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not start USBPcapCMD.");
            }

            try
            {
                return processInformation.ProcessId;
            }
            finally
            {
                CloseHandle(processInformation.Thread);
                CloseHandle(processInformation.Process);
            }
        }

        private static string QuoteArgument(string argument)
        {
            if (argument.Length > 0 && argument.IndexOfAny(new[] { ' ', '\t', '\n', '\v', '"' }) < 0)
            {
                return argument;
            }

            StringBuilder quoted = new StringBuilder();
            quoted.Append('"');
            int backslashes = 0;
            foreach (char character in argument)
            {
                if (character == '\\')
                {
                    backslashes++;
                    continue;
                }

                if (character == '"')
                {
                    quoted.Append('\\', backslashes * 2 + 1);
                    quoted.Append('"');
                    backslashes = 0;
                    continue;
                }

                quoted.Append('\\', backslashes);
                backslashes = 0;
                quoted.Append(character);
            }

            quoted.Append('\\', backslashes * 2);
            quoted.Append('"');
            return quoted.ToString();
        }
    }
}
'@
}

$resolvedExecutablePath = (Resolve-Path -LiteralPath $ExecutablePath -ErrorAction Stop).Path
if ([IO.Path]::GetFileName($resolvedExecutablePath) -ine 'USBPcapCMD.exe') {
    throw "Refusing to launch a non-USBPcap executable: $resolvedExecutablePath"
}

$resolvedOutputPath = [IO.Path]::GetFullPath($OutputPath)
$resolvedSessionPath = [IO.Path]::GetFullPath($SessionPath)
if (Test-Path -LiteralPath $resolvedOutputPath) {
    throw "Refusing to overwrite existing capture output: $resolvedOutputPath"
}
if (Test-Path -LiteralPath $resolvedSessionPath) {
    throw "Refusing to overwrite existing capture ownership session: $resolvedSessionPath"
}

$outputArguments = @(for ($index = 0; $index -lt $ArgumentList.Count; $index++) {
    if ($ArgumentList[$index] -eq '-o') {
        if ($index + 1 -ge $ArgumentList.Count) {
            throw 'USBPcap -o is missing its output path.'
        }
        $ArgumentList[$index + 1]
    }
})
if ($outputArguments.Count -ne 1 -or
    [IO.Path]::GetFullPath([string]$outputArguments[0]) -ine $resolvedOutputPath) {
    throw 'ArgumentList must contain exactly one -o value matching OutputPath.'
}

[IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($resolvedOutputPath)) | Out-Null
[IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($resolvedSessionPath)) | Out-Null

$sessionWriteProbe = "$resolvedSessionPath.$PID.$([Guid]::NewGuid().ToString('N')).probe"
try {
    [IO.File]::WriteAllText($sessionWriteProbe, '{}', [Text.UTF8Encoding]::new($false))
}
finally {
    if (Test-Path -LiteralPath $sessionWriteProbe) {
        Remove-Item -LiteralPath $sessionWriteProbe -Force
    }
}

$captureProcessId = [OpenBlade.UsbPcapProcessLauncher]::Start(
    $resolvedExecutablePath,
    $ArgumentList,
    [IO.Path]::GetDirectoryName($resolvedExecutablePath))
$process = Get-Process -Id $captureProcessId -ErrorAction Stop

try {
    $session = [ordered]@{
        schemaVersion = 1
        processId = [uint32]$captureProcessId
        processGroupId = [uint32]$captureProcessId
        executablePath = $resolvedExecutablePath
        processStartTimeUtcTicks = $process.StartTime.ToUniversalTime().Ticks
        outputPath = $resolvedOutputPath
    }
    $json = $session | ConvertTo-Json
    $temporarySessionPath = "$resolvedSessionPath.$PID.$([Guid]::NewGuid().ToString('N')).tmp"
    try {
        [IO.File]::WriteAllText($temporarySessionPath, $json, [Text.UTF8Encoding]::new($false))
        [IO.File]::Move($temporarySessionPath, $resolvedSessionPath)
    }
    finally {
        if (Test-Path -LiteralPath $temporarySessionPath) {
            Remove-Item -LiteralPath $temporarySessionPath -Force
        }
    }

    [pscustomobject]@{
        ProcessId = [uint32]$captureProcessId
        ProcessGroupId = [uint32]$captureProcessId
        OutputPath = $resolvedOutputPath
        SessionPath = $resolvedSessionPath
    }
}
finally {
    $process.Dispose()
}
