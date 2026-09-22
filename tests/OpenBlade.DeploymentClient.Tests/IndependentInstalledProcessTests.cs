using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using Microsoft.Win32.SafeHandles;

namespace OpenBlade.DeploymentClient.Tests;

public sealed class IndependentInstalledProcessTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Launch_OutlivesLauncherJob_WithoutInheritingHandles(bool hidden)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        string root = Path.Combine(Path.GetTempPath(), $"OpenBlade.LaunchTest.{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        using EventWaitHandle inheritedEvent = new(false, EventResetMode.ManualReset);
        // An unnamed inheritable event is passed through the coordinator. The child
        // must not retain it, even though it is available to ordinary CreateProcess.
        using SafeFileHandle job = LaunchFixture.CreateKillJob(allowBreakaway: true);
        using Process coordinator = LaunchFixture.StartCoordinator(job, root, inheritedEvent, hidden);
        Process? child = null;
        try
        {
            LaunchReport report = await ReadReportAsync(root, coordinator);
            Assert.True(report.Error is null, report.WorkingDirectory);
            child = Process.GetProcessById(report.ProcessId);
            _ = child.Handle;
            Assert.False(child.HasExited);
            job.Dispose();
            Assert.True(coordinator.WaitForExit(5000));
            await Task.Delay(250);
            Assert.False(child.HasExited, "The launched child died when its launcher's job closed.");
            Assert.False(report.InJob);
            Assert.False(report.InheritedHandle);
            Assert.False(inheritedEvent.WaitOne(0));
            using Process current = Process.GetCurrentProcess();
            Assert.Equal(current.SessionId, report.SessionId);
            Assert.Equal(new WindowsClientIntegritySource().IsElevated(), report.Elevated);
            Assert.Equal(LaunchFixture.GetIdentity(), report.Identity);
            Assert.Equal(root, report.WorkingDirectory);
        }
        finally
        {
            File.WriteAllText(Path.Combine(root, "exit"), "exit");
            job.Dispose();
            coordinator.WaitForExit(5000);
            if (child is not null)
            {
                if (!child.WaitForExit(5000))
                {
                    child.Kill();
                    Assert.True(child.WaitForExit(5000));
                }

                child.Dispose();
            }

            // Every child also has an independent 15-second lifetime bound.
        }
    }

    [Fact]
    public async Task Launch_RefusesJobThatDisallowsBreakaway()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        string root = Path.Combine(Path.GetTempPath(), $"OpenBlade.LaunchTest.{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        using EventWaitHandle inheritedEvent = new(false, EventResetMode.ManualReset);
        using SafeFileHandle job = LaunchFixture.CreateKillJob(allowBreakaway: false);
        using Process coordinator = LaunchFixture.StartCoordinator(job, root, inheritedEvent, hidden: true,
            requireNoOuterJob: false);
        try
        {
            LaunchReport report = await ReadReportAsync(root, coordinator);
            Assert.Equal("Win32Exception", report.Error);
            Assert.Equal(5, report.NativeError);
            Assert.Equal(0, report.ProcessId);
            Assert.False(File.Exists(Path.Combine(root, "child.started")));
        }
        finally
        {
            job.Dispose();
            Assert.True(coordinator.WaitForExit(5000));
        }
    }

    [Fact]
    public async Task Launch_RefusesResidualOuterJobBeforeExecutingChild()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        string root = Path.Combine(Path.GetTempPath(), $"OpenBlade.LaunchTest.{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        using EventWaitHandle inheritedEvent = new(false, EventResetMode.ManualReset);
        using SafeFileHandle outerJob = LaunchFixture.CreateKillJob(allowBreakaway: false);
        using SafeFileHandle innerJob = LaunchFixture.CreateKillJob(allowBreakaway: true);
        using Process coordinator = LaunchFixture.StartCoordinator(innerJob, root, inheritedEvent, hidden: true,
            requireNoOuterJob: false, outerJob: outerJob);
        try
        {
            LaunchReport report = await ReadReportAsync(root, coordinator);
            Assert.Equal(nameof(InvalidOperationException), report.Error);
            Assert.Contains("remains confined to a launcher job", report.WorkingDirectory);
            Assert.Equal(0, report.ProcessId);
            Assert.False(File.Exists(Path.Combine(root, "child.started")));
        }
        finally
        {
            innerJob.Dispose();
            Assert.True(coordinator.WaitForExit(5000));
        }
    }

    private static async Task<LaunchReport> ReadReportAsync(string root, Process coordinator)
    {
        string path = Path.Combine(root, "report.json");
        long started = Stopwatch.GetTimestamp();
        while (!File.Exists(path) && Stopwatch.GetElapsedTime(started) < TimeSpan.FromSeconds(10))
        {
            if (coordinator.HasExited)
            {
                Assert.True(File.Exists(path), $"Fixture exited before reporting: {coordinator.ExitCode}");
                break;
            }

            await Task.Delay(25);
        }

        Assert.True(File.Exists(path), "Fixture did not report within ten seconds.");
        return JsonSerializer.Deserialize<LaunchReport>(File.ReadAllText(path))!;
    }
}

