using OpenBlade.Deployment;
using OpenBlade.Setup;

namespace OpenBlade.Setup.Tests;

public sealed class SetupManagedMailboxAdmissionTests
{
    [Fact]
    public async Task ActualSetupAdmissionRequiresOriginalExitBeforeTheProtectedReceipt()
    {
        var order = new List<string>();
        var original = new Original(order);
        var receipt = new Receipt(order);
        await SetupManagedMailboxAdmission.ConfirmAsync(original, receipt, TimeSpan.FromSeconds(1), default);
        Assert.Equal(new[] { "original-exited", "protected-receipt" }, order);
        order.Clear();
        original.Running = true;
        await Assert.ThrowsAsync<IOException>(() => SetupManagedMailboxAdmission.ConfirmAsync(original, receipt, TimeSpan.FromSeconds(1), default));
        Assert.Equal(new[] { "original-exited" }, order);
        order.Clear();
        original.Running = false;
        receipt.Missing = true;
        await Assert.ThrowsAsync<IOException>(() => SetupManagedMailboxAdmission.ConfirmAsync(original, receipt, TimeSpan.FromSeconds(1), default));
        Assert.Equal(new[] { "original-exited", "protected-receipt" }, order);
    }

    [Fact]
    public async Task RetainedOriginalWaitAllowsDelayedExitButDistinguishesDeadlineCancelAndError()
    {
        int polls = 0;
        await SetupRetainedOriginalExit.WaitAsync(() => ++polls == 3, TimeSpan.FromSeconds(2), default);
        Assert.Equal(3, polls);
        await Assert.ThrowsAsync<TimeoutException>(() => SetupRetainedOriginalExit.WaitAsync(() => false, TimeSpan.FromMilliseconds(25), default));
        using var cancellation = new CancellationTokenSource();
        Task waiting = SetupRetainedOriginalExit.WaitAsync(() => false, TimeSpan.FromSeconds(2), cancellation.Token);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiting);
        await Assert.ThrowsAsync<System.ComponentModel.Win32Exception>(() => SetupRetainedOriginalExit.WaitAsync(
            () => throw new System.ComponentModel.Win32Exception(6), TimeSpan.FromSeconds(1), default));
    }

    [Fact]
    public async Task ADelayedExitSampleCannotRenewTheMonotonicBudget()
    {
        var clock = new ExitClock();
        await Assert.ThrowsAsync<TimeoutException>(() => SetupRetainedOriginalExit.WaitAsync(() =>
        { clock.Timestamp = 1001; return true; }, TimeSpan.FromSeconds(1), default, clock));
    }
    private sealed class ExitClock : TimeProvider
    {
        internal long Timestamp { get; set; }
        public override long TimestampFrequency => 1000;
        public override long GetTimestamp() => Timestamp;
    }

    private sealed class Original(List<string> order) : ISetupOriginalManagedProcess
    {
        internal bool Running { get; set; }
        public SetupCandidateLaunch Launch => new(17, 100, @"C:\private-fixture\OpenBlade.Service.exe", new string('a', 64));
        public Task WaitForExitAsync(TimeSpan budget, CancellationToken token) { order.Add("original-exited"); if (Running) { throw new IOException("Original is still running."); } return Task.CompletedTask; }
        public void Dispose() { }
    }
    private sealed class Receipt(List<string> order) : IManagedMailboxReceipt
    {
        internal bool Missing { get; set; }
        public void ConfirmOriginalExited() { order.Add("protected-receipt"); if (Missing) { throw new IOException("No quiescence receipt."); } }
    }
}
