using OpenBlade.Service;

namespace OpenBlade.Service.Tests;

public sealed class CpuFirmwareReadSchedulerTests
{
    [Fact]
    public async Task CancelledSynchronousGetterRetainsLeaseUntilItActuallyFinishes()
    {
        using var firstCancellation = new CancellationTokenSource();
        using var secondCancellation = new CancellationTokenSource();
        using var release = new ManualResetEventSlim();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int secondReads = 0;
        Task<int> first = CpuFirmwareReadScheduler.RunAsync(() =>
        {
            entered.SetResult();
            release.Wait();
            return 1;
        }, TimeSpan.FromSeconds(5), firstCancellation.Token);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            firstCancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first);
            Task<int> second = CpuFirmwareReadScheduler.RunAsync(() => ++secondReads,
                TimeSpan.FromSeconds(5), secondCancellation.Token);
            secondCancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => second);
            Assert.Equal(0, secondReads);
        }
        finally { release.Set(); }
        Assert.Equal(2, await CpuFirmwareReadScheduler.RunAsync(() => 2,
            TimeSpan.FromSeconds(5), CancellationToken.None));
    }
}
