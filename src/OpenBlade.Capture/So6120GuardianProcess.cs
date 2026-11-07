using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace OpenBlade.Capture;

internal static class So6120GuardianProcess
{
    private const uint CreateNewConsole = 0x00000010;
    private const uint ExtendedStartupInfoPresent = 0x00080000;
    private const uint CreateBreakawayFromJob = 0x01000000;
    private const uint StartfUseShowWindow = 0x00000001;
    private const uint HandleFlagInherit = 0x00000001;
    private const uint FileTypePipe = 3;
    private const int ErrorInsufficientBuffer = 122;
    private const uint WaitObject0 = 0;
    private const uint WaitTimeout = 258;
    private const uint WaitFailed = 0xFFFFFFFF;
    private const nuint ProcThreadAttributeHandleList = 0x00020002;

    internal static So6120GuardianChild Start(string executable, IReadOnlyList<string> arguments,
        SafePipeHandle inheritedControlReadHandle, bool showConsole, So6120GuardianCallerLifetime callerLifetime)
    {
        if (!Path.IsPathFullyQualified(executable))
        {
            throw new ArgumentException("The guardian executable must have a fully qualified path.", nameof(executable));
        }

        var commandLine = new StringBuilder();
        AppendArgument(commandLine, executable);
        foreach (string argument in arguments)
        {
            commandLine.Append(' ');
            AppendArgument(commandLine, argument);
        }

        bool handleReference = false;
        bool jobReference = false;
        bool callerReference = false;
        nint handleList = 0;
        nint attributeList = 0;
        bool attributesInitialized = false;
        try
        {
            inheritedControlReadHandle.DangerousAddRef(ref handleReference);
            nint controlHandle = inheritedControlReadHandle.DangerousGetHandle();
            VerifyReadablePipe(controlHandle);
            if (!GetHandleInformation(controlHandle, out uint handleFlags))
            {
                throw NativeFailure("Inspecting the guardian control handle failed.");
            }

            if ((handleFlags & HandleFlagInherit) == 0)
            {
                throw new ArgumentException("The guardian control handle must be inheritable.", nameof(inheritedControlReadHandle));
            }
            callerLifetime.JobQueryHandle.DangerousAddRef(ref jobReference);
            callerLifetime.CallerQueryHandle.DangerousAddRef(ref callerReference);
            nint jobHandle = callerLifetime.JobQueryHandle.DangerousGetHandle();
            nint callerHandle = callerLifetime.CallerQueryHandle.DangerousGetHandle();
            if (!GetHandleInformation(jobHandle, out uint jobFlags) ||
                !GetHandleInformation(callerHandle, out uint callerFlags))
            {
                throw NativeFailure("Inspecting guardian caller verification handles failed.");
            }
            if ((jobFlags & HandleFlagInherit) == 0 || (callerFlags & HandleFlagInherit) == 0)
            {
                throw new ArgumentException("Guardian caller verification handles must be inheritable.", nameof(callerLifetime));
            }

            nuint size = 0;
            if (InitializeProcThreadAttributeList(0, 1, 0, ref size)
                || Marshal.GetLastWin32Error() != ErrorInsufficientBuffer || size == 0)
            {
                throw NativeFailure("Sizing the guardian startup attributes failed.");
            }

            attributeList = Marshal.AllocHGlobal(checked((nint)size));
            if (!InitializeProcThreadAttributeList(attributeList, 1, 0, ref size))
            {
                throw NativeFailure("Initializing the guardian startup attributes failed.");
            }

            attributesInitialized = true;
            handleList = Marshal.AllocHGlobal(3 * IntPtr.Size);
            Marshal.WriteIntPtr(handleList, controlHandle);
            Marshal.WriteIntPtr(handleList, IntPtr.Size, jobHandle);
            Marshal.WriteIntPtr(handleList, 2 * IntPtr.Size, callerHandle);
            if (!UpdateProcThreadAttribute(attributeList, 0, ProcThreadAttributeHandleList, handleList,
                    (nuint)(3 * IntPtr.Size), 0, 0))
            {
                throw NativeFailure("Restricting guardian handle inheritance failed.");
            }

            var startup = new StartupInfoEx
            {
                StartupInfo = new StartupInfo
                {
                    Size = (uint)Marshal.SizeOf<StartupInfoEx>(),
                    Flags = showConsole ? 0 : StartfUseShowWindow,
                    ShowWindow = 0, // SW_HIDE when requested by a fake process test.
                },
                AttributeList = attributeList,
            };
            const uint flags = ExtendedStartupInfoPresent | CreateNewConsole | CreateBreakawayFromJob;
            if (!CreateProcessW(executable, commandLine, 0, 0, true, flags, 0, null, ref startup,
                    out ProcessInformation process))
            {
                throw NativeFailure("Starting the guardian in a separate console outside the caller job failed.");
            }

            using var thread = new SafeProcessHandle(process.Thread, ownsHandle: true);
            return new(new SafeProcessHandle(process.Process, ownsHandle: true), process.ProcessId);
        }
        finally
        {
            if (attributesInitialized) { DeleteProcThreadAttributeList(attributeList); }
            Marshal.FreeHGlobal(attributeList);
            Marshal.FreeHGlobal(handleList);
            if (callerReference) { callerLifetime.CallerQueryHandle.DangerousRelease(); }
            if (jobReference) { callerLifetime.JobQueryHandle.DangerousRelease(); }
            if (handleReference) { inheritedControlReadHandle.DangerousRelease(); }
        }
    }

