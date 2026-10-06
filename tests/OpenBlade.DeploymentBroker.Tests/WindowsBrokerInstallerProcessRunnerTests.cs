using System.Diagnostics;
using OpenBlade.DeploymentBroker;

namespace OpenBlade.DeploymentBroker.Tests;

public sealed class WindowsBrokerInstallerProcessRunnerTests
{
    [Fact]
    public async Task RunAsync_BindingFailureKeepsMonitoringUntilTheChildExits()
    {
        Process? child = null;
        WindowsBrokerInstallerProcessRunner runner = new(
            plan => child = Process.Start(plan), TimeSpan.FromSeconds(30));
        IOException bindingFailure = new("Injected PID persistence failure.");
        Task<BrokerInstallerExecutionResult> execution = runner.RunAsync(
            CreateWaitingChildPlan(), _ => throw bindingFailure, CancellationToken.None);

        Assert.NotNull(child);
        try
        {
            Assert.False(execution.IsCompleted);
            Assert.False(child.HasExited);
        }
        finally
        {
            await child.StandardInput.WriteLineAsync("finish");
            await child.StandardInput.FlushAsync();
        }

        BrokerInstallerExecutionResult result = await execution.WaitAsync(TimeSpan.FromSeconds(30));
        Assert.True(result.ExitObserved);
        Assert.Equal(17, result.ExitCode);
        Assert.False(result.TimedOut);
        Assert.Same(bindingFailure, result.ProcessBindingFailure);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RunAsync_BindingFailureRetainsBoundedWaitWithoutKillingTheChild(bool cancel)
    {
        Process? child = null;
        Process? observedChild = null;
        string releaseEventName = "Local\\OpenBlade.BrokerTest." + Guid.NewGuid().ToString("N");
        using EventWaitHandle release = new(false, EventResetMode.ManualReset, releaseEventName);
        using CancellationTokenSource cancellation = new();
        WindowsBrokerInstallerProcessRunner runner = new(
            plan => child = Process.Start(plan),
            cancel ? TimeSpan.FromSeconds(30) : TimeSpan.FromMilliseconds(100));
        IOException bindingFailure = new("Injected PID persistence failure.");
        Task<BrokerInstallerExecutionResult> execution = runner.RunAsync(
            CreateSignaledChildPlan(releaseEventName),
            processId =>
            {
                observedChild = Process.GetProcessById(processId);
                throw bindingFailure;
            },
            cancellation.Token);
        Assert.NotNull(child);
        // The runner disposes its Process wrapper on timeout. Keep a separate
        // observation handle solely to observe and join the harmless child.
        using Process observation = Assert.IsType<Process>(observedChild);
        try
        {
            if (cancel)
            {
                cancellation.Cancel();
            }

            BrokerInstallerExecutionResult result = await execution.WaitAsync(TimeSpan.FromSeconds(30));
            Assert.False(result.ExitObserved);
            Assert.Null(result.ExitCode);
            Assert.Equal(!cancel, result.TimedOut);
            Assert.Same(bindingFailure, result.ProcessBindingFailure);
            Assert.False(observation.HasExited);
        }
        finally
        {
            release.Set();
            await observation.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(30));
        }
    }

    private static ProcessStartInfo CreateWaitingChildPlan()
    {
        ProcessStartInfo plan = new(
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "cmd.exe"))
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
        };
        plan.ArgumentList.Add("/d");
        plan.ArgumentList.Add("/c");
        plan.ArgumentList.Add("set /p reviewFixInput= & exit /b 17");
        return plan;
    }

    private static ProcessStartInfo CreateSignaledChildPlan(string releaseEventName)
    {
        ProcessStartInfo plan = new(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.System),
            "WindowsPowerShell", "v1.0", "powershell.exe"))
        {
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        plan.ArgumentList.Add("-NoProfile");
        plan.ArgumentList.Add("-NonInteractive");
        plan.ArgumentList.Add("-Command");
        plan.ArgumentList.Add(
            "$event = [System.Threading.EventWaitHandle]::OpenExisting('"
            + releaseEventName + "'); $null = $event.WaitOne(30000); $event.Dispose(); exit 17");
        return plan;
    }
}
