#if OPENBLADE_CANDIDATE_RECEIPT_FIXTURE
using System.ComponentModel;
using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;
using OpenBlade.Deployment;

namespace OpenBlade.DeploymentBroker;

// This is an offline fixture launch path, never a product runner. The Windows
// mapping policy is set before the loader runs, not inferred from a later ACL.
internal sealed class ScratchNativeReceiverLaunch : IDisposable
{
    internal const string ReadinessName = "openblade-scratch-candidate-receipt.exe";
    internal const ulong Mitigations = (1UL << 32) | (1UL << 44) | (1UL << 60);
    private readonly SafeProcessHandle original;
    private readonly AnonymousPipeServerStream input;
    private readonly AnonymousPipeServerStream output;
    private readonly AnonymousPipeServerStream error;
    internal Process Process { get; }
    internal StreamWriter StandardInput { get; }
    internal StreamReader StandardOutput { get; }
    internal StreamReader StandardError { get; }

    private ScratchNativeReceiverLaunch(SafeProcessHandle original, Process process,
        AnonymousPipeServerStream input, AnonymousPipeServerStream output, AnonymousPipeServerStream error)
    {
        this.original = original;
        Process = process;
        this.input = input;
        this.output = output;
        this.error = error;
        StandardInput = new StreamWriter(input);
        StandardOutput = new StreamReader(output);
        StandardError = new StreamReader(error);
    }