    internal static So6120GuardianIsolationEvidence VerifyOwnerIsolation(SafeFileHandle job, SafeProcessHandle caller)
    {
        bool jobReference = false;
        bool callerReference = false;
        try
        {
            job.DangerousAddRef(ref jobReference);
            caller.DangerousAddRef(ref callerReference);
            nint jobHandle = job.DangerousGetHandle();
            nint callerHandle = caller.DangerousGetHandle();
            if (!SetHandleInformation(jobHandle, HandleFlagInherit, 0) ||
                !SetHandleInformation(callerHandle, HandleFlagInherit, 0))
            {
                throw NativeFailure("Disabling guardian verification handle inheritance failed.");
            }

            VerifyOwnerConsoleIsolation();
            uint callerId = GetProcessId(callerHandle);
            if (callerId == 0) { throw NativeFailure("Reading the original guardian caller identity failed."); }
            if (callerId == (uint)Environment.ProcessId)
            {
                throw new InvalidOperationException("The guardian cannot be its own caller.");
            }

            uint flags = So6120GuardianCallerLifetime.ReadJobFlags(jobHandle);
            if ((flags & So6120GuardianCallerLifetime.RequiredJobFlags) != So6120GuardianCallerLifetime.RequiredJobFlags)
            {
                throw new InvalidOperationException("The caller job must permit breakaway and terminate members when its last handle closes.");
            }

            if (!IsProcessInJob(callerHandle, jobHandle, out bool callerInJob) ||
                !IsProcessInJob(GetCurrentProcess(), jobHandle, out bool ownerInJob) ||
                !IsProcessInJob(GetCurrentProcess(), 0, out bool inAmbientJob))
            {
                throw NativeFailure("Checking the guardian caller job isolation failed.");
            }

            // Exit code 259 is a valid terminal process status as well as the
            // STILL_ACTIVE sentinel. Only the process object's signal proves
            // that the retained original caller has terminated.
            uint callerWait = WaitForSingleObject(callerHandle, 0);
            if (callerWait == WaitFailed) { throw NativeFailure("Checking original guardian caller liveness failed."); }
            if (!callerInJob || ownerInJob || callerWait != WaitTimeout)
            {
                throw new InvalidOperationException("The guardian requires a live original caller in its private job and must run outside that job.");
            }

            return new(checked((int)callerId), inAmbientJob);
        }
        finally
        {
            if (callerReference) { caller.DangerousRelease(); }
            if (jobReference) { job.DangerousRelease(); }
        }
    }

    internal static void VerifyOwnerConsoleIsolation()
    {
        if (GetConsoleWindow() == 0)
        {
            throw new InvalidOperationException("The guardian requires its own Windows console.");
        }

        // Check the attachment at admission. Windows can still close the owner
        // console or attach another process to it later.
        uint[] processes = new uint[2];
        uint count = GetConsoleProcessList(processes, (uint)processes.Length);
        if (count == 0) { throw NativeFailure("Checking the guardian console process list failed."); }
        if (count != 1 || processes[0] != (uint)Environment.ProcessId)
        {
            throw new InvalidOperationException("The guardian cannot run in a shared console.");
        }

    }

    internal static void VerifyInheritedControl(SafePipeHandle controlReadHandle)
    {
        bool handleReference = false;
        try
        {
            controlReadHandle.DangerousAddRef(ref handleReference);
            nint controlHandle = controlReadHandle.DangerousGetHandle();
            VerifyReadablePipe(controlHandle);
            if (!SetHandleInformation(controlHandle, HandleFlagInherit, 0))
            {
                throw NativeFailure("Disabling guardian control handle inheritance failed.");
            }
        }
        finally
        {
            if (handleReference) { controlReadHandle.DangerousRelease(); }
        }
    }

    private static void VerifyReadablePipe(nint handle)
    {
        if (GetFileType(handle) != FileTypePipe)
        {
            throw new ArgumentException("The guardian control handle must refer to a pipe.");
        }

        // A pipe's write end has the same file type. Peek requires read access
        // and leaves all control bytes queued for the owner.
        if (!TryGetAvailableControlBytes(handle, out _))
        {
            throw NativeFailure("Checking guardian control pipe read access failed.");
        }
    }

    internal static bool TryGetAvailableControlBytes(SafePipeHandle pipe, out uint available)
    {
        bool handleReference = false;
        try
        {
            pipe.DangerousAddRef(ref handleReference);
            return TryGetAvailableControlBytes(pipe.DangerousGetHandle(), out available);
        }
        finally
        {
            if (handleReference) { pipe.DangerousRelease(); }
        }
    }