internal sealed record LaunchReport(int ProcessId, bool InJob, bool InheritedHandle,
    int SessionId, bool Elevated, string WorkingDirectory, string? Error = null, int NativeError = 0,
    LaunchIdentity? Identity = null);

internal sealed record LaunchIdentity(string UserSid, string IntegritySid);

// The test assembly's apphost is a bounded harmless fixture. Test discovery still
// uses its DLL normally; no installed application or existing process is started.
internal static class LaunchFixture
{
    private const string RootVariable = "OPENBLADE_TEST_LAUNCH_ROOT";
    private const string HandleVariable = "OPENBLADE_TEST_LAUNCH_HANDLE";

    private static int Main(string[] args)
    {
        if (args.Length == 4 && args[0] == "launch-fixture")
        {
            string root = args[1];
            Environment.SetEnvironmentVariable(RootVariable, root);
            Environment.SetEnvironmentVariable(HandleVariable, args[2]);
            bool hidden = bool.Parse(args[3]);
            try
            {
                IntPtr inheritedHandle = new(long.Parse(args[2]));
                if (!GetHandleInformation(inheritedHandle, out uint handleFlags) || (handleFlags & 1) == 0)
                {
                    throw new InvalidOperationException("The coordinator did not receive the inheritable event fixture.");
                }

                using Process child = IndependentInstalledProcess.Start(new InstalledProcessLaunchPlan(
                    Environment.ProcessPath!, root, hidden,
                    hidden ? ProcessWindowStyle.Hidden : ProcessWindowStyle.Normal));
                Thread.Sleep(15000);
            }
            catch (Win32Exception exception)
            {
                WriteReport(root, new(0, false, false, 0, false, root,
                    nameof(Win32Exception), exception.NativeErrorCode));
            }
            catch (Exception exception)
            {
                WriteReport(root, new(0, false, false, 0, false, exception.ToString(), exception.GetType().Name));
            }

            return 0;
        }

        string? childRoot = Environment.GetEnvironmentVariable(RootVariable);
        if (childRoot is null)
        {
            return 2;
        }

        File.WriteAllText(Path.Combine(childRoot, "child.started"), "started");
        bool inherited = SetEvent(new IntPtr(long.Parse(Environment.GetEnvironmentVariable(HandleVariable)!)));
        if (!IsProcessInJob(GetCurrentProcess(), IntPtr.Zero, out bool inJob))
        {
            return 3;
        }

        using Process self = Process.GetCurrentProcess();
        WriteReport(childRoot, new(Environment.ProcessId, inJob, inherited, self.SessionId,
            new WindowsClientIntegritySource().IsElevated(), Environment.CurrentDirectory,
            Identity: GetIdentity()));
        long started = Stopwatch.GetTimestamp();
        while (!File.Exists(Path.Combine(childRoot, "exit"))
            && Stopwatch.GetElapsedTime(started) < TimeSpan.FromSeconds(15))
        {
            Thread.Sleep(25);
        }

        return 0;
    }

    private static void WriteReport(string root, LaunchReport report)
    {
        string temporary = Path.Combine(root, "report.tmp");
        File.WriteAllText(temporary, JsonSerializer.Serialize(report));
        File.Move(temporary, Path.Combine(root, "report.json"));
    }

    internal static LaunchIdentity GetIdentity()
    {
        using WindowsIdentity identity = WindowsIdentity.GetCurrent(TokenAccessLevels.Query);
        GetTokenInformation(identity.AccessToken, 25, IntPtr.Zero, 0, out int length);
        IntPtr buffer = Marshal.AllocHGlobal(length);
        try
        {
            if (!GetTokenInformation(identity.AccessToken, 25, buffer, length, out _))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error());
            }

