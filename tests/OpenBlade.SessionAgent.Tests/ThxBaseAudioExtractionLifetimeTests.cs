using System.ComponentModel;

namespace OpenBlade.SessionAgent.Tests;

public sealed class ThxBaseAudioExtractionLifetimeTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ConfirmedExitAfterCancellationWaitsBeforeAllowingCleanup(bool alreadyExited)
    {
        int kills = 0;
        int waits = 0;
        await ThxBaseAudioPackage.StopExtractionAsync(() => alreadyExited, () => kills++, token =>
        {
            Assert.True(token.CanBeCanceled);
            Assert.False(token.IsCancellationRequested);
            waits++;
            return Task.CompletedTask;
        }, new OperationCanceledException("acquisition canceled"));

        Assert.Equal(alreadyExited ? 0 : 1, kills);
        Assert.Equal(1, waits);
    }

    [Fact]
    public async Task ExitRaceStillRequiresTheIndependentWait()
    {
        bool waited = false;
        await ThxBaseAudioPackage.StopExtractionAsync(() => false,
            () => throw new InvalidOperationException("exited before kill"), token =>
            {
                waited = true;
                return Task.CompletedTask;
            }, new IOException("acquisition failed"));
        Assert.True(waited);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task UnconfirmedExitRetainsBothAcquisitionAndStopFailures(bool waitTimesOut)
    {
        var acquisition = new OperationCanceledException("acquisition canceled");
        Exception stop = waitTimesOut
            ? new OperationCanceledException("bounded exit wait expired")
            : new Win32Exception(5, "owned extractor could not stop");
        int waits = 0;
        IOException exception = await Assert.ThrowsAnyAsync<IOException>(() =>
            ThxBaseAudioPackage.StopExtractionAsync(() => false,
                () => { if (!waitTimesOut) { throw stop; } }, token =>
                {
                    waits++;
                    return Task.FromException(stop);
                }, acquisition));

        var causes = Assert.IsType<AggregateException>(exception.InnerException);
        Assert.Same(acquisition, causes.InnerExceptions[0]);
        Assert.Same(stop, causes.InnerExceptions[1]);
        Assert.Equal(waitTimesOut ? 1 : 0, waits);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task KillFailuresAlwaysRetainTheUnconfirmedExtractor(bool aggregate)
    {
        var acquisition = new IOException("acquisition failed");
        Exception stop = aggregate
            ? new AggregateException(new Win32Exception(5, "descendant remained active"))
            : new NotSupportedException("process termination unavailable");
        IOException exception = await Assert.ThrowsAnyAsync<IOException>(() =>
            ThxBaseAudioPackage.StopExtractionAsync(() => false, () => throw stop,
                token => Task.CompletedTask, acquisition));

        var causes = Assert.IsType<AggregateException>(exception.InnerException);
        Assert.Same(acquisition, causes.InnerExceptions[0]);
        Assert.Same(stop, causes.InnerExceptions[1]);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DrainFaultCannotReplaceAcquisitionOrUnconfirmedStop(bool stopUnconfirmed)
    {
        var acquisition = new OperationCanceledException("caller canceled acquisition");
        var stop = new Win32Exception(5, "extractor still active");
        var drain = new IOException("pipe closed");
        var reported = new List<Exception>();

        async Task FailAcquisitionAsync()
        {
            try { throw acquisition; }
            catch
            {
                await ThxBaseAudioPackage.FinishFailedExtractionAsync(() => Task.CompletedTask,
                    () => ThxBaseAudioPackage.StopExtractionAsync(() => false,
                        () => { if (stopUnconfirmed) { throw stop; } },
                        token => Task.CompletedTask, acquisition),
                    Task.FromException(drain), reportFailure: reported.Add);
                throw;
            }
        }

        Exception observed = await Assert.ThrowsAnyAsync<Exception>(FailAcquisitionAsync);
        if (stopUnconfirmed)
        {
            Assert.IsAssignableFrom<IOException>(observed);
            var causes = Assert.IsType<AggregateException>(observed.InnerException);
            Assert.Same(acquisition, causes.InnerExceptions[0]);
            Assert.Same(stop, causes.InnerExceptions[1]);
        }
        else
        {
            Assert.Same(acquisition, observed);
        }
        Assert.Contains(drain, reported);
    }

    [Fact]
    public async Task BoundedDrainWaitStillObservesLateFault()
    {
        var drain = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var reported = new TaskCompletionSource<Exception>(TaskCreationOptions.RunContinuationsAsynchronously);
        var lateFailure = new IOException("late pipe disposal");
        await ThxBaseAudioPackage.FinishFailedExtractionAsync(() => Task.CompletedTask,
            () => Task.CompletedTask, drain.Task, TimeSpan.Zero, exception =>
            {
                if (ReferenceEquals(exception, lateFailure)) { reported.TrySetResult(exception); }
            });
        Assert.False(drain.Task.IsCompleted);

        drain.SetException(lateFailure);

        Assert.Same(lateFailure, await reported.Task.WaitAsync(TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public async Task DrainCancellationFailureCannotSkipStoppingTheExtractor()
    {
        var cancellationFailure = new AggregateException(new IOException("cancel callback failed"));
        var reported = new List<Exception>();
        bool stopped = false;
        await ThxBaseAudioPackage.FinishFailedExtractionAsync(() => Task.FromException(cancellationFailure),
            () => { stopped = true; return Task.CompletedTask; }, Task.CompletedTask, reportFailure: reported.Add);

        Assert.True(stopped);
        Assert.Contains(cancellationFailure, reported);
    }
}
