using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using Microsoft.Win32.SafeHandles;

namespace OpenBlade.DeploymentClient.Tests;

// Hosted Windows CI runs elevated. Only this disposable test apphost is lowered;
// the production admission policy and the machine's security settings stay intact.
internal static class NormalIntegrityFixtureHost
{
    internal static async Task RunAsync(string scenario, bool forceSupervisor = false)
    {
        DesktopProcessIdentity identity = DesktopProcessIdentity.ReadCaller();
        if (identity.IsNormal && !forceSupervisor)
        {
            await RunScenarioAsync(scenario);
            return;
        }

        // The supervisor creates the report directory and all scenario objects
        // itself, after its real primary token has passed normal admission.
        string root = Path.Combine(Path.GetTempPath(), $"OpenBlade.NormalFixture.{Guid.NewGuid():N}");
        using SafeFileHandle job = LaunchFixture.CreateJob(killOnClose: true);
        try
        {
            using Process supervisor = StartSupervisor(scenario, root, identity, job);
            Assert.True(LaunchFixture.IsInJob(supervisor.SafeHandle, job));
            await supervisor.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(60));
            string reportPath = Path.Combine(root, "result.json");
            Assert.True(File.Exists(reportPath), $"Normal fixture supervisor exited {supervisor.ExitCode} without a report.");
            SupervisorReport report = JsonSerializer.Deserialize<SupervisorReport>(File.ReadAllText(reportPath))!;
            Assert.True(report.Error is null, report.Error);
            Assert.Equal(0, supervisor.ExitCode);
        }
        finally
        {
            // Includes descendants if startup, an assertion, or the watchdog fails.
            // This job belongs only to this fixture; the host's jobs are retained.
            LaunchFixture.TerminateAndDrainFixtureJob(job);
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    internal static async Task<int> RunSupervisorAsync(string scenario, string root)
    {
        string? error = null;
        try
        {
            AssertNormalIdentity(DesktopProcessIdentity.ReadCaller(), "running supervisor");
            await RunScenarioAsync(scenario);
        }
        catch (Exception exception)
        {
            error = exception.ToString();
        }

        Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, "result.json"), JsonSerializer.Serialize(new SupervisorReport(error)));
        return error is null ? 0 : 1;
    }

    private static Task RunScenarioAsync(string scenario) => scenario switch
    {
        "hidden" => IndependentInstalledProcessTests.RunLifetimeScenarioAsync(hidden: true),
        "visible" => IndependentInstalledProcessTests.RunLifetimeScenarioAsync(hidden: false),
        "dead-parent" => IndependentInstalledProcessTests.RunDeadParentScenarioAsync(),
        _ => throw new ArgumentException("Unknown native fixture scenario.", nameof(scenario)),
    };

    internal static void AssertNormalIdentity(DesktopProcessIdentity identity, string stage) =>
        Assert.True(identity.IsNormal,
            $"{stage}: integrity={identity.IntegritySid}, elevated={identity.Elevated}, "
            + $"restricted={identity.Restricted}, appContainer={identity.AppContainer}, uiAccess={identity.UiAccess}.");

    private static Process StartSupervisor(string scenario, string root,
        DesktopProcessIdentity identity, SafeFileHandle job)
    {
        using SafeAccessTokenHandle? token = identity.IsNormal ? null : CreateNormalUserToken(identity);
        string executable = Path.ChangeExtension(typeof(LaunchFixture).Assembly.Location, ".exe");
        StartupInfo startup = new() { Size = Marshal.SizeOf<StartupInfo>() };
        StringBuilder command = new($"\"{executable}\" normal-fixture-supervisor {scenario} \"{root}\"");
        // CREATE_NO_WINDOW | CREATE_SUSPENDED. No breakaway, alternate parent,
        // inherited handles, impersonation, shell, or elevation request.
        ProcessInformation info;
        bool created = token is null
            ? CreateProcessW(executable, command, IntPtr.Zero, IntPtr.Zero, false, 0x08000004,
                IntPtr.Zero, Path.GetDirectoryName(executable)!, ref startup, out info)
            : CreateProcessAsUserW(token, executable, command, IntPtr.Zero, IntPtr.Zero, false, 0x08000004,
                IntPtr.Zero, Path.GetDirectoryName(executable)!, ref startup, out info);
        if (!created)
        {
            throw NativeFailure("create normal fixture supervisor");
        }

        using SafeProcessHandle processHandle = new(info.Process, ownsHandle: true);
        using SafeFileHandle thread = new(info.Thread, ownsHandle: true);
        Process? process = null;
        try
        {
            if (!AssignProcessToJobObject(job, processHandle))
            {
                throw NativeFailure("contain normal fixture supervisor");
            }

            DesktopProcessIdentity actual = DesktopProcessIdentity.Read(processHandle);
            AssertNormalIdentity(actual, "suspended supervisor");
            Assert.Equal(identity.UserSid, actual.UserSid);
            Assert.Equal(identity.AuthenticationId, actual.AuthenticationId);
            Assert.Equal(identity.SessionId, actual.SessionId);
            process = Process.GetProcessById(info.ProcessId);
            _ = process.Handle;
            Assert.Equal(1u, ResumeThread(thread));
            return process;
        }
        catch
        {
            process?.Dispose();
            if (!TerminateProcess(processHandle, 1))
            {
                throw NativeFailure("terminate rejected fixture supervisor");
            }

            Assert.Equal(0u, WaitForSingleObject(processHandle, 5000));
            throw;
        }
    }

    private static SafeAccessTokenHandle CreateNormalUserToken(DesktopProcessIdentity identity)
    {
        Assert.True(identity.Elevated && identity.IntegritySid == "S-1-16-12288"
            && !identity.Restricted && !identity.AppContainer && !identity.UiAccess,
            "Only an unrestricted high-integrity testhost may create the reduced fixture token.");
        // SAFER_LEVELID_NORMALUSER removes administrator/power-user rights.
        // It derives from this process's token; no other process token is opened.
        // https://learn.microsoft.com/windows/win32/api/winsafer/nf-winsafer-safercreatelevel
        if (!SaferCreateLevel(1, 0x20000, 1, out IntPtr level, IntPtr.Zero))
        {
            throw NativeFailure("open SAFER normal-user level");
        }

        SafeAccessTokenHandle? token = null;
        try
        {
            if (!SaferComputeTokenFromLevel(level, IntPtr.Zero, out token, 0, IntPtr.Zero))
            {
                throw NativeFailure("compute reduced fixture token");
            }

            // SAFER strips rights; explicitly lower the derived token's mandatory
            // integrity label as well. Actual process admission is checked above.
            SecurityIdentifier medium = new("S-1-16-8192");
            byte[] bytes = new byte[medium.BinaryLength];
            medium.GetBinaryForm(bytes, 0);
            IntPtr sid = Marshal.AllocHGlobal(bytes.Length);
            try
            {
                Marshal.Copy(bytes, 0, sid, bytes.Length);
                SidAndAttributes label = new() { Sid = sid, Attributes = 0x20 };
                if (!SetTokenInformation(token, 25, ref label, Marshal.SizeOf<SidAndAttributes>() + bytes.Length))
                {
                    throw NativeFailure("lower fixture token integrity");
                }
            }
            finally
            {
                Marshal.FreeHGlobal(sid);
            }

            return token;
        }
        catch
        {
            token?.Dispose();
            throw;
        }
        finally
        {
            SaferCloseLevel(level);
        }
    }

    private static Win32Exception NativeFailure(string stage)
    {
        int error = Marshal.GetLastWin32Error();
        return new(error, $"{stage}: {new Win32Exception(error).Message} ({error}).");
    }

    private sealed record SupervisorReport(string? Error);

    [StructLayout(LayoutKind.Sequential)]
    private struct SidAndAttributes
    {
        internal IntPtr Sid;
        internal uint Attributes;
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

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SaferCreateLevel(uint scope, uint level, uint flags, out IntPtr handle, IntPtr reserved);
    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SaferComputeTokenFromLevel(IntPtr level, IntPtr input,
        out SafeAccessTokenHandle token, uint flags, IntPtr reserved);
    [DllImport("advapi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SaferCloseLevel(IntPtr level);
    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetTokenInformation(SafeAccessTokenHandle token, int informationClass,
        ref SidAndAttributes information, int length);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateProcessAsUserW(SafeAccessTokenHandle token, string applicationName,
        StringBuilder commandLine, IntPtr processAttributes, IntPtr threadAttributes,
        [MarshalAs(UnmanagedType.Bool)] bool inheritHandles, uint flags, IntPtr environment, string directory,
        ref StartupInfo startup, out ProcessInformation information);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateProcessW(string applicationName, StringBuilder commandLine,
        IntPtr processAttributes, IntPtr threadAttributes, [MarshalAs(UnmanagedType.Bool)] bool inheritHandles,
        uint flags, IntPtr environment, string directory, ref StartupInfo startup, out ProcessInformation information);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AssignProcessToJobObject(SafeFileHandle job, SafeProcessHandle process);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint ResumeThread(SafeFileHandle thread);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool TerminateProcess(SafeProcessHandle process, uint exitCode);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint WaitForSingleObject(SafeProcessHandle process, uint milliseconds);
}
