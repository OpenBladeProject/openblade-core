using OpenBlade.Protocol;

namespace OpenBlade.Protocol.Tests;

public sealed class CpuVoltageValidationDeadlineTests
{
    [Theory]
    [InlineData("expiry")]
    [InlineData("clock")]
    [InlineData("identity")]
    [InlineData("cancel")]
    public void DeadlineRevocationIsIrreversibleAndDoesNotDisposePendingWorkerResources(string reason)
    {
        long now = 100; bool identity = true; var resources = new Resources();
        using var cancellation = new CancellationTokenSource();
        var deadline = new CpuLiveCurveWriteDeadline(100, 600_100, () => now, () => identity, cancellation.Token, resources);
        Assert.True(deadline.IsCurrent());
        if (reason == "expiry") { now = 600_100; }
        if (reason == "clock") { now = 99; }
        if (reason == "identity") { identity = false; }
        if (reason == "cancel") { cancellation.Cancel(); }
        Assert.False(deadline.IsCurrent()); Assert.True(deadline.Token.IsCancellationRequested);
        now = 100; identity = true;
        Assert.False(deadline.IsCurrent()); Assert.False(resources.Disposed);
        deadline.Dispose(); Assert.True(resources.Disposed);
    }
    [Fact]
    public void DeadlineRechecksTimeAfterPotentiallySlowIdentityCheck()
    {
        long now = 100;
        using var deadline = new CpuLiveCurveWriteDeadline(100, 600_100, () => now,
            () => { now = 600_100; return true; }, default);
        Assert.False(deadline.IsCurrent());
    }
    [Fact]
    public void FixedRunBindsExactHostBudgetServiceBirthVersionAndDriverTag()
    {
        Guid run = Guid.NewGuid();
        var job = new CpuVoltageJobDeadline(Guid.NewGuid(), Guid.NewGuid(), 1, 2, @"C:\protected\OpenBlade.Capture.exe",
            100, 600_100, new('a', 40), new('A', 64), "exact-version");
        string tag = $"1:{run:N}:{Guid.NewGuid():N}:{job.Manifest}:{new string('b', 64)}";
        var value = new CpuVoltageRunDeadline(job, run, 3, 4, job.HostImage, 5, 6, tag);
        Assert.True(value.Matches(tag, 5, 6, job.ServiceVersion));
        Assert.False(value.Matches(tag, 5, 7, job.ServiceVersion));
        Assert.False(value.Matches(tag, 5, 6, "different-version"));
        Assert.False(value.Matches(tag + "x", 5, 6, job.ServiceVersion));
        Assert.True(job.IsCurrent(100)); Assert.False(job.IsCurrent(600_100));
        Assert.False((job with { ExpiresTick = 600_101 }).IsCurrent(100));
    }
    private sealed class Resources : IDisposable
    {
        internal bool Disposed;
        public void Dispose() => Disposed = true;
    }
}
