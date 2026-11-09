using System.Diagnostics;
using OpenBlade.DeploymentBroker;

namespace OpenBlade.DeploymentBroker.Tests;

public sealed class WindowsBrokerInstallerProcessRunnerTests
{
    private static readonly TimeSpan FixtureWatchdog = TimeSpan.FromMinutes(1);

    [Fact]
    public async Task RunAsync_BindingFailureKeepsMonitoringUntilTheChildExits()
    {
        string releaseEventName = "Local\\OpenBlade.BrokerTest." + Guid.NewGuid().ToString("N");
        using EventWaitHandle release = new(false, EventResetMode.ManualReset, releaseEventName);
        using Process child = await StartReadyChildAsync(releaseEventName, release);
        WindowsBrokerInstallerProcessRunner runner = new(
            _ => Process.GetProcessById(child.Id), TimeSpan.FromSeconds(30));
        IOException bindingFailure = new("Injected PID persistence failure.");
        Task<BrokerInstallerExecutionResult> execution = runner.RunAsync(
            CreateSignaledChildPlan(releaseEventName), _ => throw bindingFailure, CancellationToken.None);

        Exception? assertionFailure = null;
        try
        {
            Assert.False(execution.IsCompleted);
            Assert.False(child.HasExited);
            release.Set();
            BrokerInstallerExecutionResult result = await execution.WaitAsync(FixtureWatchdog);
            Assert.True(result.ExitObserved);
            Assert.Equal(17, result.ExitCode);
            Assert.False(result.TimedOut);
            Assert.Same(bindingFailure, result.ProcessBindingFailure);
        }
        catch (Exception exception)
        {
            assertionFailure = exception;
            throw;
        }
        finally
        {
            await CleanupFixtureAsync(child, release, execution, assertionFailure);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RunAsync_BindingFailureRetainsBoundedWaitWithoutKillingTheChild(bool cancel)
    {
        string releaseEventName = "Local\\OpenBlade.BrokerTest." + Guid.NewGuid().ToString("N");
        using EventWaitHandle release = new(false, EventResetMode.ManualReset, releaseEventName);
        using Process child = await StartReadyChildAsync(releaseEventName, release);
        using CancellationTokenSource cancellation = new();
        WindowsBrokerInstallerProcessRunner runner = new(
            _ => Process.GetProcessById(child.Id),
            cancel ? TimeSpan.FromSeconds(30) : TimeSpan.FromMilliseconds(100));
        IOException bindingFailure = new("Injected PID persistence failure.");
        Task<BrokerInstallerExecutionResult> execution = runner.RunAsync(
            CreateSignaledChildPlan(releaseEventName),
            _ => throw bindingFailure,
            cancellation.Token);
        Exception? assertionFailure = null;
        try
        {
            if (cancel)
            {
                cancellation.Cancel();
            }

            BrokerInstallerExecutionResult result = await execution.WaitAsync(FixtureWatchdog);
            Assert.False(result.ExitObserved);
            Assert.Null(result.ExitCode);
            Assert.Equal(!cancel, result.TimedOut);
            Assert.Same(bindingFailure, result.ProcessBindingFailure);
            Assert.False(child.HasExited);
        }
        catch (Exception exception)
        {
            assertionFailure = exception;
            throw;
        }
        finally
        {
            await CleanupFixtureAsync(child, release, execution, assertionFailure);
        }
    }

    private static async Task<Process> StartReadyChildAsync(string releaseEventName, EventWaitHandle release)
    {
        // Cold PowerShell startup on a loaded CI runner is not installer-monitor
        // behavior. Establish the child's waiting phase before starting the tested
        // deadline, and retain a wrapper independent of the runner's disposal.
        Process child = Process.Start(CreateSignaledChildPlan(releaseEventName))
            ?? throw new InvalidOperationException("The fixture child did not start.");
        try
        {
            Assert.Equal("ready", await child.StandardOutput.ReadLineAsync().WaitAsync(FixtureWatchdog));
            return child;
        }
        catch (Exception exception)
        {
            try { await CleanupFixtureAsync(child, release, Task.CompletedTask, exception); }
            finally { child.Dispose(); }
            throw;
        }
    }

    private static async Task CleanupFixtureAsync(
        Process child, EventWaitHandle release, Task execution, Exception? primaryFailure)
    {
        List<Exception> cleanupFailures = [];
        release.Set();
        try { await JoinFixtureChildAsync(child); }
        catch (Exception exception) { cleanupFailures.Add(exception); }
        // A failed child join must not skip observation of the monitor task.
        try { await execution.WaitAsync(FixtureWatchdog); }
        catch (Exception exception) { cleanupFailures.Add(exception); }
        if (cleanupFailures.Count > 0)
        {
            if (primaryFailure is not null) { cleanupFailures.Insert(0, primaryFailure); }
            throw new AggregateException("Process fixture cleanup failed.", cleanupFailures);
        }
    }

    private static async Task JoinFixtureChildAsync(Process child)
    {
        try
        {
            await child.WaitForExitAsync().WaitAsync(FixtureWatchdog);
        }
        catch (TimeoutException)
        {
            // Only this harmless test-owned child may be killed on fixture failure.
            // The assertions above require the production runner to leave it alive.
            if (!child.HasExited) { child.Kill(entireProcessTree: true); }
            await child.WaitForExitAsync().WaitAsync(FixtureWatchdog);
            throw;
        }
    }

    private static ProcessStartInfo CreateSignaledChildPlan(string releaseEventName)
    {
        ProcessStartInfo plan = new(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.System),
            "WindowsPowerShell", "v1.0", "powershell.exe"))
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            // Child errors reach the test host rather than an unread pipe.
            RedirectStandardError = false,
        };
        plan.ArgumentList.Add("-NoProfile");
        plan.ArgumentList.Add("-NonInteractive");
        plan.ArgumentList.Add("-Command");
        plan.ArgumentList.Add(
            "$event = [System.Threading.EventWaitHandle]::OpenExisting('"
            + releaseEventName + "'); [Console]::WriteLine('ready'); $null = $event.WaitOne(); $event.Dispose(); exit 17");
        return plan;
    }
}
