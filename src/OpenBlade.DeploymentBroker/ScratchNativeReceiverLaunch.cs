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
    private readonly SafeFileHandle job;
    private readonly ScratchLaunchData dataLocation;
    private readonly ScratchChildChannels channels;
    private bool disposed;
    internal Process Process { get; }
    internal StreamWriter StandardInput { get; }
    internal StreamReader StandardOutput { get; }
    internal StreamReader StandardError { get; }

    private ScratchNativeReceiverLaunch(SafeProcessHandle original, Process process,
        ScratchChildChannels channels,
        SafeFileHandle job, ScratchLaunchData dataLocation)
    {
        this.job = job;
        this.dataLocation = dataLocation;
        this.original = original;
        Process = process;
        this.channels = channels;
        StandardInput = new StreamWriter(channels.Input);
        StandardOutput = new StreamReader(channels.Output);
        StandardError = new StreamReader(channels.Error);
    }

    internal static ScratchNativeReceiverLaunch Start(string image, ScratchReceiverClosure retained)
    {
        if (!string.Equals(Path.GetFullPath(image), retained.ImagePath, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("The scratch receiver differs from its fixed launch image.");
        }
        return StartCore(image, retained.Storage, () => retained.RequireUnchanged());
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
        return StartCore(Path.Combine(storage.Root, "receiver", ReadinessName), storage, RequireImage);
    }

    private static ScratchNativeReceiverLaunch StartCore(string image, WindowsCandidateReceiptStorage storage, Action requireImage)
    {
        requireImage();
        ScratchChildChannels? channels = null;
        SafeProcessHandle? original = null;
        Process? process = null;
        SafeFileHandle? job = null;
        ScratchLaunchData? dataLocation = null;
        nint thread = 0, attributes = 0, policy = 0, environment = 0, jobs = 0;
        uint childId = 0;
        bool initialized = false;
        try
        {
            string directory = Path.GetDirectoryName(Path.GetFullPath(image))!;
            dataLocation = ScratchLaunchData.Create(storage.Root, directory, path => storage.PinDirectory(path));
            channels = new ScratchChildChannels();
            nuint bytes = 0;
            _ = InitializeProcThreadAttributeList(0, 2, 0, ref bytes);
            if (bytes == 0 || bytes > 65536) { throw new Win32Exception(); }
            attributes = Marshal.AllocHGlobal(checked((int)bytes));
            Check(InitializeProcThreadAttributeList(attributes, 2, 0, ref bytes));
            initialized = true;
            policy = Marshal.AllocHGlobal(sizeof(ulong));
            Marshal.WriteInt64(policy, unchecked((long)Mitigations));
            Check(UpdateProcThreadAttribute(attributes, 0, 0x20007, policy, sizeof(ulong), 0, 0));
            environment = Marshal.StringToHGlobalUni(CreateEnvironment(directory, dataLocation.Path));
            var startup = new StartupInfoEx
            {
                Startup = new StartupInfo
                {
                    Size = Marshal.SizeOf<StartupInfoEx>(),
                },
                Attributes = attributes,
            };
            // SDK 26100: JOB_LIST = ProcThreadAttributeValue(13, false, true, false).
            // Post-creation AssignProcessToJobObject would leave a parent-death gap.
            AdmitSuspendedChild(
                configureJob: () =>
                {
                    job = CreateJobObjectW(0, null); // Private, unnamed, noninheritable.
                    if (job.IsInvalid) { throw new Win32Exception(); }
                    var limits = new JobExtendedLimits { Basic = new JobBasicLimits { LimitFlags = 0x2000 } };
                    Check(SetInformationJobObject(job, 9, ref limits, (uint)Marshal.SizeOf<JobExtendedLimits>()));
                },
                bindJob: () =>
                {
                    jobs = Marshal.AllocHGlobal(nint.Size);
                    Marshal.WriteIntPtr(jobs, job!.DangerousGetHandle());
                    Check(UpdateProcThreadAttribute(attributes, 0, 0x2000D, jobs, (nuint)nint.Size, 0, 0));
                },
                create: () =>
                {
                    // CREATE_SUSPENDED | CREATE_UNICODE_ENVIRONMENT |
                    // EXTENDED_STARTUPINFO_PRESENT | CREATE_NO_WINDOW. No fallback.
                    Check(CreateProcessW(Path.GetFullPath(image), new StringBuilder("\"" + Path.GetFullPath(image) + "\" " + channels.Arguments),
                        0, 0, false, 0x08080404, environment, directory, ref startup, out var created));
                    original = new SafeProcessHandle(created.Process, ownsHandle: true);
                    thread = created.Thread;
                    childId = created.ProcessId;
                },
                authenticate: () =>
                {
                    process = Process.GetProcessById(checked((int)childId));
                    if (!GetProcessTimes(original!, out long originalTime, out _, out _, out _)
                        || !GetProcessTimes(process.SafeHandle, out long observedTime, out _, out _, out _)
                        || originalTime != observedTime)
                    {
                        throw new InvalidDataException("The created scratch receiver process generation differs.");
                    }
                    ScratchCandidateLaunch.RequireLiveImage(process, Path.GetFullPath(image));
                    RequirePolicies(original!);
                    requireImage();
                },
                resume: () => { if (ResumeThread(thread) != 1) { throw new Win32Exception(); } },
                connect: () => channels.Connect(childId, () =>
                {
                    if (WaitForSingleObject(original!, 0) != 258)
                    {
                        throw new InvalidDataException("The created scratch channel peer is not live.");
                    }
                }));
            return new ScratchNativeReceiverLaunch(original!, process!, channels, job!, dataLocation);
        }
        catch (Exception failure)
        {
            CleanupLaunch(failure,
                () => { if (original is not null && !original.IsInvalid) { Stop(original); } }, null,
                () => ScratchReceiverClosure.DisposeRetainedResources(new IDisposable?[]
                    { channels, process, original, job }.OfType<IDisposable>()), dataLocation);
            throw;
        }
        finally
        {
            if (thread != 0) { _ = CloseHandle(thread); }
            if (initialized) { DeleteProcThreadAttributeList(attributes); }
            Marshal.FreeHGlobal(attributes); Marshal.FreeHGlobal(policy);
            Marshal.FreeHGlobal(environment); Marshal.FreeHGlobal(jobs);
        }
    }

    // Explicit child block, never inherited launcher environment. NativeAOT
    // has no CLR startup/profiler inputs; external images remain OS-policy bound.
    internal static string CreateEnvironment(string codeDirectory, string dataDirectory)
    {
        string code = Path.GetFullPath(codeDirectory).TrimEnd(Path.DirectorySeparatorChar);
        string data = Path.GetFullPath(dataDirectory).TrimEnd(Path.DirectorySeparatorChar);
        if (data.IndexOfAny(['\0', '=']) >= 0
            || data.Equals(code, StringComparison.OrdinalIgnoreCase)
            || data.StartsWith(code + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("Scratch child data must be outside its retained code closure.");
        }
        return "LOCALAPPDATA=" + Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData) + '\0'
            + "SystemRoot=" + Environment.GetFolderPath(Environment.SpecialFolder.Windows) + '\0'
            + "TEMP=" + data + '\0' + "TMP=" + data + "\0\0";
    }

    // Exercise this exact ordering with injected offline lifecycle outcomes.
    internal static void AdmitSuspendedChild(Action configureJob, Action bindJob, Action create,
        Action authenticate, Action resume, Action connect)
    {
        configureJob();
        bindJob();
        create();
        authenticate();
        resume();
        connect();
    }

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

    public void Dispose() => Dispose(null);

    internal void Dispose(Action? afterStop)
    {
        if (disposed) { return; }
        disposed = true;
        CleanupLaunch(null, () => Stop(original), afterStop,
            () => ScratchReceiverClosure.DisposeRetainedResources([StandardInput, StandardOutput, StandardError,
                channels, Process, original, job]), dataLocation);
    }

    // Shared by partial launch failure and normal disposal. Job close is never
    // taken as proof of retirement; only original-handle confirmation admits data cleanup.
    internal static void CleanupLaunch(Exception? primary, Action confirmRetirement, Action? afterStop,
        Action releaseResources, ScratchLaunchData? data)
    {
        bool confirmed = false;
        ScratchFixtureFailure.Cleanup(primary,
            () => { confirmRetirement(); confirmed = true; },
            () => afterStop?.Invoke(), releaseResources, () => data?.Finish(confirmed));
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

    [StructLayout(LayoutKind.Sequential)]
    private struct JobBasicLimits
    {
        internal long ProcessTime, JobTime;
        internal uint LimitFlags;
        internal nuint MinimumWorkingSet, MaximumWorkingSet;
        internal uint ActiveProcessLimit;
        internal nuint Affinity;
        internal uint PriorityClass, SchedulingClass;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct JobExtendedLimits
    {
        internal JobBasicLimits Basic;
        internal ulong ReadOperations, WriteOperations, OtherOperations, ReadBytes, WriteBytes, OtherBytes;
        internal nuint ProcessMemoryLimit, JobMemoryLimit, PeakProcessMemory, PeakJobMemory;
    }
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    private static extern SafeFileHandle CreateJobObjectW(nint security, string? name);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetInformationJobObject(SafeFileHandle job, int informationClass,
        ref JobExtendedLimits information, uint size);
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
