using OpenBlade.Capture;

namespace OpenBlade.Capture.Tests;

public sealed class CpuPmBootstrapBoundSafetyTests
{
    [Theory]
    [InlineData("create", 0, 0)]
    [InlineData("tag", 1, 0)]
    [InlineData("start", 1, 1)]
    public void LostBootstrapAfterReturnedMutationPreventsEveryFollowingMutation(string boundary, int tags, int starts)
    {
        var f = new Fixture { LoseAfter = boundary };
        Assert.False(f.Owner.PrepareAndStart(CancellationToken.None));
        Assert.Equal(1, f.Creates); Assert.Equal(tags, f.Tags); Assert.Equal(starts, f.Starts);
        Assert.Same(f.Owner, f.Safety.Retained); Assert.Equal(0, f.Safety.Releases);
        f.BootstrapAlive = true; // returning metadata cannot undo the failure
        Assert.False(f.Wrapped.ReadFresh().Admits);
        Assert.Equal(CpuPmDriverCleanup.Refused, f.Owner.Cleanup(CancellationToken.None));
        Assert.Equal(0, f.Stops); Assert.Equal(0, f.Deletes); Assert.Equal(0, f.Safety.Releases);
    }
    [Fact]
    public void BootstrapLossInsideDelegatedFullReadDeniesBeforeCreate()
    {
        var f = new Fixture(); f.Safety.OnRead = () => f.BootstrapAlive = false;
        Assert.False(f.Owner.PrepareAndStart(CancellationToken.None));
        Assert.Equal(0, f.Creates); Assert.Equal(0, f.Tags); Assert.Equal(0, f.Starts);
        f.BootstrapAlive = true; f.Safety.OnRead = null;
        Assert.False(f.Wrapped.ReadFresh().Admits); Assert.Equal(0, f.Safety.Releases);
    }
    [Fact]
    public void LostBootstrapAfterReturnedStopPreventsDeleteAndKeepsExactOwnerRetained()
    {
        var f = new Fixture(); Assert.True(f.Owner.PrepareAndStart(CancellationToken.None));
        f.LoseAfter = "stop";
        Assert.Equal(CpuPmDriverCleanup.Refused, f.Owner.Cleanup(CancellationToken.None));
        Assert.Equal(1, f.Stops); Assert.Equal(0, f.Deletes);
        Assert.Same(f.Owner, f.Safety.Retained); Assert.Equal(0, f.Safety.Releases);
    }
    [Fact]
    public void HealthyExplicitNoChildCleanupReleasesOnlyAfterActualReducerProvesAbsence()
    {
        var f = new Fixture(); Assert.True(f.Owner.PrepareAndStart(CancellationToken.None));
        Assert.Equal(CpuPmDriverCleanup.Complete, f.Owner.Cleanup(CancellationToken.None));
        Assert.Equal(1, f.Stops); Assert.Equal(1, f.Deletes); Assert.Equal(1, f.Closes);
        Assert.Equal(CpuPmDriverEvent.AbsenceVerified, f.Events[^1]);
        Assert.Equal(1, f.Safety.Releases); Assert.Null(f.Safety.Retained);
        f.Wrapped.ReleaseAfterVerifiedCleanup(); Assert.Equal(1, f.Safety.Releases);
    }
    private sealed class Fixture : ICpuPmDriverLifecyclePlatform, ICpuPmCreatedDriver, ICpuPmDriverJournal, ICpuPmAuthorityClock
    {
        internal readonly Safety Safety = new();
        internal readonly CpuPmBootstrapBoundSafety Wrapped;
        internal readonly CpuPmOwnedDriverLifecycle Owner;
        internal readonly List<CpuPmDriverEvent> Events = [];
        internal bool BootstrapAlive = true;
        internal string LoseAfter = "";
        internal int Creates, Tags, Starts, Stops, Deletes, Closes;
        private string? tag;
        private CpuPmDriverState state = CpuPmDriverState.Stopped;
        internal Fixture()
        {
            Wrapped = new(Safety, () => { if (!BootstrapAlive) { throw new IOException("Bootstrap lost."); } });
            Owner = new(new(Guid.NewGuid(), Guid.NewGuid(), new('A', 64), new('a', 64)), this, Wrapped, this, this);
        }
        public TimeSpan Now => TimeSpan.Zero;
        private void Boundary(string name) { if (LoseAfter == name) { BootstrapAlive = false; } }
        public ICpuPmDriverMutex AcquireMutex(CancellationToken token) => new MutexLease();
        public CpuPmDriverAbsence ReadIndependentAbsence() => new(Creates == 0 || Deletes == 1 ? 1060 : 0, Creates == 0 || Deletes == 1, true, Creates == 0 || Deletes == 1 ? 0 : 1);
        public ICpuPmCreatedDriver CreateFixedDriver() { Creates++; Boundary("create"); return this; }
        public CpuPmDriverSnapshot ReadFresh() => new(true, true, true, tag, state);
        public bool WriteNewOwnerTagAndFlush(CpuPmDriverOwnerTag owner) { Tags++; tag = owner.Value; Boundary("tag"); return true; }
        public bool StartOnce() { Starts++; state = CpuPmDriverState.Running; Boundary("start"); return true; }
        public bool StopOnce() { Stops++; state = CpuPmDriverState.Stopped; Boundary("stop"); return true; }
        public bool DeleteOnce() { Deletes++; return true; }
        public void CloseAfterDelete() => Closes++;
        public bool AppendAndFlush(CpuPmDriverLifecycleRecord record) { Events.Add(record.Event); return true; }
        private sealed class MutexLease : ICpuPmDriverMutex
        {
            private readonly int thread = Environment.CurrentManagedThreadId;
            private bool closed;
            public bool IsOwnedByCurrentThread => !closed && thread == Environment.CurrentManagedThreadId;
            public bool WasAbandoned => false;
            public void ReleaseAndCloseOnCurrentThread() { Assert.Equal(thread, Environment.CurrentManagedThreadId); closed = true; }
        }
    }
    private sealed class Safety : ICpuPmDriverSafety
    {
        internal CpuPmOwnedDriverLifecycle? Retained;
        internal int Releases;
        internal Action? OnRead;
        public CpuPmDriverSafetySnapshot ReadFresh() { OnRead?.Invoke(); return new(true, true, true, true, true, true, true); }
        public CpuPmDriverProofSafetySnapshot ReadFreshProof() => new(true, true, true, true);
        public void RetainUntilVerifiedCleanup(CpuPmOwnedDriverLifecycle owner) => Retained = owner;
        public void ReleaseAfterVerifiedCleanup()
        {
            Assert.NotNull(Retained); Assert.Equal(CpuPmDriverPhase.Cleaned, Retained.Phase); Assert.False(Retained.MutationInFlight);
            Releases++; Retained = null;
        }
    }
}
