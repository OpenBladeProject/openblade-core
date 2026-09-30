using OpenBlade.Capture;

namespace OpenBlade.Capture.Tests;

// Fake platform only. Never instantiate the native SCM/registry/mutex providers.
public sealed class CpuPmOwnedDriverLifecycleTests
{
    private static readonly Guid Run = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid Lifetime = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private static CpuPmDriverOwnerTag Owner() => new(Run, Lifetime, new('A', 64), new('b', 64));

    [Fact]
    public void OwnedCreateStartChildAndTerminalCleanupPreserveOrderedDurableEvidence()
    {
        var f = new Fixture(); f.Active();
        Assert.True(f.Lifecycle.ReadFresh(Run, Lifetime)!.Admits(Run, Lifetime));
        Assert.Equal(1, f.Platform.Creates); Assert.Equal(1, f.Driver.Starts);
        Assert.False(f.Platform.MutexHeld); Assert.True(f.Safety.Retained); Assert.Equal(0, f.Safety.Releases);
        f.Child.State = CpuPmChildState.Terminal;
        Assert.Equal(CpuPmDriverCleanup.Complete, f.Lifecycle.Cleanup(CancellationToken.None));
        Assert.Equal(CpuPmDriverPhase.Cleaned, f.Lifecycle.Phase); Assert.Equal(1, f.Driver.Stops); Assert.Equal(1, f.Driver.Deletes);
        Assert.Equal(1, f.Driver.Closes); Assert.Equal(1, f.Safety.Releases); Assert.Null(f.Lifecycle.ReadFresh(Run, Lifetime));
        Assert.Equal(Enum.GetValues<CpuPmDriverEvent>(), f.Journal.Records.Select(r => r.Event));
        Assert.Equal(Enumerable.Range(1, 13), f.Journal.Records.Select(r => r.Sequence));
        Assert.All(f.Journal.Records, r => Assert.Equal(Owner(), r.Owner));
    }

    [Theory]
    [InlineData("scm-access-denied")]
    [InlineData("scm-existing")]
    [InlineData("registry")]
    [InlineData("active")]
    [InlineData("scan-unknown")]
    [InlineData("abandoned")]
    [InlineData("mutex-timeout")]
    [InlineData("host")]
    [InlineData("power")]
    [InlineData("session")]
    [InlineData("controllers")]
    [InlineData("pins")]
    [InlineData("operator")]
    public void MissingIndependentAbsenceOrAnySafetyFactPreventsCreate(string fault)
    {
        var f = new Fixture();
        f.Platform.Absence = fault switch
        {
            "scm-access-denied" => new(5, true, true, 0),
            "scm-existing" => new(0, true, true, 0),
            "registry" => new(1060, false, true, 0),
            "active" => new(1060, true, true, 1),
            "scan-unknown" => new(1060, true, false, 0),
            _ => f.Platform.Absence,
        };
        f.Platform.Abandoned = fault == "abandoned"; f.Platform.Timeout = fault == "mutex-timeout";
        f.Safety.Current = fault switch
        {
            "host" => f.Safety.Current with { ExactRz090528Bios202Package1 = false },
            "power" => f.Safety.Current with { AcPower = false },
            "session" => f.Safety.Current with { LocalActiveDefaultSession = false },
            "controllers" => f.Safety.Current with { ControllersAbsent = false },
            "pins" => f.Safety.Current with { VendorAndPayloadPinsHeld = false },
            "operator" => f.Safety.Current with { TrustedOperatorPremiseAccepted = false },
            _ => f.Safety.Current,
        };
        Assert.False(f.Lifecycle.PrepareAndStart(CancellationToken.None)); Assert.Equal(0, f.Platform.Creates);
        Assert.Equal(0, f.Driver.Starts); Assert.False(f.Platform.MutexHeld);
    }

