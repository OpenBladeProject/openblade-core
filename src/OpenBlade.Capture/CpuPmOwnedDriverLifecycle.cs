namespace OpenBlade.Capture;

internal enum CpuPmDriverPhase { New, Created, Tagged, Running, LaunchPossible, ChildBound, StopRequested, Deleted, Cleaned, Blocked }
internal enum CpuPmDriverCleanup { Complete, PendingStopped, Refused }
internal enum CpuPmDriverState { Unknown = 0, Stopped = 1, StopPending = 3, Running = 4 }
internal enum CpuPmChildState { Unknown, Running, Terminal }
internal enum CpuPmDriverEvent { CreateIntent, Created, TagIntent, Tagged, StartIntent, Started, ChildLaunchIntent, ChildBound, StopIntent, Stopped, DeleteIntent, Deleted, AbsenceVerified }

internal sealed record CpuPmDriverAbsence(int ScmOpenError, bool Registry64Absent, bool ActiveScanComplete, int MatchingActiveDrivers)
{
    internal bool IsAbsent => ScmOpenError == 1060 && Registry64Absent && ActiveScanComplete && MatchingActiveDrivers == 0;
}
internal sealed record CpuPmDriverSnapshot(bool RetainedHandle, bool ScmConfigurationExact, bool RegistryConfigurationExact,
    string? OwnerTag, CpuPmDriverState State)
{
    internal bool Owned(string? tag) => RetainedHandle && ScmConfigurationExact && RegistryConfigurationExact && OwnerTag == tag;
}
internal sealed record CpuPmDriverSafetySnapshot(bool ExactRz090528Package1, bool HealthyUniqueUsb,
    bool AcPower, bool LocalActiveDefaultSession, bool ControllersAbsent, bool VendorAndPayloadPinsHeld, bool TrustedOperatorPremiseAccepted)
{
    internal bool Admits => ExactRz090528Package1 && HealthyUniqueUsb && AcPower && LocalActiveDefaultSession
        && ControllersAbsent && VendorAndPayloadPinsHeld && TrustedOperatorPremiseAccepted;
}
internal sealed record CpuPmDriverOwnerTag
{
    internal Guid Run { get; }
    internal Guid Lifetime { get; }
    internal string Value { get; }
    internal CpuPmDriverOwnerTag(Guid run, Guid lifetime, string payloadSha256, string nonce)
    {
        if (run == Guid.Empty || lifetime == Guid.Empty || payloadSha256 is not { Length: 64 }
            || payloadSha256.Any(c => c is not (>= '0' and <= '9' or >= 'A' and <= 'F'))
            || nonce is not { Length: 64 } || nonce.Any(c => c is not (>= '0' and <= '9' or >= 'a' and <= 'f')))
        { throw new ArgumentException("Canonical run, lifetime, payload and random ownership nonce required."); }
        Run = run; Lifetime = lifetime; Value = $"1:{run:N}:{lifetime:N}:{payloadSha256}:{nonce}";
    }
}
internal sealed record CpuPmDriverLifecycleRecord(int Sequence, CpuPmDriverEvent Event, CpuPmDriverOwnerTag Owner);
internal interface ICpuPmDriverJournal { bool AppendAndFlush(CpuPmDriverLifecycleRecord record); }
// Safety-only evidence for an already-owned live driver. This type cannot admit
// lifecycle mutations, and cannot establish driver ownership by itself.
internal sealed record CpuPmDriverProofSafetySnapshot(bool InitialFullAdmission, bool VendorAndPayloadPinsHeld,
    bool OwnerContinuity, bool TrustedOperatorPremiseAccepted)
{
    internal bool Admits => InitialFullAdmission && VendorAndPayloadPinsHeld && OwnerContinuity && TrustedOperatorPremiseAccepted;
}
internal interface ICpuPmDriverSafety
{
    CpuPmDriverSafetySnapshot ReadFresh();
    CpuPmDriverProofSafetySnapshot ReadFreshProof();
    // Must root this owner AND all its host/payload/driver pins until explicit
    // verified cleanup. Cancellation, lost pipe or an unreturned call never frees them.
    void RetainUntilVerifiedCleanup(CpuPmOwnedDriverLifecycle owner);
    void ReleaseAfterVerifiedCleanup();
}
internal interface ICpuPmDriverMutex
{
    bool IsOwnedByCurrentThread { get; }
    bool WasAbandoned { get; }
    void ReleaseAndCloseOnCurrentThread();
}
internal interface ICpuPmCreatedDriver
{
    CpuPmDriverSnapshot ReadFresh();
    bool WriteNewOwnerTagAndFlush(CpuPmDriverOwnerTag tag);
    bool StartOnce();
    bool StopOnce();
    bool DeleteOnce();
    // No IDisposable/finalizer cleanup. This is callable only after Delete returned.
    void CloseAfterDelete();
}
internal interface ICpuPmDriverLifecyclePlatform
{
    ICpuPmDriverMutex AcquireMutex(CancellationToken token);
    CpuPmDriverAbsence ReadIndependentAbsence();
    ICpuPmCreatedDriver? CreateFixedDriver();
}
internal interface ICpuPmRetainedChild
{
    CpuPmAuthorityPeer Identity { get; }
    CpuPmAuthorityPeer? ReadFreshIdentity();
    CpuPmChildState ReadState();
}