    private static bool TryGetAvailableControlBytes(nint pipe, out uint available) =>
        PeekNamedPipe(pipe, 0, 0, 0, out available, 0);

    private static void AppendArgument(StringBuilder commandLine, string argument)
    {
        if (argument.Contains('\0'))
        {
            throw new ArgumentException("A guardian argument cannot contain a null character.", nameof(argument));
        }

        // The Windows CRT halves a run of backslashes before a quote. Preserve
        // literal runs before an embedded quote and before the closing quote.
        commandLine.Append('"');
        int backslashes = 0;
        foreach (char character in argument)
        {
            if (character == '\\')
            {
                backslashes++;
                continue;
            }

            commandLine.Append('\\', character == '"' ? 2 * backslashes + 1 : backslashes);
            commandLine.Append(character);
            backslashes = 0;
        }

        commandLine.Append('\\', 2 * backslashes);
        commandLine.Append('"');
    }

    internal static int? PollExitCode(nint process)
    {
        uint result = WaitForSingleObject(process, 0);
        if (result == WaitTimeout) { return null; }
        if (result == WaitFailed) { throw NativeFailure("Waiting for the guardian process failed."); }
        if (result != WaitObject0)
        {
            throw new InvalidOperationException($"Unexpected guardian process wait result {result}.");
        }

        if (!GetExitCodeProcess(process, out int exitCode))
        {
            throw NativeFailure("Reading the guardian process exit code failed.");
        }

        return exitCode;
    }

    private static Win32Exception NativeFailure(string message) => new(Marshal.GetLastWin32Error(), message);

    [StructLayout(LayoutKind.Sequential)]
    private struct StartupInfo
    {
        internal uint Size;
        internal nint Reserved, Desktop, Title;
        internal uint X, Y, XSize, YSize, XCountChars, YCountChars, FillAttribute, Flags;
        internal ushort ShowWindow, ReservedSize;
        internal nint ReservedBytes, StdInput, StdOutput, StdError;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct StartupInfoEx
    {
        internal StartupInfo StartupInfo;
        internal nint AttributeList;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessInformation
    {
        internal nint Process, Thread;
        internal int ProcessId, ThreadId;
    }

    [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateProcessW(string applicationName, StringBuilder commandLine,
        nint processAttributes, nint threadAttributes, [MarshalAs(UnmanagedType.Bool)] bool inheritHandles,
        uint flags, nint environment, string? currentDirectory, ref StartupInfoEx startupInfo,
        out ProcessInformation processInformation);

    [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool InitializeProcThreadAttributeList(nint list, int count, uint flags, ref nuint size);

    [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UpdateProcThreadAttribute(nint list, uint flags, nuint attribute, nint value,
        nuint size, nint previousValue, nint returnSize);

    [DllImport("kernel32.dll", ExactSpelling = true)]
    private static extern void DeleteProcThreadAttributeList(nint list);

    [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)]
    private static extern uint GetFileType(nint handle);

    [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PeekNamedPipe(nint pipe, nint buffer, uint size, nint bytesRead,
        out uint totalBytesAvailable, nint bytesLeftThisMessage);

    [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetHandleInformation(nint handle, out uint flags);

    [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetHandleInformation(nint handle, uint mask, uint flags);

    [DllImport("kernel32.dll", ExactSpelling = true)]
    private static extern nint GetCurrentProcess();

    [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)]
    private static extern uint GetProcessId(nint process);

    [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsProcessInJob(nint process, nint job, [MarshalAs(UnmanagedType.Bool)] out bool inJob);

    [DllImport("kernel32.dll", ExactSpelling = true)]
    private static extern nint GetConsoleWindow();

    [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)]
    private static extern uint GetConsoleProcessList([Out] uint[] processList, uint processCount);

    [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)]
    private static extern uint WaitForSingleObject(nint handle, uint milliseconds);

    [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetExitCodeProcess(nint process, out int exitCode);
}

internal sealed record So6120GuardianIsolationEvidence(int CallerProcessId, bool InAmbientJob);

internal sealed class So6120GuardianChild(SafeProcessHandle process, int id) : IDisposable
{
    internal int Id { get; } = id;

    internal async Task<int> WaitForExitAsync(CancellationToken token = default)
    {
        // Keep the original process object alive across each asynchronous delay,
        // even if another caller disposes the observation handle during this wait.
        bool handleReference = false;
        try
        {
            process.DangerousAddRef(ref handleReference);
            nint handle = process.DangerousGetHandle();
            while (true)
            {
                token.ThrowIfCancellationRequested();
                if (So6120GuardianProcess.PollExitCode(handle) is int exitCode) { return exitCode; }
                await Task.Delay(50, token).ConfigureAwait(false);
            }
        }
        finally
        {
            if (handleReference) { process.DangerousRelease(); }
        }
    }

    public void Dispose() => process.Dispose();
}
