using System.Diagnostics;
using OpenBlade.Setup;

namespace OpenBlade.Setup.Tests;

public sealed class ScCommandRunnerTests
{
    [Fact]
    public async Task SuccessfulCommand_DrainsBothStreamsAndPreservesLiteralArguments()
    {
        var process = new FakeProcess { CompleteImmediately = true };
        ProcessStartInfo? observed = null;
        var runner = new ScCommandRunner(info =>
        {
            observed = info;
            return process;
        }, TimeSpan.FromSeconds(30));

        ScProcessResult result = await runner.RunAsync(
            ["config", "OpenBlade", "binPath=", "\"C:\\Program Files\\OpenBlade\\OpenBlade.Service.exe\""],
            CancellationToken.None);

        Assert.Equal(new ScProcessResult(0, "RUNNING", "diagnostic"), result);
        Assert.NotNull(observed);
        Assert.Equal(ScSetupServiceControl.ExecutablePath, observed.FileName);
        Assert.False(observed.UseShellExecute);
        Assert.True(observed.CreateNoWindow);
        Assert.Equal("\"C:\\Program Files\\OpenBlade\\OpenBlade.Service.exe\"", observed.ArgumentList[3]);
        Assert.True(process.BothReadersStartedBeforeWait);
        Assert.Equal(0, process.KillCount);
        Assert.True(process.Disposed);
    }

    [Fact]
    public async Task CommandDeadline_KillsAndReapsOwnedChildBeforeReportingTimeout()
    {
        var process = new FakeProcess();
        var runner = new ScCommandRunner(_ => process, TimeSpan.FromMilliseconds(40));

        TimeoutException failure = await Assert.ThrowsAsync<TimeoutException>(() =>
            runner.RunAsync(["stop", "OpenBlade"], CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5)));

