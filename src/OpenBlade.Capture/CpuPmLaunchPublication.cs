using System.Security.Cryptography;

namespace OpenBlade.Capture;

internal interface ICpuPmLaunchRecordStore
{
    void Store(string name, byte[] bytes);
    void AssertPins();
}

// One-shot record reducer. Identities are supplied by retained OS observations
// in the native composition, never parsed from owner/child records.
internal sealed class CpuPmLaunchPublication
{
    private readonly CpuPmLaunchOwner owner;
    private readonly ICpuPmLaunchRecordStore store;
    private readonly string ownerHash;
    private bool childAttempted;
    internal CpuPmLaunchPublication(CpuPmLaunchOwner owner, ICpuPmLaunchRecordStore store)
    {
        this.owner = owner; this.store = store;
        byte[] bytes = CpuPmLaunchSchema.EncodeOwner(owner);
        ownerHash = Convert.ToHexString(SHA256.HashData(bytes));
        store.AssertPins(); store.Store("owner.json", bytes); store.AssertPins();
    }
    internal void PublishChild(ICpuPmRetainedChild retained, Action<ICpuPmRetainedChild> prepareAuthority)
    {
        if (childAttempted) { throw new InvalidOperationException("Child publication already attempted."); }
        childAttempted = true;
        ArgumentNullException.ThrowIfNull(prepareAuthority);
        var child = retained.ReadFreshIdentity();
        if (child is null || child != retained.Identity || retained.ReadState() != CpuPmChildState.Running
            || child.ProcessId == owner.Parent.ProcessId || child.ExecutablePath != owner.ChildExecutable
            || child.ExecutableSha256 != owner.ChildSha256) { throw new IOException("Child publication binding denied."); }
        byte[] bytes = CpuPmLaunchSchema.EncodeChild(new(owner.Run, owner.Lifetime, owner.Source, owner.Manifest,
            ownerHash, owner.Parent, child));
        store.AssertPins();
        // The owning lifecycle must bind this exact retained child and start its
        // authority listener before readiness can become visible. This callback
        // supplies ordering only; it cannot replace live SCM ownership proof.
        prepareAuthority(retained);
        store.AssertPins();
        if (retained.ReadFreshIdentity() != child || retained.ReadState() != CpuPmChildState.Running) { throw new IOException("Child changed before publication."); }
        store.Store("child.json", bytes); store.AssertPins();
        if (retained.ReadFreshIdentity() != child) { throw new IOException("Child changed after publication."); }
    }
}

internal interface ICpuPmLaunchStartupWait
{
    TimeSpan Elapsed { get; }
    bool ChildRecordPresent();
    Task Delay(CancellationToken token);
}

internal static class CpuPmLaunchStartupBarrier
{
    internal static readonly TimeSpan Budget = TimeSpan.FromSeconds(5);
    // Readiness is only scheduling. The consuming loader independently pins,
    // parses, hashes and validates the complete records/payload/process ancestry.
    internal static async Task<T?> AwaitThenLoad<T>(ICpuPmLaunchStartupWait wait,
        Func<T?> strictLoadOnce, CancellationToken token) where T : class
    {
        try
        {
            var started = wait.Elapsed; var previous = started;
            for (int polls = 0; polls < 201; polls++)
            {
                token.ThrowIfCancellationRequested(); var now = wait.Elapsed;
                if (now < previous || now - started >= Budget) { return null; }
                previous = now;
                if (wait.ChildRecordPresent()) { token.ThrowIfCancellationRequested(); return strictLoadOnce(); }
                await wait.Delay(token).ConfigureAwait(false);
            }
        }
        catch (Exception) { return null; }
        return null;
    }
}
