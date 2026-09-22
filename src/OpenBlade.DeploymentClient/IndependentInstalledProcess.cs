using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace OpenBlade.DeploymentClient;

internal static class IndependentInstalledProcess
{
    private const uint CreateSuspended = 0x00000004;
    private const uint CreateBreakawayFromJob = 0x01000000;
    private const uint CreateNoWindow = 0x08000000;
    private const uint StartUseShowWindow = 0x00000001;
    private const uint WaitObjectSignaled = 0;

    // Called only after WindowsDeploymentProcessLauncher admits the two fixed
    // installed paths. No arguments, shell, alternate token, or elevation path.
    internal static Process Start(InstalledProcessLaunchPlan plan)
    {
        StartupInfo startup = new()
        {
            Size = Marshal.SizeOf<StartupInfo>(),
            Flags = StartUseShowWindow,
            ShowWindow = plan.WindowStyle == ProcessWindowStyle.Hidden ? (ushort)0 : (ushort)1,
        };
        uint flags = CreateBreakawayFromJob | CreateSuspended;
        if (plan.CreateNoWindow)
        {
            flags |= CreateNoWindow;
        }

        // lpApplicationName selects the exact admitted executable. The quoted
        // command line supplies argv[0] only; no caller-supplied arguments exist.
        StringBuilder commandLine = new($"\"{plan.FileName}\"");
        if (!CreateProcessW(plan.FileName, commandLine, IntPtr.Zero, IntPtr.Zero,
                false, flags, IntPtr.Zero, plan.WorkingDirectory, ref startup, out ProcessInformation info))
        {
            // A parent job that forbids breakaway must fail without a fallback.
            throw new Win32Exception(Marshal.GetLastWin32Error());
        }

        using SafeProcessHandle processHandle = new(info.Process, ownsHandle: true);
        using SafeFileHandle threadHandle = new(info.Thread, ownsHandle: true);
        Process? process = null;
        bool resumed = false;
        try
        {
            if (!IsProcessInJob(processHandle, IntPtr.Zero, out bool inJob))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error());
            }

            // Breakaway can leave an outer nested job in place. Never execute
            // application code unless independence from every job is confirmed.
            if (inJob)
            {
                throw new InvalidOperationException("The installed process remains confined to a launcher job.");
            }

            process = Process.GetProcessById(info.ProcessId);
            _ = process.Handle;
            uint suspendCount = ResumeThread(threadHandle);
            if (suspendCount == uint.MaxValue)
            {
                throw new Win32Exception(Marshal.GetLastWin32Error());
            }

            if (suspendCount != 1)
            {
                throw new InvalidOperationException("The installed process did not resume from its initial suspension.");
            }

            resumed = true;
            return process;
        }
        finally
        {
            if (!resumed)
            {
                process?.Dispose();
                // Only the handle returned by this creation is eligible for
                // cleanup. Never search for, or terminate, another process.
                if (WaitForSingleObject(processHandle, 0) != WaitObjectSignaled)
                {
                    if (!TerminateProcess(processHandle, 1))
                    {
                        throw new Win32Exception(Marshal.GetLastWin32Error());
                    }

                    if (WaitForSingleObject(processHandle, 5000) != WaitObjectSignaled)
                    {
                        throw new InvalidOperationException("The rejected installed process did not terminate.");
                    }
                }
            }
        }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct StartupInfo
    {
        internal int Size;
        internal string? Reserved, Desktop, Title;
        internal uint X, Y, XSize, YSize, XCountChars, YCountChars, FillAttribute, Flags;
        internal ushort ShowWindow, ReservedSize;
        internal IntPtr ReservedData, StandardInput, StandardOutput, StandardError;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessInformation
    {
        internal IntPtr Process, Thread;
        internal int ProcessId, ThreadId;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateProcessW(string applicationName, StringBuilder commandLine,
        IntPtr processAttributes, IntPtr threadAttributes, [MarshalAs(UnmanagedType.Bool)] bool inheritHandles,
        uint creationFlags, IntPtr environment, string currentDirectory, ref StartupInfo startupInfo,
        out ProcessInformation processInformation);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsProcessInJob(SafeProcessHandle process, IntPtr job,
        [MarshalAs(UnmanagedType.Bool)] out bool inJob);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint ResumeThread(SafeFileHandle thread);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool TerminateProcess(SafeProcessHandle process, uint exitCode);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint WaitForSingleObject(SafeProcessHandle handle, uint milliseconds);
}