        Assert.Contains("may already have been submitted", failure.Message);
        Assert.Equal(1, process.KillCount);
        Assert.True(process.HasExited);
        Assert.Equal(2, process.WaitCount);
        Assert.True(process.Disposed);
    }

    [Fact]
    public async Task CallerCancellation_KillsOwnedChildAndPreservesCancellationIdentity()
    {
        var process = new FakeProcess();
        using CancellationTokenSource caller = new();
        var runner = new ScCommandRunner(_ => process, TimeSpan.FromSeconds(30));
        Task<ScProcessResult> running = runner.RunAsync(["query", "OpenBlade"], caller.Token);
        await process.WaitStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        caller.Cancel();

        OperationCanceledException failure = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => running);

        Assert.Equal(caller.Token, failure.CancellationToken);
        Assert.Equal(1, process.KillCount);
        Assert.True(process.Disposed);
    }

    [Fact]
    public async Task AlreadyCanceled_DoesNotStartChild()
    {
        using CancellationTokenSource caller = new();
        caller.Cancel();
        var runner = new ScCommandRunner(_ => throw new InvalidOperationException("Must not start."), TimeSpan.FromSeconds(30));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            runner.RunAsync(["query", "OpenBlade"], caller.Token));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PollDeadline_IncludesAnInFlightQueryAndItsCleanup(bool deletion)
    {
        var process = new FakeProcess();
        var runner = new ScCommandRunner(_ => process, TimeSpan.FromSeconds(30));
        var control = new ScSetupServiceControl(runner.RunAsync);
        TimeSpan timeout = TimeSpan.FromMilliseconds(40);

        await Assert.ThrowsAsync<TimeoutException>(() =>
            (deletion
                ? control.WaitForDeletionAsync("OpenBlade", timeout, CancellationToken.None)
                : control.WaitForStateAsync("OpenBlade", SetupServiceState.Stopped, timeout, CancellationToken.None))
            .WaitAsync(TimeSpan.FromSeconds(5)));

        Assert.Equal(1, process.KillCount);
        Assert.True(process.Disposed);
    }

    [Fact]
    public async Task PollCancellation_IsNotReportedAsTimeout()
    {
        var process = new FakeProcess();
        using CancellationTokenSource caller = new();
        var runner = new ScCommandRunner(_ => process, TimeSpan.FromSeconds(30));
        var control = new ScSetupServiceControl(runner.RunAsync);
        Task running = control.WaitForDeletionAsync("OpenBlade", TimeSpan.FromSeconds(30), caller.Token);
        await process.WaitStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        caller.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => running);
        Assert.Equal(1, process.KillCount);
    }

    [Fact]
    public async Task ExitRacesTermination_PreservesTimeoutAfterConfirmingExit()
    {
        var process = new FakeProcess { ExitDuringKill = true };
        var runner = new ScCommandRunner(_ => process, TimeSpan.FromMilliseconds(40));

        await Assert.ThrowsAsync<TimeoutException>(() =>
            runner.RunAsync(["query", "OpenBlade"], CancellationToken.None));

        Assert.True(process.HasExited);
        Assert.Equal(2, process.WaitCount);
        Assert.True(process.Disposed);
    }

    [Fact]
    public async Task WaitFailure_StillTerminatesChildAndObservesReaders()
    {
        var process = new FakeProcess { FailFirstWait = true };
        var runner = new ScCommandRunner(_ => process, TimeSpan.FromSeconds(30));

        InvalidOperationException failure = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            runner.RunAsync(["query", "OpenBlade"], CancellationToken.None));

        Assert.Equal("Synthetic wait failure.", failure.Message);
        Assert.Equal(1, process.KillCount);
        Assert.True(process.HasExited);
        Assert.True(process.Disposed);
    }

    [Fact]
    public async Task FailedChildTermination_IsReportedInsteadOfClaimingCleanup()
    {
        var process = new FakeProcess { FailKill = true };
        var runner = new ScCommandRunner(_ => process, TimeSpan.FromMilliseconds(40));

        IOException failure = await Assert.ThrowsAsync<IOException>(() =>
            runner.RunAsync(["stop", "OpenBlade"], CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5)));

        Assert.Contains("could not be confirmed stopped", failure.Message);
        Assert.False(process.HasExited);
        Assert.True(process.Disposed);
    }

    private sealed class FakeProcess : IScCommandProcess
    {
        private bool outputStarted;
        private bool errorStarted;
        public bool CompleteImmediately { get; init; }
        public bool FailKill { get; init; }
        public bool ExitDuringKill { get; init; }
        public bool FailFirstWait { get; init; }
        public int ExitCode => 0;
        public bool HasExited { get; private set; }
        public bool Disposed { get; private set; }
        public int KillCount { get; private set; }
        public int WaitCount { get; private set; }
        public bool BothReadersStartedBeforeWait { get; private set; }
        public TaskCompletionSource WaitStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<string> ReadOutputAsync(CancellationToken cancellationToken)
        {
            outputStarted = true;
            return ReadAsync("RUNNING", cancellationToken);
        }

        public Task<string> ReadErrorAsync(CancellationToken cancellationToken)
        {
            errorStarted = true;
            return ReadAsync("diagnostic", cancellationToken);
        }

        private async Task<string> ReadAsync(string text, CancellationToken cancellationToken)
        {
            if (!CompleteImmediately)
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            }
            return text;
        }

        public async Task WaitForExitAsync(CancellationToken cancellationToken)
        {
            WaitCount++;
            BothReadersStartedBeforeWait = outputStarted && errorStarted;
            WaitStarted.TrySetResult();
            if (FailFirstWait && WaitCount == 1)
            {
                throw new InvalidOperationException("Synthetic wait failure.");
            }
            if (CompleteImmediately)
            {
                HasExited = true;
            }
            if (!HasExited)
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            }
        }

        public void Kill()
        {
            KillCount++;
            if (ExitDuringKill)
            {
                HasExited = true;
                throw new InvalidOperationException("The process has already exited.");
            }
            if (FailKill)
            {
                throw new System.ComponentModel.Win32Exception("Synthetic termination failure.");
            }
            HasExited = true;
        }

        public void Dispose() => Disposed = true;
    }
}
