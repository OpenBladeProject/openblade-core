using System;
using System.Collections;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace OpenBlade.Testing
{
    // Test infrastructure only. Never switch the input desktop or fall back to it.
    public sealed class WindowsVisualTestDesktop : IDisposable
    {
        private IntPtr desktop;

        public WindowsVisualTestDesktop()
        {
            Name = "OpenBlade-visual-" + Guid.NewGuid().ToString("N");
            int error = 0;
            // CreateDesktop associates the calling thread with the new desktop.
            // Keep the test runner/PowerShell thread on its original desktop.
            var creator = new Thread(() =>
            {
                // Desktop rights excluding DESKTOP_SWITCHDESKTOP.
                desktop = CreateDesktopW(Name, IntPtr.Zero, IntPtr.Zero, 0, 0x00FF, IntPtr.Zero);
                error = Marshal.GetLastWin32Error();
            });
            creator.Start();
            creator.Join();
            if (desktop == IntPtr.Zero)
            {
                throw new Win32Exception(error, "Cannot create an isolated visual-test desktop.");
            }
        }

        public string Name { get; private set; }

        // Call on a fresh UI thread before creating any windows or hooks.
        public void AttachCurrentThread()
        {
            ThrowIfDisposed();
            if (!SetThreadDesktop(desktop))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(),
                    "Cannot attach the visual-test thread to its isolated desktop.");
            }
        }

        public Process Start(ProcessStartInfo startInfo)
        {
            ThrowIfDisposed();
            if (startInfo.UseShellExecute || startInfo.RedirectStandardInput
                || startInfo.RedirectStandardOutput || startInfo.RedirectStandardError)
            {
                throw new ArgumentException("Isolated renderers require direct launch without redirected streams.", "startInfo");
            }
            // Preserve per-process localization and evidence settings.
            var variables = new SortedList(StringComparer.OrdinalIgnoreCase);
            foreach (DictionaryEntry entry in startInfo.EnvironmentVariables)
            {
                variables.Add(entry.Key, entry.Value);
            }
            var environment = new StringBuilder();
            foreach (DictionaryEntry entry in variables)
            {
                environment.Append(entry.Key).Append('=').Append(entry.Value).Append('\0');
            }
            environment.Append('\0');
            IntPtr block = Marshal.StringToHGlobalUni(environment.ToString());
            ProcessInformation information = new ProcessInformation();
            try
            {
                var startup = new StartupInfo();
                startup.Size = Marshal.SizeOf(typeof(StartupInfo));
                startup.Desktop = Name;
                var command = new StringBuilder("\"" + startInfo.FileName + "\" " + startInfo.Arguments);
                // CREATE_NO_WINDOW | CREATE_UNICODE_ENVIRONMENT | CREATE_SUSPENDED.
                // Obtain our Process handle before even a short-lived child can exit.
                if (!CreateProcessW(startInfo.FileName, command, IntPtr.Zero, IntPtr.Zero,
                    false, 0x08000404, block, string.IsNullOrEmpty(startInfo.WorkingDirectory) ? Environment.CurrentDirectory : startInfo.WorkingDirectory, ref startup, out information))
                {
                    throw new Win32Exception(Marshal.GetLastWin32Error(),
                        "Cannot launch the renderer on its isolated desktop.");
                }
                Process process = Process.GetProcessById(information.ProcessId);
                try
                {
                    IntPtr ownedHandle = process.Handle;
                    if (ResumeThread(information.Thread) == uint.MaxValue)
                    {
                        throw new Win32Exception(Marshal.GetLastWin32Error(), "Cannot resume the isolated renderer.");
                    }
                    return process;
                }
                catch
                {
                    process.Dispose();
                    throw;
                }
            }
            catch
            {
                if (information.Process != IntPtr.Zero)
                {
                    TerminateProcess(information.Process, 1);
                    WaitForSingleObject(information.Process, 5000);
                }
                throw;
            }
            finally
            {
                Marshal.FreeHGlobal(block);
                if (information.Thread != IntPtr.Zero) { CloseHandle(information.Thread); }
                if (information.Process != IntPtr.Zero) { CloseHandle(information.Process); }
            }
        }

        public void Dispose()
        {
            if (desktop != IntPtr.Zero)
            {
                if (!CloseDesktop(desktop))
                {
                    throw new Win32Exception(Marshal.GetLastWin32Error(),
                        "Cannot close the isolated visual-test desktop; stop its UI threads and processes first.");
                }
                desktop = IntPtr.Zero;
            }
        }

        private void ThrowIfDisposed()
        {
            if (desktop == IntPtr.Zero) { throw new ObjectDisposedException("WindowsVisualTestDesktop"); }
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct StartupInfo
        {
            public int Size;
            public string Reserved, Desktop, Title;
            public int X, Y, XSize, YSize, XCountChars, YCountChars, FillAttribute, Flags;
            public short ShowWindow, ReservedSize;
            public IntPtr ReservedPointer, StandardInput, StandardOutput, StandardError;
        }
        [StructLayout(LayoutKind.Sequential)]
        private struct ProcessInformation
        {
            public IntPtr Process, Thread;
            public int ProcessId, ThreadId;
        }
        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr CreateDesktopW(string name, IntPtr device, IntPtr mode, uint flags, uint access, IntPtr security);
        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool SetThreadDesktop(IntPtr desktop);
        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool CloseDesktop(IntPtr desktop);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool CreateProcessW(string application, StringBuilder command,
            IntPtr processSecurity, IntPtr threadSecurity, [MarshalAs(UnmanagedType.Bool)] bool inheritHandles,
            uint flags, IntPtr environment, string directory, ref StartupInfo startup, out ProcessInformation information);
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern uint ResumeThread(IntPtr thread);
        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool TerminateProcess(IntPtr process, uint exitCode);
        [DllImport("kernel32.dll")]
        private static extern uint WaitForSingleObject(IntPtr handle, uint milliseconds);
        [DllImport("kernel32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool CloseHandle(IntPtr handle);
    }
}
