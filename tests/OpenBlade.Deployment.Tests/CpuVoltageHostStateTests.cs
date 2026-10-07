using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32.SafeHandles;

namespace OpenBlade.Deployment.Tests;

[SupportedOSPlatform("windows")]
public sealed class CpuVoltageHostStateTests
{
    [Fact]
    public async Task RetainedExitedExactLifetimeIsTerminalWithoutLiveImageQuery()
    {
        string image = Path.Combine(Environment.SystemDirectory, "cmd.exe");
        using Process child = Process.Start(new ProcessStartInfo(image)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden,
            ArgumentList = { "/d", "/c", "exit", "0" },
        }) ?? throw new IOException("The inert process fixture did not start.");
        // Keep the kernel object alive exactly as the deployment broker does.
        SafeProcessHandle retained = child.SafeHandle;
        Assert.True(GetProcessTimes(retained, out long created, out _, out _, out _));
        using Process caller = Process.GetCurrentProcess();
        var enrollment = Enrollment(child.Id, created, image, caller.SessionId);
        await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(0, child.ExitCode);
        Assert.Equal(0u, WaitForSingleObject(retained, 0));
        Assert.True(GetProcessTimes(retained, out long afterExit, out _, out _, out _));
        Assert.Equal(created, afterExit);

        Assert.False(CpuVoltageHostState.IsExactProcessRunning(enrollment));
        Assert.False(retained.IsClosed);
    }

    [Fact]
    public void LiveHostStillRequiresExactImageSessionAndLifetime()
    {
        using Process current = Process.GetCurrentProcess();
        Assert.True(GetProcessTimes(current.SafeHandle, out long created, out _, out _, out _));
        var enrollment = Enrollment(current.Id, created, Environment.ProcessPath!, current.SessionId);

        Assert.True(CpuVoltageHostState.IsExactProcessRunning(enrollment));
        Assert.Throws<IOException>(() => CpuVoltageHostState.IsExactProcessRunning(
            enrollment with { HostImage = Path.Combine(Environment.SystemDirectory, "not-this-host.exe") }));
        Assert.Throws<IOException>(() => CpuVoltageHostState.IsExactProcessRunning(
            enrollment with { WindowsSession = current.SessionId + 1 }));
        Assert.False(CpuVoltageHostState.IsExactProcessRunning(enrollment with { HostCreated = created + 1 }));
    }

    private static CpuVoltageHostEnrollment Enrollment(int pid, long created, string image, int session) =>
        new(Guid.NewGuid(), "unused", session, pid, created, image, new string('a', 40), new string('b', 64), "unused");

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetProcessTimes(SafeProcessHandle process, out long created, out long exit, out long kernel, out long user);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint WaitForSingleObject(SafeProcessHandle process, uint milliseconds);
}