    [Theory]
    [InlineData((int)CpuPmDriverEvent.CreateIntent, 0, 0)]
    [InlineData((int)CpuPmDriverEvent.Created, 1, 0)]
    [InlineData((int)CpuPmDriverEvent.TagIntent, 1, 0)]
    [InlineData((int)CpuPmDriverEvent.Tagged, 1, 0)]
    [InlineData((int)CpuPmDriverEvent.StartIntent, 1, 0)]
    [InlineData((int)CpuPmDriverEvent.Started, 1, 1)]
    public void FailedDurableRecordBlocksFollowingMutationsAndRetainsPins(int failValue, int creates, int starts)
    {
        var f = new Fixture(); f.Journal.Fail = (CpuPmDriverEvent)failValue;
        Assert.False(f.Lifecycle.PrepareAndStart(CancellationToken.None));
        Assert.Equal(creates, f.Platform.Creates); Assert.Equal(starts, f.Driver.Starts);
        Assert.True(f.Safety.Retained); Assert.Equal(0, f.Safety.Releases); Assert.Equal(0, f.Driver.Closes);
        Assert.Equal(CpuPmDriverCleanup.Refused, f.Lifecycle.Cleanup(CancellationToken.None));
        Assert.Equal(0, f.Driver.Stops); Assert.Equal(0, f.Driver.Deletes);
    }

    [Theory]
    [InlineData((int)CpuPmDriverEvent.CreateIntent, "absence", 0, 0)]
    [InlineData((int)CpuPmDriverEvent.TagIntent, "config", 1, 0)]
    [InlineData((int)CpuPmDriverEvent.StartIntent, "tag", 1, 0)]
    [InlineData((int)CpuPmDriverEvent.StartIntent, "cancel", 1, 0)]
    public void EveryDurableIntentIsFollowedByFreshAdmissionBeforeMutation(int kindValue, string fault, int creates, int starts)
    {
        var kind = (CpuPmDriverEvent)kindValue;
        var f = new Fixture(); using var cancel = new CancellationTokenSource();
        f.Journal.OnAppend = record =>
        {
            if (record.Event != kind) { return; }
            if (fault == "absence") { f.Platform.Absence = new(0, false, true, 0); }
            if (fault == "config") { f.Driver.Config = false; }
            if (fault == "tag") { f.Driver.Tag = "forged"; }
            if (fault == "cancel") { cancel.Cancel(); }
        };
        Assert.False(f.Lifecycle.PrepareAndStart(cancel.Token)); Assert.Equal(creates, f.Platform.Creates); Assert.Equal(starts, f.Driver.Starts);
    }

    [Theory]
    [InlineData("create")]
    [InlineData("tag")]
    [InlineData("start")]
    [InlineData("start-throw")]
    public void FalseOrUnknownNativeResultIsNotAdoptedAndNeverRetried(string fault)
    {
        var f = new Fixture(); f.Platform.FailCreate = fault == "create"; f.Driver.FailTag = fault == "tag";
        f.Driver.FailStart = fault == "start"; f.Driver.OnStart = fault == "start-throw" ? () => throw new IOException("private") : null;
        Assert.False(f.Lifecycle.PrepareAndStart(CancellationToken.None));
        int creates = f.Platform.Creates, starts = f.Driver.Starts;
        Assert.False(f.Lifecycle.PrepareAndStart(CancellationToken.None)); Assert.Equal(creates, f.Platform.Creates); Assert.Equal(starts, f.Driver.Starts);
        Assert.Null(f.Lifecycle.ReadFresh(Run, Lifetime)); Assert.Equal(0, f.Driver.Stops); Assert.Equal(0, f.Driver.Deletes); Assert.Equal(0, f.Driver.Closes);
    }