// Synchronous lifecycle calls must run on one supervisor-owned thread. There is
// no Task.Run/timeout replacement or automatic disposal here. The caller retains
// and observes that SAME invocation until it returns, even after RevokeAuthority.
// No native composition exists: safety, durable journal, independent active-driver
// scan and retained-child launch are mandatory future integration boundaries.
internal sealed class CpuPmOwnedDriverLifecycle : ICpuPmOwnedDriverProofSource
{
    private readonly CpuPmDriverOwnerTag owner;
    private readonly ICpuPmDriverLifecyclePlatform platform;
    private readonly ICpuPmDriverSafety safety;
    private readonly ICpuPmDriverJournal journal;
    private readonly ICpuPmAuthorityClock clock;
    private ICpuPmCreatedDriver? driver;
    private ICpuPmDriverMutex? heldMutex;
    private ICpuPmRetainedChild? child;
    private CpuPmAuthorityPeer? childIdentity;
    private TimeSpan previous, stopStarted;
    private int busy, blocked, revoked, inFlight, sequence, successfulStarts, stopPolls;
    private CpuPmDriverPhase phase;
    private bool absenceObserved, retained, launchPossible, cleanupMode;
    internal CpuPmDriverPhase Phase => Volatile.Read(ref blocked) != 0 ? CpuPmDriverPhase.Blocked : phase;
    internal bool MutationInFlight => Volatile.Read(ref inFlight) != 0;
    internal CpuPmOwnedDriverLifecycle(CpuPmDriverOwnerTag owner, ICpuPmDriverLifecyclePlatform platform,
        ICpuPmDriverSafety safety, ICpuPmDriverJournal journal, ICpuPmAuthorityClock clock)
    {
        this.owner = owner ?? throw new ArgumentNullException(nameof(owner));
        this.platform = platform ?? throw new ArgumentNullException(nameof(platform));
        this.safety = safety ?? throw new ArgumentNullException(nameof(safety));
        this.journal = journal ?? throw new ArgumentNullException(nameof(journal));
        this.clock = clock ?? throw new ArgumentNullException(nameof(clock)); previous = clock.Now;
    }
    internal void RevokeAuthority() => Interlocked.Exchange(ref revoked, 1);
    // A timed-out synchronous call is still running. This irreversible latch
    // forbids later mutations even if that same call eventually reports success.
    internal void MarkUncertain() { RevokeAuthority(); Interlocked.Exchange(ref blocked, 1); }