    internal static ScratchNativeReceiverLaunch Start(string image, ScratchReceiverClosure retained)
    {
        if (!string.Equals(Path.GetFullPath(image), retained.ImagePath, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("The scratch receiver differs from its fixed launch image.");
        }
        return StartCore(image, () => retained.RequireUnchanged());
    }

    internal static ScratchNativeReceiverLaunch StartReadiness(WindowsCandidateReceiptStorage storage, FileStream retained)
    {
        retained.Position = 0;
        byte[] expected = System.Security.Cryptography.SHA256.HashData(retained);
        void RequireImage()
        {
            retained.Position = 0;
            if (!System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(expected,
                System.Security.Cryptography.SHA256.HashData(retained)))
            {
                throw new InvalidDataException("The retained scratch readiness image changed.");
            }
        }
        return StartCore(Path.Combine(storage.Root, "receiver", ReadinessName), RequireImage);
    }

    private static ScratchNativeReceiverLaunch StartCore(string image, Action requireImage)
    {
        requireImage();
        AnonymousPipeServerStream? input = null, output = null, error = null;
        SafeProcessHandle? original = null;
        Process? process = null;
        nint thread = 0, attributes = 0, policy = 0, handles = 0, environment = 0;
        bool initialized = false;
        try
        {
            input = new AnonymousPipeServerStream(PipeDirection.Out, HandleInheritability.Inheritable);
            output = new AnonymousPipeServerStream(PipeDirection.In, HandleInheritability.Inheritable);
            error = new AnonymousPipeServerStream(PipeDirection.In, HandleInheritability.Inheritable);
            nuint bytes = 0;
            _ = InitializeProcThreadAttributeList(0, 2, 0, ref bytes);
            if (bytes == 0 || bytes > 65536) { throw new Win32Exception(); }
            attributes = Marshal.AllocHGlobal(checked((int)bytes));
            Check(InitializeProcThreadAttributeList(attributes, 2, 0, ref bytes));
            initialized = true;
            policy = Marshal.AllocHGlobal(sizeof(ulong));
            Marshal.WriteInt64(policy, unchecked((long)Mitigations));
            Check(UpdateProcThreadAttribute(attributes, 0, 0x20007, policy, sizeof(ulong), 0, 0));
            handles = Marshal.AllocHGlobal(3 * nint.Size);
            Marshal.WriteIntPtr(handles, 0, input.ClientSafePipeHandle.DangerousGetHandle());
            Marshal.WriteIntPtr(handles, nint.Size, output.ClientSafePipeHandle.DangerousGetHandle());
            Marshal.WriteIntPtr(handles, 2 * nint.Size, error.ClientSafePipeHandle.DangerousGetHandle());
            Check(UpdateProcThreadAttribute(attributes, 0, 0x20002, handles, (nuint)(3 * nint.Size), 0, 0));
            string directory = Path.GetDirectoryName(Path.GetFullPath(image))!;
            environment = Marshal.StringToHGlobalUni(CreateEnvironment(directory));
            var startup = new StartupInfoEx
            {
                Startup = new StartupInfo
                {
                    Size = Marshal.SizeOf<StartupInfoEx>(),
                    Flags = 0x100,
                    Input = input.ClientSafePipeHandle.DangerousGetHandle(),
                    Output = output.ClientSafePipeHandle.DangerousGetHandle(),
                    Error = error.ClientSafePipeHandle.DangerousGetHandle(),
                },
                Attributes = attributes,
            };
            // CREATE_SUSPENDED | CREATE_UNICODE_ENVIRONMENT |
            // EXTENDED_STARTUPINFO_PRESENT | CREATE_NO_WINDOW.
            Check(CreateProcessW(Path.GetFullPath(image), new StringBuilder("\"" + Path.GetFullPath(image) + "\""),
                0, 0, true, 0x08080404, environment, directory, ref startup, out var created));
            original = new SafeProcessHandle(created.Process, ownsHandle: true);
            thread = created.Thread;
            process = Process.GetProcessById(checked((int)created.ProcessId));
            // Authenticate the original returned object before the first loader
            // instruction. The path is stable under the retained directory and
            // executable leases; compare the kernel creation generations too.
            if (!GetProcessTimes(original, out long originalTime, out _, out _, out _)
                || !GetProcessTimes(process.SafeHandle, out long observedTime, out _, out _, out _)
                || originalTime != observedTime)
            {
                throw new InvalidDataException("The created scratch receiver process generation differs.");
            }
            ScratchCandidateLaunch.RequireLiveImage(process, Path.GetFullPath(image));
            RequirePolicies(original);
            input.DisposeLocalCopyOfClientHandle();
            output.DisposeLocalCopyOfClientHandle();
            error.DisposeLocalCopyOfClientHandle();
            requireImage();
            if (ResumeThread(thread) != 1) { throw new Win32Exception(); }
            return new ScratchNativeReceiverLaunch(original, process, input, output, error);
        }
        catch (Exception failure)
        {
            Exception? cleanupFailure = null;
            try { if (original is not null && !original.IsInvalid) { Stop(original); } }
            catch (Exception stopError) { cleanupFailure = stopError; }
            try
            {
                ScratchReceiverClosure.DisposeRetainedResources(new IDisposable?[]
                    { process, original, input, output, error }.OfType<IDisposable>());
            }
            catch (Exception disposeError)
            {
                cleanupFailure = cleanupFailure is null ? disposeError : new AggregateException(cleanupFailure, disposeError);
            }
            if (cleanupFailure is not null) { throw new AggregateException(failure, cleanupFailure); }
            throw;
        }
        finally
        {
            if (thread != 0) { _ = CloseHandle(thread); }
            if (initialized) { DeleteProcThreadAttributeList(attributes); }
            Marshal.FreeHGlobal(attributes); Marshal.FreeHGlobal(policy);
            Marshal.FreeHGlobal(handles); Marshal.FreeHGlobal(environment);
        }
    }

    // Explicit child block, never inherited launcher environment. NativeAOT
    // has no CLR startup/profiler inputs; external images remain OS-policy bound.
    internal static string CreateEnvironment(string directory) =>
        "LOCALAPPDATA=" + Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData) + '\0'
        + "SystemRoot=" + Environment.GetFolderPath(Environment.SpecialFolder.Windows) + '\0'
        + "TEMP=" + directory + '\0' + "TMP=" + directory + "\0\0";