            return new(identity.User!.Value, new SecurityIdentifier(Marshal.ReadIntPtr(buffer)).Value);
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    internal static SafeFileHandle CreateKillJob(bool allowBreakaway)
    {
        SafeFileHandle job = CreateJobObjectW(IntPtr.Zero, null);
        Assert.False(job.IsInvalid);
        JobExtendedLimits limits = new();
        limits.Basic.LimitFlags = 0x2000u | (allowBreakaway ? 0x800u : 0u);
        Assert.True(SetInformationJobObject(job, 9, ref limits, Marshal.SizeOf<JobExtendedLimits>()));
        return job;
    }

    internal static Process StartCoordinator(SafeFileHandle job, string root, EventWaitHandle inheritedEvent,
        bool hidden, bool requireNoOuterJob = true, SafeFileHandle? outerJob = null)
    {
        Assert.True(SetHandleInformation(inheritedEvent.SafeWaitHandle, 1, 1));
        string executable = Path.ChangeExtension(typeof(LaunchFixture).Assembly.Location, ".exe");
        StartupInfo startup = new() { Size = Marshal.SizeOf<StartupInfo>() };
        StringBuilder command = new($"\"{executable}\" launch-fixture \"{root}\" {inheritedEvent.SafeWaitHandle.DangerousGetHandle().ToInt64()} {hidden}");
        if (!CreateProcessW(executable, command, IntPtr.Zero, IntPtr.Zero, true,
                0x01000000 | 0x08000000 | 4, IntPtr.Zero, root, ref startup, out ProcessInformation info))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error());
        }

        using SafeFileHandle processHandle = new(info.Process, true);
        using SafeFileHandle thread = new(info.Thread, true);
        try
        {
            Assert.True(IsProcessInJob(info.Process, IntPtr.Zero, out bool inheritedOuterJob));
            if (requireNoOuterJob)
            {
                Assert.False(inheritedOuterJob, "The test coordinator could not leave the test host's outer job.");
            }

            if (outerJob is not null)
            {
                Assert.True(AssignProcessToJobObject(outerJob, processHandle));
            }

            Assert.True(AssignProcessToJobObject(job, processHandle));
            Process process = Process.GetProcessById(info.ProcessId);
            _ = process.Handle;
            Assert.NotEqual(uint.MaxValue, ResumeThread(thread));
            return process;
        }
        catch
        {
            TerminateProcess(processHandle, 1);
            throw;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JobBasicLimits
    {
        internal long ProcessTime, JobTime;
        internal uint LimitFlags;
        internal UIntPtr MinimumWorkingSet, MaximumWorkingSet;
        internal uint ActiveProcesses;
        internal UIntPtr Affinity;
        internal uint Priority, SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JobExtendedLimits
    {
        internal JobBasicLimits Basic;
        internal ulong ReadOperations, WriteOperations, OtherOperations, ReadBytes, WriteBytes, OtherBytes;
        internal UIntPtr ProcessMemory, JobMemory, PeakProcessMemory, PeakJobMemory;
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
    private static extern SafeFileHandle CreateJobObjectW(IntPtr attributes, string? name);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetInformationJobObject(SafeFileHandle job, int informationClass, ref JobExtendedLimits information, int size);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AssignProcessToJobObject(SafeFileHandle job, SafeFileHandle process);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateProcessW(string applicationName, StringBuilder commandLine, IntPtr processAttributes,
        IntPtr threadAttributes, [MarshalAs(UnmanagedType.Bool)] bool inheritHandles, uint flags,
        IntPtr environment, string directory, ref StartupInfo startup, out ProcessInformation information);
    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern uint ResumeThread(SafeFileHandle thread);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool TerminateProcess(SafeFileHandle process, uint exitCode);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool SetHandleInformation(SafeWaitHandle handle, uint mask, uint flags);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetHandleInformation(IntPtr handle, out uint flags);
    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetTokenInformation(SafeAccessTokenHandle token, int informationClass,
        IntPtr information, int informationLength, out int returnLength);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetEvent(IntPtr handle);
    [DllImport("kernel32.dll")]
    private static extern IntPtr GetCurrentProcess();
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsProcessInJob(IntPtr process, IntPtr job, [MarshalAs(UnmanagedType.Bool)] out bool inJob);
}