    [Fact]
    public async Task UnreturnedStartRetainsSameCallMutexAndPinsWithoutCleanupOrRetry()
    {
        var f = new Fixture(); using var entered = new ManualResetEventSlim(); using var release = new ManualResetEventSlim();
        f.Driver.OnStart = () => { entered.Set(); release.Wait(); };
        Task<bool> pending = Task.Run(() => f.Lifecycle.PrepareAndStart(CancellationToken.None));
        Assert.True(entered.Wait(TimeSpan.FromSeconds(5)));
        try
        {
            f.Lifecycle.MarkUncertain();
            Assert.True(f.Lifecycle.MutationInFlight); Assert.False(pending.IsCompleted); Assert.True(f.Platform.MutexHeld); Assert.True(f.Safety.Retained);
            Assert.Equal(CpuPmDriverCleanup.Refused, f.Lifecycle.Cleanup(CancellationToken.None));
            Assert.Equal(0, f.Driver.Stops); Assert.Equal(0, f.Driver.Deletes); Assert.Equal(0, f.Driver.Closes); Assert.Equal(0, f.Safety.Releases);
        }
        finally { release.Set(); }
        Assert.False(await pending.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.False(f.Platform.MutexHeld); Assert.False(f.Lifecycle.MutationInFlight); Assert.Equal(1, f.Driver.Starts);
        Assert.Equal(CpuPmDriverCleanup.Refused, f.Lifecycle.Cleanup(CancellationToken.None));
    }

    [Fact]
    public async Task UnreturnedStopCannotDeleteOrReleasePinsEvenAfterCancellation()
    {
        var f = new Fixture(); f.Active(); f.Child.State = CpuPmChildState.Terminal;
        using var entered = new ManualResetEventSlim(); using var release = new ManualResetEventSlim();
        f.Driver.OnStop = () => { entered.Set(); release.Wait(); };
        Task<CpuPmDriverCleanup> pending = Task.Run(() => f.Lifecycle.Cleanup(CancellationToken.None));
        Assert.True(entered.Wait(TimeSpan.FromSeconds(5)));
        try
        {
            f.Lifecycle.MarkUncertain(); Assert.True(f.Lifecycle.MutationInFlight); Assert.False(pending.IsCompleted);
            Assert.True(f.Platform.MutexHeld); Assert.Equal(0, f.Driver.Deletes); Assert.Equal(0, f.Driver.Closes); Assert.Equal(0, f.Safety.Releases);
        }
        finally { release.Set(); }
        Assert.Equal(CpuPmDriverCleanup.Refused, await pending.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal(1, f.Driver.Stops); Assert.Equal(0, f.Driver.Deletes); Assert.False(f.Platform.MutexHeld); Assert.True(f.Safety.Retained);
    }

    [Fact]
    public void MutexReleaseFailureCannotReportReadyForChildLaunch()
    {
        var f = new Fixture(); f.Platform.FailRelease = true;
        Assert.False(f.Lifecycle.PrepareAndStart(CancellationToken.None));
        Assert.False(f.Lifecycle.BeginChildLaunch(CancellationToken.None)); Assert.Equal(CpuPmDriverPhase.Blocked, f.Lifecycle.Phase);
        Assert.Equal(1, f.Driver.Starts); Assert.True(f.Safety.Retained);
    }

    [Theory]
    [InlineData("no-launch")]
    [InlineData("possible-launch")]
    [InlineData("live-child")]
    [InlineData("unknown-child")]
    [InlineData("wrong-start")]
    [InlineData("wrong-run")]
    [InlineData("tag-drift")]
    [InlineData("stopped")]
    [InlineData("clock-regression")]
    public void ProofRequiresOwnSuccessfulStartAndFreshBoundLiveChild(string fault)
    {
        var f = new Fixture(); Assert.True(f.Lifecycle.PrepareAndStart(CancellationToken.None));
        if (fault != "no-launch") { Assert.True(f.Lifecycle.BeginChildLaunch(CancellationToken.None)); }
        if (fault is not ("no-launch" or "possible-launch")) { Assert.True(f.Lifecycle.BindStartedChild(f.Child, CancellationToken.None)); }
        if (fault == "unknown-child") { f.Child.State = CpuPmChildState.Unknown; }
        if (fault == "wrong-start") { f.Child.Current = f.Child.Identity with { CreationFileTimeUtc = 201 }; }
        if (fault == "tag-drift") { f.Driver.Tag = "forged"; }
        if (fault == "stopped") { f.Driver.State = CpuPmDriverState.Stopped; }
        if (fault == "clock-regression") { f.Clock.Now = TimeSpan.FromTicks(-1); }
        var proof = f.Lifecycle.ReadFresh(fault == "wrong-run" ? Lifetime : Run, Lifetime);
        if (fault == "live-child") { Assert.True(proof!.Admits(Run, Lifetime)); }
        else { Assert.Null(proof); }
        Assert.Equal(1, f.Driver.Starts); Assert.Equal(0, f.Driver.Stops);
    }

    [Theory]
    [InlineData("possible")]
    [InlineData("running")]
    [InlineData("unknown")]
    [InlineData("wrong-peer")]
    public void CleanupRefusesLiveUnknownOrUnboundChild(string fault)
    {
        var f = new Fixture(); Assert.True(f.Lifecycle.PrepareAndStart(CancellationToken.None)); Assert.True(f.Lifecycle.BeginChildLaunch(CancellationToken.None));
        if (fault != "possible") { Assert.True(f.Lifecycle.BindStartedChild(f.Child, CancellationToken.None)); }
        if (fault == "unknown") { f.Child.State = CpuPmChildState.Unknown; }
        if (fault == "wrong-peer") { f.Child.State = CpuPmChildState.Terminal; f.Child.Current = f.Child.Identity with { ProcessId = 99 }; }
        Assert.Equal(CpuPmDriverCleanup.Refused, f.Lifecycle.Cleanup(CancellationToken.None));
        Assert.Equal(0, f.Driver.Stops); Assert.Equal(0, f.Driver.Deletes); Assert.Equal(0, f.Safety.Releases);
    }

    [Theory]
    [InlineData((int)CpuPmDriverEvent.StopIntent, "tag", 0, 0)]
    [InlineData((int)CpuPmDriverEvent.StopIntent, "child", 0, 0)]
    [InlineData((int)CpuPmDriverEvent.DeleteIntent, "config", 1, 0)]
    [InlineData((int)CpuPmDriverEvent.DeleteIntent, "power", 1, 0)]
    [InlineData((int)CpuPmDriverEvent.DeleteIntent, "cancel", 1, 0)]
    public void CleanupRechecksEveryIntentAndRetainsOnDrift(int kindValue, string fault, int stops, int deletes)
    {
        var kind = (CpuPmDriverEvent)kindValue;
        var f = new Fixture(); f.Active(); f.Child.State = CpuPmChildState.Terminal; using var cancel = new CancellationTokenSource();
        f.Journal.OnAppend = record =>
        {
            if (record.Event != kind) { return; }
            if (fault == "tag") { f.Driver.Tag = "forged"; }
            if (fault == "child") { f.Child.State = CpuPmChildState.Unknown; }
            if (fault == "config") { f.Driver.Config = false; }
            if (fault == "power") { f.Safety.Current = f.Safety.Current with { AcPower = false }; }
            if (fault == "cancel") { cancel.Cancel(); }
        };
        Assert.Equal(CpuPmDriverCleanup.Refused, f.Lifecycle.Cleanup(cancel.Token));
        Assert.Equal(stops, f.Driver.Stops); Assert.Equal(deletes, f.Driver.Deletes); Assert.Equal(0, f.Driver.Closes); Assert.True(f.Safety.Retained);
    }

    [Theory]
    [InlineData("false-stop")]
    [InlineData("false-delete")]
    [InlineData("deleted-flush")]
    [InlineData("absence")]
    public void CleanupFailureNeverReportsRestorationOrReleasesPins(string fault)
    {
        var f = new Fixture(); f.Active(); f.Child.State = CpuPmChildState.Terminal;
        f.Driver.FailStop = fault == "false-stop"; f.Driver.FailDelete = fault == "false-delete";
        if (fault == "deleted-flush") { f.Journal.Fail = CpuPmDriverEvent.Deleted; }
        if (fault == "absence") { f.Platform.Absence = new(1072, false, true, 0); }
        Assert.Equal(CpuPmDriverCleanup.Refused, f.Lifecycle.Cleanup(CancellationToken.None));
        Assert.True(f.Safety.Retained); Assert.Equal(0, f.Safety.Releases);
        int stops = f.Driver.Stops, deletes = f.Driver.Deletes;
        Assert.Equal(CpuPmDriverCleanup.Refused, f.Lifecycle.Cleanup(CancellationToken.None));
        Assert.Equal(stops, f.Driver.Stops); Assert.Equal(deletes, f.Driver.Deletes);
    }

    [Fact]
    public void StopPendingPollsAreReadOnlyBoundedAndNeverRepeatStop()
    {
        var f = new Fixture(); Assert.True(f.Lifecycle.PrepareAndStart(CancellationToken.None)); f.Driver.StopPending = true;
        Assert.Equal(CpuPmDriverCleanup.PendingStopped, f.Lifecycle.Cleanup(CancellationToken.None));
        Assert.Equal(CpuPmDriverCleanup.PendingStopped, f.Lifecycle.Cleanup(CancellationToken.None));
        Assert.Equal(1, f.Driver.Stops); Assert.Equal(0, f.Driver.Deletes);
        f.Driver.State = CpuPmDriverState.Stopped;
        Assert.Equal(CpuPmDriverCleanup.Complete, f.Lifecycle.Cleanup(CancellationToken.None));
        Assert.Equal(1, f.Driver.Stops); Assert.Equal(1, f.Driver.Deletes);
        var timed = new Fixture(); Assert.True(timed.Lifecycle.PrepareAndStart(CancellationToken.None)); timed.Driver.StopPending = true;
        Assert.Equal(CpuPmDriverCleanup.PendingStopped, timed.Lifecycle.Cleanup(CancellationToken.None)); timed.Clock.Now = TimeSpan.FromSeconds(10);
        Assert.Equal(CpuPmDriverCleanup.Refused, timed.Lifecycle.Cleanup(CancellationToken.None)); Assert.Equal(1, timed.Driver.Stops); Assert.Equal(0, timed.Driver.Deletes);
    }

    [Fact]
    public void AuthorityRevocationPreventsNewChildButAllowsExplicitNoChildCleanup()
    {
        var f = new Fixture(); Assert.True(f.Lifecycle.PrepareAndStart(CancellationToken.None)); f.Lifecycle.RevokeAuthority();
        Assert.Null(f.Lifecycle.ReadFresh(Run, Lifetime));
        Assert.Equal(CpuPmDriverCleanup.Complete, f.Lifecycle.Cleanup(CancellationToken.None));
        var other = new Fixture(); Assert.True(other.Lifecycle.PrepareAndStart(CancellationToken.None)); other.Lifecycle.RevokeAuthority();
        Assert.False(other.Lifecycle.BeginChildLaunch(CancellationToken.None));
    }

    [Fact]
    public void LiveProofUsesCheapSafetyButCannotAuthorizeAnyLifecycleMutation()
    {
        var f = new Fixture(); f.Active(); int full = f.Safety.FullReads;
        f.Safety.DenyFull = true;
        Assert.True(f.Lifecycle.ReadFresh(Run, Lifetime)!.Admits(Run, Lifetime));
        Assert.Equal(full, f.Safety.FullReads); Assert.True(f.Safety.ProofReads > 0);
        f.Child.State = CpuPmChildState.Terminal;
        Assert.Equal(CpuPmDriverCleanup.Refused, f.Lifecycle.Cleanup(CancellationToken.None));
        Assert.Equal(0, f.Driver.Stops); Assert.Equal(0, f.Driver.Deletes);
        var fresh = new Fixture(); fresh.Safety.DenyFull = true;
        Assert.False(fresh.Lifecycle.PrepareAndStart(CancellationToken.None));
        Assert.Equal(0, fresh.Platform.Creates); Assert.Equal(0, fresh.Safety.ProofReads);
    }
    [Fact]
    public void CheapSafetyDenialStopsAuthorityAndInitialMetadataCannotGrantOwnership()
    {
        var initial = new Fixture(); Assert.Null(initial.Lifecycle.ReadFresh(Run, Lifetime));
        var f = new Fixture(); f.Active(); f.Safety.DenyProof = true;
        Assert.Null(f.Lifecycle.ReadFresh(Run, Lifetime));
        Assert.Equal(0, f.Driver.Stops); Assert.Equal(0, f.Driver.Deletes);
    }
    private sealed class Fixture
    {
        internal readonly Clock Clock = new(); internal readonly Safety Safety = new(); internal readonly Journal Journal = new();
        internal readonly Driver Driver = new(); internal readonly Child Child = new(); internal readonly Platform Platform;
        internal readonly CpuPmOwnedDriverLifecycle Lifecycle;
        internal Fixture() { Platform = new(Driver); Lifecycle = new(Owner(), Platform, Safety, Journal, Clock); }
        internal void Active()
        {
            Assert.True(Lifecycle.PrepareAndStart(CancellationToken.None)); Assert.True(Lifecycle.BeginChildLaunch(CancellationToken.None));
            Assert.True(Lifecycle.BindStartedChild(Child, CancellationToken.None));
        }
    }
    private sealed class Clock : ICpuPmAuthorityClock { public TimeSpan Now { get; set; } }
    private sealed class Safety : ICpuPmDriverSafety
    {
        internal CpuPmDriverSafetySnapshot Current = new(true, true, true, true, true, true, true);
        internal bool Retained; internal int Releases;
        internal int FullReads, ProofReads;
        internal bool DenyFull, DenyProof;
        public CpuPmDriverSafetySnapshot ReadFresh()
        { FullReads++; return DenyFull ? new(false, false, false, false, false, false, false) : Current; }
        public CpuPmDriverProofSafetySnapshot ReadFreshProof()
        { ProofReads++; return new(Current.Admits && !DenyProof, Current.VendorAndPayloadPinsHeld, Current.AcPower && Current.LocalActiveDefaultSession, Current.TrustedOperatorPremiseAccepted); }
        public void RetainUntilVerifiedCleanup(CpuPmOwnedDriverLifecycle owner) => Retained = true;
        public void ReleaseAfterVerifiedCleanup() { Releases++; Retained = false; }
    }
    private sealed class Journal : ICpuPmDriverJournal
    {
        internal readonly List<CpuPmDriverLifecycleRecord> Records = []; internal CpuPmDriverEvent? Fail; internal Action<CpuPmDriverLifecycleRecord>? OnAppend;
        public bool AppendAndFlush(CpuPmDriverLifecycleRecord record) { Records.Add(record); OnAppend?.Invoke(record); return record.Event != Fail; }
    }
    private sealed class Platform(Driver driver) : ICpuPmDriverLifecyclePlatform
    {
        internal CpuPmDriverAbsence Absence = new(1060, true, true, 0); internal int Creates; internal bool FailCreate, Abandoned, Timeout, MutexHeld, FailRelease;
        public CpuPmDriverAbsence ReadIndependentAbsence() => Absence;
        public ICpuPmCreatedDriver? CreateFixedDriver() { Assert.True(MutexHeld); Creates++; return FailCreate ? null : driver; }
        public ICpuPmDriverMutex AcquireMutex(CancellationToken token) { MutexHeld = !Timeout; return new Lease(this); }
        private sealed class Lease(Platform owner) : ICpuPmDriverMutex
        {
            private readonly int thread = Environment.CurrentManagedThreadId;
            public bool IsOwnedByCurrentThread => owner.MutexHeld && thread == Environment.CurrentManagedThreadId;
            public bool WasAbandoned => owner.Abandoned;
            public void ReleaseAndCloseOnCurrentThread() { Assert.Equal(thread, Environment.CurrentManagedThreadId); if (owner.FailRelease) { throw new IOException(); } owner.MutexHeld = false; }
        }
    }
    private sealed class Driver : ICpuPmCreatedDriver
    {
        internal string? Tag; internal bool Config = true, FailTag, FailStart, FailStop, FailDelete, StopPending;
        internal CpuPmDriverState State = CpuPmDriverState.Stopped; internal int Starts, Stops, Deletes, Closes; internal Action? OnStart, OnStop;
        public CpuPmDriverSnapshot ReadFresh() => new(true, Config, Config, Tag, State);
        public bool WriteNewOwnerTagAndFlush(CpuPmDriverOwnerTag tag) { if (FailTag) { return false; } Tag = tag.Value; return true; }
        public bool StartOnce() { Starts++; OnStart?.Invoke(); State = CpuPmDriverState.Running; return !FailStart; }
        public bool StopOnce() { Stops++; OnStop?.Invoke(); State = StopPending ? CpuPmDriverState.StopPending : CpuPmDriverState.Stopped; return !FailStop; }
        public bool DeleteOnce() { Deletes++; return !FailDelete; }
        public void CloseAfterDelete() => Closes++;
    }
    private sealed class Child : ICpuPmRetainedChild
    {
        public CpuPmAuthorityPeer Identity { get; } = new(32, 200, @"C:\pinned\child.exe", new('B', 64));
        internal CpuPmAuthorityPeer? Current; internal CpuPmChildState State = CpuPmChildState.Running;
        internal Child() => Current = Identity;
        public CpuPmAuthorityPeer? ReadFreshIdentity() => Current;
        public CpuPmChildState ReadState() => State;
    }
}