    internal bool PrepareAndStart(CancellationToken token)
    {
        if (!Enter()) { return false; }
        ICpuPmDriverMutex? mutex = null;
        try
        {
            if (phase != CpuPmDriverPhase.New || !Guard(token)) { return Fail(); }
            safety.RetainUntilVerifiedCleanup(this); retained = true;
            mutex = platform.AcquireMutex(token);
            heldMutex = mutex;
            if (!MutexReady(mutex) || !Guard(token) || !platform.ReadIndependentAbsence().IsAbsent || !Guard(token)) { return Fail(); }
            absenceObserved = true;
            if (!Persist(CpuPmDriverEvent.CreateIntent, token) || !Guard(token)
                || !platform.ReadIndependentAbsence().IsAbsent || !Guard(token)) { return Fail(); }
            driver = Mutate(platform.CreateFixedDriver);
            if (driver is null || !Guard(token)) { return Fail(); }
            phase = CpuPmDriverPhase.Created;
            if (!Owned(CpuPmDriverState.Stopped, null, token) || !Persist(CpuPmDriverEvent.Created, token)
                || !Persist(CpuPmDriverEvent.TagIntent, token) || !Owned(CpuPmDriverState.Stopped, null, token)
                || !Mutate(() => driver.WriteNewOwnerTagAndFlush(owner)) || !Guard(token)) { return Fail(); }
            phase = CpuPmDriverPhase.Tagged;
            if (!Owned(CpuPmDriverState.Stopped, owner.Value, token) || !Persist(CpuPmDriverEvent.Tagged, token)
                || !Persist(CpuPmDriverEvent.StartIntent, token) || !Owned(CpuPmDriverState.Stopped, owner.Value, token)
                || !Mutate(driver.StartOnce) || !Guard(token)) { return Fail(); }
            successfulStarts = 1;
            if (!Owned(CpuPmDriverState.Running, owner.Value, token) || !Persist(CpuPmDriverEvent.Started, token)) { return Fail(); }
            phase = CpuPmDriverPhase.Running;
            bool released = ReleaseMutex(mutex); mutex = null;
            if (!released) { return Fail(); }
            return true;
        }
        catch (Exception) { return Fail(); }
        finally { ReleaseMutex(mutex); Exit(); }
    }

    // Call before even attempting Process.Start. Once recorded, lack of a child
    // handle is UNKNOWN, never evidence that no child started. No retry API exists.
    internal bool BeginChildLaunch(CancellationToken token)
    {
        if (!Enter()) { return false; }
        try
        {
            if (phase != CpuPmDriverPhase.Running || launchPossible || !Owned(CpuPmDriverState.Running, owner.Value, token)
                || !Persist(CpuPmDriverEvent.ChildLaunchIntent, token) || !Owned(CpuPmDriverState.Running, owner.Value, token)) { return Fail(); }
            launchPossible = true; phase = CpuPmDriverPhase.LaunchPossible; return true;
        }
        catch (Exception) { return Fail(); }
        finally { Exit(); }
    }
    internal bool BindStartedChild(ICpuPmRetainedChild retainedChild, CancellationToken token)
        => BindStartedChildCore(retainedChild, token, allowTerminal: false);
    // The voltage workload may finish during the full safety guards. Bind its
    // retained identity for completion without requesting a live authority proof.
    internal bool BindStartedChildForCompletion(ICpuPmRetainedChild retainedChild, CancellationToken token)
        => BindStartedChildCore(retainedChild, token, allowTerminal: true);
    private bool BindStartedChildCore(ICpuPmRetainedChild retainedChild, CancellationToken token, bool allowTerminal)
    {
        if (!Enter()) { return false; }
        try
        {
            if (phase != CpuPmDriverPhase.LaunchPossible || !Owned(CpuPmDriverState.Running, owner.Value, token)) { return Fail(); }
            child = retainedChild; childIdentity = retainedChild.Identity;
            if (!BindingChildReady(allowTerminal) || !Guard(token) || !Persist(CpuPmDriverEvent.ChildBound, token)) { return Fail(); }
            phase = CpuPmDriverPhase.ChildBound; return true;
        }
        catch (Exception) { return Fail(); }
        finally { Exit(); }
    }
    public CpuPmOwnedDriverProof? ReadFresh(Guid runId, Guid lifetimeId)
    {
        if (Volatile.Read(ref revoked) != 0) { return null; }
        if (!Enter()) { return null; }
        try
        {
            if (Volatile.Read(ref revoked) != 0 || runId != owner.Run || lifetimeId != owner.Lifetime
                || phase != CpuPmDriverPhase.ChildBound || successfulStarts != 1 || !absenceObserved || !retained
                || !ChildIs(CpuPmChildState.Running) || !OwnedForProof()
                || !ChildIs(CpuPmChildState.Running) || !ProofGuard()) { Fail(); return null; }
            return new(owner.Run, owner.Lifetime, true, true, successfulStarts, true, true, true, true, true, true, true);
        }
        catch (Exception) { Fail(); return null; }
        finally { Exit(); }
    }

