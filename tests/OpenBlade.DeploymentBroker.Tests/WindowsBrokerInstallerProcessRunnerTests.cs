using System.Diagnostics;
using System.Runtime.ExceptionServices;
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
        using CancellationTokenSource cancellation = new();
        WindowsBrokerInstallerProcessRunner runner = new(
            plan =>
            {
                child = Process.Start(plan)
                    ?? throw new InvalidOperationException("The test child did not start.");
                // The runner may dispose its observation wrapper. Keep the
                // actual launch wrapper/stdin owned here so that disposal cannot
                // release the child through EOF before the no-kill assertions.
                return Process.GetProcessById(child.Id);
            },
            cancel ? TimeSpan.FromSeconds(30) : TimeSpan.FromMilliseconds(100));
        IOException bindingFailure = new("Injected PID persistence failure.");
        Task<BrokerInstallerExecutionResult> execution = runner.RunAsync(
            CreateWaitingChildPlan(),
            processId =>
            {
                Assert.Equal(child!.Id, processId);
                throw bindingFailure;
            },
            cancellation.Token);
        using Process actualChild = Assert.IsType<Process>(child);
        Exception? original = null;
        List<Exception> cleanupFailures = [];
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
            Assert.False(actualChild.HasExited);
        }
        catch (Exception error) { original = error; }
        finally
        {
            try
            {
                await actualChild.StandardInput.WriteLineAsync("finish");
                await actualChild.StandardInput.FlushAsync();
            }
            catch (Exception error) { cleanupFailures.Add(error); }
            try
            {
                await actualChild.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(30));
                Assert.Equal(17, actualChild.ExitCode);
            }
            catch (Exception error) { cleanupFailures.Add(error); }
        }
        if (original is not null)
        {
            if (cleanupFailures.Count == 0) { ExceptionDispatchInfo.Capture(original).Throw(); }
            cleanupFailures.Insert(0, original);
        }
        if (cleanupFailures.Count != 0) { throw new AggregateException(cleanupFailures); }
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

}
