using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace OpenBlade.DeploymentClient;

internal static class IndependentInstalledProcess
{
    private const uint CreateSuspended = 0x00000004;
    private const uint ExtendedStartupInfoPresent = 0x00080000;
    private const uint CreateNoWindow = 0x08000000;
    private const uint StartUseShowWindow = 0x00000001;
    private const uint WaitObjectSignaled = 0;

    // Called only after WindowsDesktopLaunchContext admits the two fixed
    // installed paths and the same-user desktop parent. No arguments or shell execution.
    internal static Process Start(InstalledProcessLaunchPlan plan, SafeProcessHandle parent,
        DesktopProcessIdentity identity)
    {
        if (HasExited(parent) || !identity.IsSameNormalIdentity(DesktopProcessIdentity.ReadCaller())
            || !identity.IsSameNormalIdentity(DesktopProcessIdentity.Read(parent)))
        {
            throw new UnauthorizedAccessException("The retained launch parent security context changed.");
        }

        using ParentProcessAttribute attribute = new(parent);
        StartupInfoEx startup = new()
        {
            Startup = new()
            {
                Size = Marshal.SizeOf<StartupInfoEx>(),
                Flags = StartUseShowWindow,
                ShowWindow = plan.WindowStyle == ProcessWindowStyle.Hidden ? (ushort)0 : (ushort)1,
            },
            AttributeList = attribute.List,
        };
        uint flags = ExtendedStartupInfoPresent | CreateSuspended;
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
            throw new Win32Exception(Marshal.GetLastWin32Error());
        }

        using SafeProcessHandle processHandle = new(info.Process, ownsHandle: true);
        using SafeFileHandle threadHandle = new(info.Thread, ownsHandle: true);
        Process? process = null;
        bool resumed = false;
        try
        {
            // The documented parent-process attribute inherits the desktop's
            // token and job context. Verify the actual child before user code runs.
            if (HasExited(parent) || !identity.IsSameNormalIdentity(DesktopProcessIdentity.Read(processHandle)))
            {
                throw new UnauthorizedAccessException("The installed child security context does not match the verified desktop.");
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

    internal static bool HasExited(SafeProcessHandle process)
    {
        uint result = WaitForSingleObject(process, 0);
        if (result == uint.MaxValue)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error());
        }

        return result != 258;
    }

    private sealed class ParentProcessAttribute : IDisposable
    {
        private readonly SafeProcessHandle parent;
        private bool retained;
        private bool initialized;
        private IntPtr list;
        private IntPtr value;

        internal ParentProcessAttribute(SafeProcessHandle parent)
        {
            this.parent = parent;
            try
            {
                parent.DangerousAddRef(ref retained);
                nuint size = 0;
                InitializeProcThreadAttributeList(IntPtr.Zero, 1, 0, ref size);
                if (size == 0)
                {
                    throw new Win32Exception(Marshal.GetLastWin32Error());
                }

                list = Marshal.AllocHGlobal(checked((nint)size));
                if (!InitializeProcThreadAttributeList(list, 1, 0, ref size))
                {
                    throw new Win32Exception(Marshal.GetLastWin32Error());
                }

                initialized = true;
                value = Marshal.AllocHGlobal(IntPtr.Size);
                Marshal.WriteIntPtr(value, parent.DangerousGetHandle());
                if (!UpdateProcThreadAttribute(list, 0, (nuint)0x00020000, value, (nuint)IntPtr.Size,
                        IntPtr.Zero, IntPtr.Zero))
                {
                    throw new Win32Exception(Marshal.GetLastWin32Error());
                }
            }
            catch
            {
                Dispose();
                throw;
            }
        }

        internal IntPtr List => list;

        public void Dispose()
        {
            if (initialized)
            {
                DeleteProcThreadAttributeList(list);
                initialized = false;
            }

            Marshal.FreeHGlobal(list);
            Marshal.FreeHGlobal(value);
            list = IntPtr.Zero;
            value = IntPtr.Zero;
            if (retained)
            {
                parent.DangerousRelease();
                retained = false;
            }
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct StartupInfoEx
    {
        internal StartupInfo Startup;
        internal IntPtr AttributeList;
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
        uint creationFlags, IntPtr environment, string currentDirectory, ref StartupInfoEx startupInfo,
        out ProcessInformation processInformation);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool InitializeProcThreadAttributeList(IntPtr list, int count, uint flags, ref nuint size);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UpdateProcThreadAttribute(IntPtr list, uint flags, nuint attribute,
        IntPtr value, nuint size, IntPtr previousValue, IntPtr returnSize);

    [DllImport("kernel32.dll")]
    private static extern void DeleteProcThreadAttributeList(IntPtr list);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint ResumeThread(SafeFileHandle thread);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool TerminateProcess(SafeProcessHandle process, uint exitCode);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint WaitForSingleObject(SafeProcessHandle handle, uint milliseconds);
}