    // Explicit attended cleanup only. PendingStopped permits bounded read-only
    // polling of the SAME stop; it never sends STOP again. Native false/exception,
    // cancellation, drift or missing child identity permanently blocks mutation.
    internal CpuPmDriverCleanup Cleanup(CancellationToken token)
    {
        RevokeAuthority();
        if (!Enter()) { return CpuPmDriverCleanup.Refused; }
        ICpuPmDriverMutex? mutex = null;
        try
        {
            cleanupMode = true;
            if (phase is not (CpuPmDriverPhase.Running or CpuPmDriverPhase.ChildBound or CpuPmDriverPhase.StopRequested)
                || !CleanupChildReady() || !Guard(token)) { return Refuse(); }
            mutex = platform.AcquireMutex(token);
            heldMutex = mutex;
            if (!MutexReady(mutex) || !CleanupChildReady() || !Guard(token)) { return Refuse(); }
            if (phase != CpuPmDriverPhase.StopRequested)
            {
                if (!Owned(CpuPmDriverState.Running, owner.Value, token) || !Persist(CpuPmDriverEvent.StopIntent, token)
                    || !CleanupChildReady() || !Owned(CpuPmDriverState.Running, owner.Value, token)
                    || !Mutate(driver!.StopOnce) || !Guard(token)) { return Refuse(); }
                phase = CpuPmDriverPhase.StopRequested; stopStarted = clock.Now;
            }
            if (++stopPolls > 64 || clock.Now - stopStarted >= TimeSpan.FromSeconds(10) || !Guard(token)) { return Refuse(); }
            var stopped = driver!.ReadFresh();
            if (!stopped.Owned(owner.Value) || !Guard(token) || !CleanupChildReady()) { return Refuse(); }
            if (stopped.State == CpuPmDriverState.StopPending)
            {
                bool released = ReleaseMutex(mutex); mutex = null;
                if (!released) { return Refuse(); }
                return CpuPmDriverCleanup.PendingStopped;
            }
            if (stopped.State != CpuPmDriverState.Stopped || !Persist(CpuPmDriverEvent.Stopped, token)
                || !Persist(CpuPmDriverEvent.DeleteIntent, token) || !CleanupChildReady()
                || !Owned(CpuPmDriverState.Stopped, owner.Value, token) || !Mutate(driver.DeleteOnce) || !Guard(token)) { return Refuse(); }
            phase = CpuPmDriverPhase.Deleted;
            if (!Persist(CpuPmDriverEvent.Deleted, token)) { return Refuse(); }
            driver.CloseAfterDelete();
            if (!Guard(token) || !CleanupChildReady() || !platform.ReadIndependentAbsence().IsAbsent || !Guard(token)
                || !Persist(CpuPmDriverEvent.AbsenceVerified, token)) { return Refuse(); }
            bool cleanupReleased = ReleaseMutex(mutex); mutex = null;
            if (!cleanupReleased) { return Refuse(); }
            phase = CpuPmDriverPhase.Cleaned;
            safety.ReleaseAfterVerifiedCleanup(); retained = false;
            return CpuPmDriverCleanup.Complete;
        }
        catch (Exception) { return Refuse(); }
        finally { ReleaseMutex(mutex); cleanupMode = false; Exit(); }
    }
    private bool CleanupChildReady() => !launchPossible ? child is null : ChildIs(CpuPmChildState.Terminal);
    private bool BindingChildReady(bool allowTerminal) => !allowTerminal ? ChildIs(CpuPmChildState.Running)
        : childIdentity is { IsValid: true } && child is not null && child.ReadFreshIdentity() == childIdentity
        && child.ReadState() is CpuPmChildState.Running or CpuPmChildState.Terminal && child.ReadFreshIdentity() == childIdentity;
    private bool ChildIs(CpuPmChildState state) => childIdentity is { IsValid: true } && child is not null
        && child.ReadFreshIdentity() == childIdentity && child.ReadState() == state && child.ReadFreshIdentity() == childIdentity;
    private bool Owned(CpuPmDriverState state, string? tag, CancellationToken token)
    {
        if (!Guard(token) || driver is null) { return false; }
        var value = driver.ReadFresh();
        return value.Owned(tag) && value.State == state && Guard(token);
    }
    private bool OwnedForProof()
    {
        if (!ProofGuard() || driver is null) { return false; }
        var value = driver.ReadFresh();
        return value.Owned(owner.Value) && value.State == CpuPmDriverState.Running && ProofGuard();
    }
    private bool ProofGuard()
    {
        if (Volatile.Read(ref blocked) != 0 || Volatile.Read(ref revoked) != 0 || !ClockValid()) { return false; }
        return safety.ReadFreshProof().Admits && Volatile.Read(ref blocked) == 0 && Volatile.Read(ref revoked) == 0 && ClockValid();
    }
    // Only full admission can precede Create/Tag/Start/Stop/Delete or durable
    // lifecycle progress. Cheap proof safety has no route into this guard.
    private bool Guard(CancellationToken token)
    {
        if (Volatile.Read(ref blocked) != 0 || (!cleanupMode && Volatile.Read(ref revoked) != 0) || token.IsCancellationRequested || !ClockValid()) { return false; }
        return safety.ReadFresh().Admits && !token.IsCancellationRequested && Volatile.Read(ref blocked) == 0
            && (cleanupMode || Volatile.Read(ref revoked) == 0) && ClockValid();
    }
    private bool ClockValid() { var now = clock.Now; if (now < previous) { return false; } previous = now; return true; }
    private bool Persist(CpuPmDriverEvent kind, CancellationToken token) => Guard(token)
        && journal.AppendAndFlush(new(++sequence, kind, owner)) && Guard(token);
    private T Mutate<T>(Func<T> action)
    {
        Volatile.Write(ref inFlight, 1);
        try { return action(); }
        finally { Volatile.Write(ref inFlight, 0); }
    }
    private bool Enter() { if (Interlocked.CompareExchange(ref busy, 1, 0) == 0) { return true; } MarkUncertain(); return false; }
    private void Exit() => Volatile.Write(ref busy, 0);
    private bool Fail() { MarkUncertain(); return false; }
    private CpuPmDriverCleanup Refuse() { Fail(); return CpuPmDriverCleanup.Refused; }
    private static bool MutexReady(ICpuPmDriverMutex value) => value.IsOwnedByCurrentThread && !value.WasAbandoned;
    private bool ReleaseMutex(ICpuPmDriverMutex? mutex)
    {
        if (mutex is null) { return true; }
        try
        {
            mutex.ReleaseAndCloseOnCurrentThread();
            if (ReferenceEquals(heldMutex, mutex)) { heldMutex = null; }
            return true;
        }
        catch (Exception) { MarkUncertain(); return false; }
    }
}