    internal static bool AcceptPolicies(uint signature, uint extensions, uint imageLoad) =>
        signature == 1 && extensions == 1 && imageLoad == 4;

    internal static void RequirePolicies(SafeProcessHandle process)
    {
        Check(GetProcessMitigationPolicy(process, 8, out uint signature, sizeof(uint)));
        Check(GetProcessMitigationPolicy(process, 6, out uint extensions, sizeof(uint)));
        Check(GetProcessMitigationPolicy(process, 10, out uint imageLoad, sizeof(uint)));
        if (!AcceptPolicies(signature, extensions, imageLoad))
        {
            throw new InvalidDataException("The native scratch receiver lacks its required creation policies.");
        }
    }

    public void Dispose()
    {
        try
        {
            if (!Process.WaitForExit(1000))
            {
                Stop(original);
            }
        }
        finally
        {
            ScratchReceiverClosure.DisposeRetainedResources([StandardInput, StandardOutput, StandardError,
                input, output, error, Process, original]);
        }
    }

    private static void Stop(SafeProcessHandle process) => Stop(
        timeout => WaitForSingleObject(process, timeout),
        () => TerminateProcess(process, 3) ? null : Marshal.GetLastPInvokeError());

    // Exercise this exact bounded sequence with in-memory OS outcomes. A child
    // can exit between observation and TerminateProcess; only the original
    // handle's signaled readback can turn that failure into success.
    internal static void Stop(Func<uint, uint> wait, Func<int?> terminate)
    {
        uint initial = wait(0);
        if (initial == 0) { return; }
        if (initial != 258) { throw new IOException("The scratch child state could not be observed."); }
        if (terminate() is { } error)
        {
            if (wait(0) == 0) { return; }
            throw new Win32Exception(error);
        }
        if (wait(5000) != 0)
        {
            throw new IOException("The scratch child termination could not be confirmed.");
        }
    }

    private static void Check(bool success) { if (!success) { throw new Win32Exception(); } }
    [StructLayout(LayoutKind.Sequential)]
    private struct StartupInfo
    {
        internal int Size; internal nint Reserved, Desktop, Title;
        internal int X, Y, XSize, YSize, XCount, YCount, Fill, Flags;
        internal short Show, ReservedBytes; internal nint ReservedData, Input, Output, Error;
    }
    [StructLayout(LayoutKind.Sequential)] private struct StartupInfoEx { internal StartupInfo Startup; internal nint Attributes; }
    [StructLayout(LayoutKind.Sequential)] private struct ProcessInfo { internal nint Process, Thread; internal uint ProcessId, ThreadId; }
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool InitializeProcThreadAttributeList(nint list, int count, int flags, ref nuint size);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UpdateProcThreadAttribute(nint list, uint flags, nuint attribute, nint value, nuint size, nint previous, nint returned);
    [DllImport("kernel32.dll")] private static extern void DeleteProcThreadAttributeList(nint list);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateProcessW(string image, StringBuilder command, nint processSecurity, nint threadSecurity,
        [MarshalAs(UnmanagedType.Bool)] bool inherit, uint flags, nint environment, string directory, ref StartupInfoEx startup, out ProcessInfo process);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern uint ResumeThread(nint thread);
    [DllImport("kernel32.dll")][return: MarshalAs(UnmanagedType.Bool)] private static extern bool CloseHandle(nint handle);
    [DllImport("kernel32.dll", SetLastError = true)][return: MarshalAs(UnmanagedType.Bool)] private static extern bool TerminateProcess(SafeProcessHandle process, uint exit);
    [DllImport("kernel32.dll")] private static extern uint WaitForSingleObject(SafeProcessHandle process, uint milliseconds);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetProcessMitigationPolicy(SafeProcessHandle process, int policy, out uint value, nuint size);
    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetProcessTimes(SafeProcessHandle process, out long created, out long exited, out long kernel, out long user);
}
#endif
