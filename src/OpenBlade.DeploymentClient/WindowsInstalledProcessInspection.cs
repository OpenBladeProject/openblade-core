using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Principal;
using Microsoft.Win32.SafeHandles;

namespace OpenBlade.DeploymentClient;

internal static class WindowsInstalledProcessInspection
{
    private const int MaximumCommandLineBytes = 128 * 1024;

    internal static InstalledProcessSnapshot Capture(InstalledPayloadPaths paths)
    {
        List<Process> retained = [];
        List<InstalledProcessObservation> observations = [];
        bool complete = true;
        string[] names = [Path.GetFileNameWithoutExtension(paths.SessionAgentExecutable),
            Path.GetFileNameWithoutExtension(paths.TrayExecutable)];
        try
        {
            foreach (string name in names)
            {
                retained.AddRange(Process.GetProcessesByName(name));
            }
            foreach (Process process in retained)
            {
                try
                {
                    // Keep the process object/handle alive until both inventories
                    // agree, so PID reuse cannot substitute a different process.
                    _ = process.SafeHandle;
                    string actualPath = process.MainModule?.FileName
                        ?? throw new InvalidOperationException("Process image unavailable.");
                    if (!string.Equals(actualPath, paths.SessionAgentExecutable, StringComparison.OrdinalIgnoreCase)
                        && !string.Equals(actualPath, paths.TrayExecutable, StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }
                    observations.Add(Read(process, actualPath));
                }
                catch (Exception exception) when (IsInspectionFailure(exception))
                {
                    complete = false;
                }
            }
            foreach (string name in names)
            {
                Process[] after = Process.GetProcessesByName(name);
                try
                {
                    complete &= retained.Where(process => string.Equals(process.ProcessName, name,
                        StringComparison.OrdinalIgnoreCase)).Select(process => process.Id).Order()
                        .SequenceEqual(after.Select(process => process.Id).Order());
                }
                finally
                {
                    foreach (Process process in after)
                    {
                        process.Dispose();
                    }
                }
            }
            complete &= retained.All(process => !process.HasExited);
        }
        catch (Exception exception) when (IsInspectionFailure(exception))
        {
            complete = false;
        }
        finally
        {
            foreach (Process process in retained)
            {
                process.Dispose();
            }
        }
        return new InstalledProcessSnapshot(complete, observations);
    }

    internal static InstalledProcessObservation Read(Process process, string actualPath)
    {
        SafeProcessHandle handle = process.SafeHandle;
        int status = NtQueryBasicInformation(handle, 0, out ProcessBasicInformation basic,
            Marshal.SizeOf<ProcessBasicInformation>(), out _);
        if (status < 0 || basic.ProcessId != (nint)process.Id
            || basic.ParentProcessId <= 0 || basic.ParentProcessId > int.MaxValue)
        {
            throw new InvalidOperationException("Process ancestry unavailable.");
        }
        if (!OpenProcessToken(handle, 0x0008, out SafeAccessTokenHandle token))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error());
        }
        using (token)
        using (var identity = new WindowsIdentity(token.DangerousGetHandle()))
        {
            bool normal = ReadTokenInt(token, 20) == 0 && ReadIntegrity(token) == 0x2000;
            string[] arguments = ReadArguments(handle);
            if (process.HasExited)
            {
                throw new InvalidOperationException("Process exited during inspection.");
            }

            return new InstalledProcessObservation(process.Id, checked((int)basic.ParentProcessId),
                process.StartTime.ToUniversalTime().Ticks, actualPath, process.SessionId,
                identity.User?.Value, normal, arguments);
        }
    }

    private static int ReadTokenInt(SafeAccessTokenHandle token, int informationClass)
    {
        if (!GetTokenInformation(token, informationClass, out int value, sizeof(int), out _))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error());
        }

        return value;
    }

    private static int ReadIntegrity(SafeAccessTokenHandle token)
    {
        GetTokenInformationBuffer(token, 25, 0, 0, out int size);
        if (size <= 0 || size > 65536)
        {
            throw new InvalidOperationException("Token size unavailable.");
        }

        nint buffer = Marshal.AllocHGlobal(size);
        try
        {
            if (!GetTokenInformationBuffer(token, 25, buffer, size, out _))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error());
            }

            var sid = new SecurityIdentifier(Marshal.ReadIntPtr(buffer));
            return int.Parse(sid.Value.AsSpan(sid.Value.LastIndexOf('-') + 1),
                System.Globalization.CultureInfo.InvariantCulture);
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }

    private static string[] ReadArguments(SafeProcessHandle process)
    {
        // ProcessCommandLineInformation returns a self-contained UNICODE_STRING,
        // avoiding remote PEB reads and cross-bitness pointer assumptions.
        NtQueryInformationProcess(process, 60, 0, 0, out int size);
        if (size < Marshal.SizeOf<UnicodeString>() || size > MaximumCommandLineBytes)
        {
            throw new InvalidOperationException("Command line size unavailable.");
        }

        nint buffer = Marshal.AllocHGlobal(size);
        try
        {
            int status = NtQueryInformationProcess(process, 60, buffer, size, out int returned);
            if (status < 0 || returned > size || returned < Marshal.SizeOf<UnicodeString>())
            {
                throw new InvalidOperationException("Command line changed or unavailable.");
            }

            UnicodeString value = Marshal.PtrToStructure<UnicodeString>(buffer);
            long offset = value.Buffer.ToInt64() - buffer.ToInt64();
            if (value.Length == 0 || value.Length % 2 != 0 || value.MaximumLength % 2 != 0
                || value.Length > value.MaximumLength || offset < Marshal.SizeOf<UnicodeString>()
                || offset > returned || value.MaximumLength > returned - offset)
            {
                throw new InvalidOperationException("Command line buffer is invalid.");
            }

            string commandLine = Marshal.PtrToStringUni(value.Buffer, value.Length / 2)!;
            if (commandLine.Contains('\0'))
            {
                throw new InvalidOperationException("Command line is invalid.");
            }

            nint argv = CommandLineToArgvW(commandLine, out int count);
            if (argv == 0)
            {
                throw new Win32Exception(Marshal.GetLastWin32Error());
            }

            try
            {
                if (count is < 1 or > 16)
                {
                    throw new InvalidOperationException("Command line is not an installed role.");
                }

                return Enumerable.Range(0, count).Select(index =>
                    Marshal.PtrToStringUni(Marshal.ReadIntPtr(argv, index * IntPtr.Size))!).ToArray();
            }
            finally { LocalFree(argv); }
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }

    private static bool IsInspectionFailure(Exception exception) => exception is
        Win32Exception or InvalidOperationException or UnauthorizedAccessException
        or ArgumentException or NotSupportedException;

    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessBasicInformation
    {
        internal nint Reserved1, PebBaseAddress, Reserved2, Reserved3, ProcessId, ParentProcessId;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct UnicodeString
    {
        internal ushort Length, MaximumLength;
        internal nint Buffer;
    }

    [DllImport("ntdll.dll", EntryPoint = "NtQueryInformationProcess")]
    private static extern int NtQueryBasicInformation(SafeProcessHandle process, int informationClass,
        out ProcessBasicInformation information, int length, out int returned);
    [DllImport("ntdll.dll")]
    private static extern int NtQueryInformationProcess(SafeProcessHandle process, int informationClass,
        nint information, int length, out int returned);
    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool OpenProcessToken(SafeProcessHandle process, uint access, out SafeAccessTokenHandle token);
    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetTokenInformation(SafeAccessTokenHandle token, int informationClass,
        out int information, int length, out int returned);
    [DllImport("advapi32.dll", EntryPoint = "GetTokenInformation", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetTokenInformationBuffer(SafeAccessTokenHandle token, int informationClass,
        nint information, int length, out int returned);
    [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern nint CommandLineToArgvW(string commandLine, out int count);
    [DllImport("kernel32.dll")]
    private static extern nint LocalFree(nint memory);
}
