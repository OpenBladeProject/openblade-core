using OpenBlade.Tray;

namespace OpenBlade.Tray.Tests;

public sealed class RazerKeyboardWindowsUpdateJobTests
{
    [Fact]
    public async Task TimeoutRequestsAbortWithoutReleasingAnActiveNativeJob()
    {
        var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var released = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int begin = 0, abort = 0, end = 0, cleanup = 0, evidence = 0;
        var error = await Assert.ThrowsAsync<RazerKeyboardWindowsUpdateException>(() => RazerKeyboardWindowsUpdateJob.RunAsync(
            () => { begin++; return new object(); }, job => { end++; return new object(); }, job => abort++,
            job => cleanup++, completed.Task, TimeSpan.FromMilliseconds(5), default, () => released.SetResult(), result => evidence++));
        Assert.Equal(unchecked((int)0x800705B4), error.HResult);
        Assert.Equal(1, begin);
        Assert.Equal(1, abort);
        Assert.Equal(0, end);
        Assert.Equal(0, cleanup);
        Assert.False(released.Task.IsCompleted);
        completed.SetResult();
        await released.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(1, end);
        Assert.Equal(1, cleanup);
        Assert.Equal(1, evidence);
    }

    [Fact]
    public async Task CancellationAfterBeginRequestsAbortAndNeverRetries()
    {
        var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var released = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var cancellation = new CancellationTokenSource();
        int begin = 0, abort = 0;
        var running = RazerKeyboardWindowsUpdateJob.RunAsync(() => { begin++; return new object(); }, job => new object(),
            job => abort++, job => { }, completed.Task, TimeSpan.FromSeconds(10), cancellation.Token,
            () => released.SetResult());
        cancellation.Cancel();
        var error = await Assert.ThrowsAsync<RazerKeyboardWindowsUpdateException>(() => running);
        Assert.Equal(unchecked((int)0x800704C7), error.HResult);
        Assert.Equal(1, begin);
        Assert.Equal(1, abort);
        Assert.False(released.Task.IsCompleted);
        completed.SetResult();
        await released.Task.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task AbortFailureRetainsItsNativeCodeAndStillKeepsTheLiveJob()
    {
        var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var released = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var error = await Assert.ThrowsAsync<RazerKeyboardWindowsUpdateException>(() => RazerKeyboardWindowsUpdateJob.RunAsync(
            () => new object(), job => new object(), job => throw new RazerKeyboardWindowsUpdateException("abort", unchecked((int)0x80004005)),
            job => { }, completed.Task, TimeSpan.FromMilliseconds(5), default, () => released.SetResult()));
        Assert.Contains("80004005", error.Message);
        Assert.False(released.Task.IsCompleted);
        completed.SetResult();
        await released.Task.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task SuccessfulJobEndsBeforeCleanupAndRelease()
    {
        List<string> calls = [];
        await RazerKeyboardWindowsUpdateJob.RunAsync(() => { calls.Add("begin"); return new object(); },
            job => { calls.Add("end"); return new object(); }, job => calls.Add("abort"), job => calls.Add("cleanup"),
            Task.CompletedTask, TimeSpan.FromSeconds(1), default, () => calls.Add("release"));
        Assert.Equal(["begin", "end", "cleanup", "release"], calls);
    }

    [Fact]
    public async Task BeginFailureReleasesOwnershipWithoutAbortOrRetry()
    {
        int released = 0;
        await Assert.ThrowsAsync<InvalidOperationException>(() => RazerKeyboardWindowsUpdateJob.RunAsync(
            () => throw new InvalidOperationException(), job => throw new Exception(), job => throw new Exception(),
            job => throw new Exception(), Task.CompletedTask, TimeSpan.FromSeconds(1), default, () => released++));
        Assert.Equal(1, released);
    }
}
