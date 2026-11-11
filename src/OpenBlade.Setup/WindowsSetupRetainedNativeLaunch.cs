using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace OpenBlade.Setup;

// Fixed SCM-selected native generation only. Retain one original object; never
// reopen by PID during health/commit. Exact code provenance comes from the held
// authenticated package, non-reused namespace and eager native entry together.
internal sealed class WindowsSetupRetainedNativeLaunch : ISetupRetainedCandidateLaunch, ISetupOriginalManagedProcess, IDisposable
{
    private readonly SafeProcessHandle process;
    private bool lost;
    internal SetupCandidateLaunch Launch { get; }
    SetupCandidateLaunch ISetupOriginalManagedProcess.Launch => Launch;
    private WindowsSetupRetainedNativeLaunch(SafeProcessHandle process, SetupCandidateLaunch launch)
    { this.process = process; Launch = launch; }
    internal static WindowsSetupRetainedNativeLaunch Capture(SetupCandidateServiceObservation scm, string selectedPath, string digest)
    {
        if (!scm.Running || scm.ProcessId == 0 || scm.ServiceType != 0x10 || scm.StartType != 2
            || scm.Account != "LocalSystem" || scm.CommandLine != $"\"{selectedPath}\"")
        { throw new IOException("Native startup SCM identity differs."); }
        var process = OpenProcess(0x1000 | 0x100000, false, scm.ProcessId);
        try
        {
            if (process.IsInvalid) { throw new Win32Exception(Marshal.GetLastPInvokeError()); }
            RequireLive(process);
            if (!OpenProcessToken(process, 8, out var token)) { throw new Win32Exception(Marshal.GetLastPInvokeError()); }
            using (token)
            using (var identity = new WindowsIdentity(token.DangerousGetHandle()))
            {
                if (identity.User?.Value != "S-1-5-18" || !GetTokenInformation(token, 8, out int type, 4, out uint count) || count != 4 || type != 1)
                { throw new IOException("Native service requires its actual LocalSystem primary token."); }
            }
            var launch = Read(process, digest);
            if (launch.ProcessId != scm.ProcessId || launch.ExecutablePath != selectedPath) { throw new IOException("Native original process differs from selection."); }
            RequireLive(process);
            return new(process, launch);
        }
        catch { process.Dispose(); throw; }
    }
    public ValueTask<SetupCandidateLaunch?> ReadLiveAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (lost) { return ValueTask.FromResult<SetupCandidateLaunch?>(null); }
        try
        {
            RequireLive(process);
            var current = Read(process, Launch.ExecutableSha256);
            RequireLive(process);
            if (current != Launch) { throw new IOException("Native original process identity changed."); }
            return ValueTask.FromResult<SetupCandidateLaunch?>(current);
        }
        catch { lost = true; throw; }
    }
    private static SetupCandidateLaunch Read(SafeProcessHandle process, string digest)
    {
        var image = new StringBuilder(32768); uint size = (uint)image.Capacity;
        if (!QueryFullProcessImageNameW(process, 0, image, ref size) || !GetProcessTimes(process, out long created, out _, out _, out _))
        { throw new Win32Exception(Marshal.GetLastPInvokeError()); }
        return new(GetProcessId(process), created, image.ToString(), digest);
    }
    private static void RequireLive(SafeProcessHandle process)
    { if (WaitForSingleObject(process, 0) != 258) { throw new IOException("Native original process is exited or unknown."); } }
    public Task WaitForExitAsync(TimeSpan budget, CancellationToken token) =>
        SetupRetainedOriginalExit.WaitAsync(() =>
        {
            uint result = WaitForSingleObject(process, 0);
            return result switch
            {
                0 => true,
                258 => false,
                0xffffffff => throw new Win32Exception(Marshal.GetLastPInvokeError()),
                _ => throw new IOException("Original process wait returned an unknown result."),
            };
        }, budget, token);
    public void Dispose() { lost = true; process.Dispose(); }
    [DllImport("kernel32.dll", SetLastError = true)] private static extern SafeProcessHandle OpenProcess(uint access, [MarshalAs(UnmanagedType.Bool)] bool inherit, uint pid);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern uint WaitForSingleObject(SafeProcessHandle process, uint milliseconds);
    [DllImport("kernel32.dll")] private static extern uint GetProcessId(SafeProcessHandle process);
    [DllImport("kernel32.dll", SetLastError = true)][return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetProcessTimes(SafeProcessHandle process, out long created, out long exit, out long kernel, out long user);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)][return: MarshalAs(UnmanagedType.Bool)] private static extern bool QueryFullProcessImageNameW(SafeProcessHandle process, uint flags, StringBuilder image, ref uint size);
    [DllImport("advapi32.dll", SetLastError = true)][return: MarshalAs(UnmanagedType.Bool)] private static extern bool OpenProcessToken(SafeProcessHandle process, uint access, out SafeAccessTokenHandle token);
    [DllImport("advapi32.dll", SetLastError = true)][return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetTokenInformation(SafeAccessTokenHandle token, int information, out int value, uint size, out uint returned);
}
