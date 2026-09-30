namespace OpenBlade.Capture;

// Full lifecycle guards must retain the live outer bootstrap at each boundary,
// not merely before/after a multi-mutation PrepareAndStart or Cleanup call.
// This decorator creates no ownership; the same concrete lifecycle remains owner.
internal sealed class CpuPmBootstrapBoundSafety : ICpuPmDriverSafety
{
    private readonly object gate = new();
    private readonly ICpuPmDriverSafety inner;
    private readonly Action assertBootstrap;
    private CpuPmOwnedDriverLifecycle? owner;
    private bool refused, released;
    internal CpuPmBootstrapBoundSafety(ICpuPmDriverSafety inner, Action assertBootstrap)
    {
        this.inner = inner ?? throw new ArgumentNullException(nameof(inner));
        this.assertBootstrap = assertBootstrap ?? throw new ArgumentNullException(nameof(assertBootstrap));
    }
    public CpuPmDriverSafetySnapshot ReadFresh()
    {
        lock (gate)
        {
            try
            {
                AssertAvailable(); var value = inner.ReadFresh(); AssertAvailable();
                if (!value.Admits) { throw new IOException("Full parent safety denied."); }
                return value;
            }
            catch (Exception) { refused = true; return new(false, false, false, false, false, false, false); }
        }
    }
    public CpuPmDriverProofSafetySnapshot ReadFreshProof()
    {
        lock (gate)
        {
            try
            {
                if (owner is null) { throw new IOException("Lifecycle owner unavailable."); }
                AssertAvailable(); var value = inner.ReadFreshProof(); AssertAvailable();
                if (!value.Admits) { throw new IOException("Parent proof safety denied."); }
                return value;
            }
            catch (Exception) { refused = true; return new(false, false, false, false); }
        }
    }
    public void RetainUntilVerifiedCleanup(CpuPmOwnedDriverLifecycle value)
    {
        lock (gate)
        {
            try
            {
                if (value is null || owner is not null) { throw new IOException("Exact owner may be bound once."); }
                AssertAvailable(); owner = value;
                inner.RetainUntilVerifiedCleanup(value); AssertAvailable();
            }
            catch (Exception) { refused = true; throw; }
        }
    }
    public void ReleaseAfterVerifiedCleanup()
    {
        lock (gate)
        {
            if (released) { return; }
            try
            {
                if (owner?.Phase != CpuPmDriverPhase.Cleaned || owner.MutationInFlight)
                { throw new IOException("Exact owner's verified cleanup required."); }
                AssertAvailable(); inner.ReleaseAfterVerifiedCleanup(); released = true;
            }
            catch (Exception) { refused = true; throw; }
        }
    }
    private void AssertAvailable()
    {
        if (refused || released) { throw new IOException("Parent bootstrap safety unavailable."); }
        assertBootstrap();
    }
}
