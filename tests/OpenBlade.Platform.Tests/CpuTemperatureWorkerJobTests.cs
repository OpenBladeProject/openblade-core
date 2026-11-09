using System.Diagnostics;
using OpenBlade.Platform;

namespace OpenBlade.Platform.Tests;

public sealed class CpuTemperatureWorkerJobTests
{
    [Fact]
    public async Task ClosingTheOwnerJobTerminatesTheAssignedProcess()
    {
        if (!OperatingSystem.IsWindows()) { return; }

        string systemRoot = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        string executable = Path.Combine(systemRoot, "System32", "WindowsPowerShell", "v1.0", "powershell.exe");
        using var process = Process.Start(new ProcessStartInfo
        {
            FileName = executable,
            Arguments = "-NoLogo -NoProfile -NonInteractive -Command \"Start-Sleep -Seconds 30\"",
            UseShellExecute = false,
            CreateNoWindow = true,
        }) ?? throw new InvalidOperationException("Could not start the harmless lifetime-test process.");
        using var job = new CpuTemperatureWorkerJob();
        var retainedProcessHandle = process.SafeHandle;
        try
        {
            job.Assign(retainedProcessHandle);
            Assert.False(process.HasExited);

            job.Dispose();
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
            Assert.True(process.HasExited);
        }
        finally
        {
            if (!process.HasExited)
            {
                process.Kill();
                await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
            }
        }
    }
}
