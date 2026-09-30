using OpenBlade.Protocol;

namespace OpenBlade.Capture;

// Attended userspace admission. Trusted bootstrap/operator approval must be
// supplied independently by the reviewed supervisor, never parsed from JSON.
// This provider says nothing about ownership of a driver service.
internal sealed class CpuPmParentDriverSafety : ICpuPmDriverSafety
{
    private static int nativeClaimed;
    private static CpuPmParentDriverSafety? nativeRoot;
    private readonly object gate = new();
    private readonly CpuPmReviewedLaunchBootstrap trusted;
    private readonly ICpuPmLaunchFiles files;
    private readonly ICpuPmParentPassiveSource source;
    private readonly CpuPmParentContinuity continuity;
    private readonly bool operatorApproved;
    private IReadOnlyList<CpuPmLaunchFile>? manifest;
    private readonly ICpuPmParentVendorLease vendorPins;
    private CpuPmOwnedDriverLifecycle? owner;
    private bool ready, fullAdmitted, refused, released;
    private CpuPmParentDriverSafety(CpuPmReviewedLaunchBootstrap trusted, ICpuPmLaunchFiles files,
        ICpuPmParentPassiveSource source, ICpuPmContinuityPlatform continuity, ICpuPmParentVendorLease vendorPins, bool operatorApproved)
    {
        this.trusted = trusted; this.files = files; this.source = source;
        this.continuity = new(continuity); this.vendorPins = vendorPins; this.operatorApproved = operatorApproved;
    }
    internal static CpuPmParentDriverSafety? CreateNative(CpuPmReviewedLaunchBootstrap trusted,
        bool trustedOperatorPremiseApproved, CancellationToken token)
    {
        if (trusted is null || !trustedOperatorPremiseApproved || !OperatingSystem.IsWindows()
            || !Environment.Is64BitProcess || Interlocked.CompareExchange(ref nativeClaimed, 1, 0) != 0) { return null; }
        var result = new CpuPmParentDriverSafety(trusted, new WindowsCpuPmLaunchFiles(),
            WindowsRz090528CpuQueryAdapter.CreateParentPassiveSource(),
            WindowsCpuPmSessionContinuity.CreateParentPassivePlatform(), new CpuPmParentVendorLease(new WindowsCpuPmLaunchFiles(), WindowsInstalledCpuBackendSnapshotSource.ReadFileMetadata), true);
        // Root before callbacks or retained pins exist. Failed initialization
        // retains resources until exit; no failed factory permits a second run.
        nativeRoot = result;
        return result.Initialize(token) ? result : null;
    }
    internal static CpuPmParentDriverSafety? CreateForTesting(CpuPmReviewedLaunchBootstrap trusted,
        ICpuPmLaunchFiles files, ICpuPmParentPassiveSource source, ICpuPmContinuityPlatform continuity,
        bool operatorApproved, ICpuPmParentVendorLease vendorPins, CancellationToken token = default)
    {
        var value = new CpuPmParentDriverSafety(trusted, files, source, continuity, vendorPins, operatorApproved);
        return value.Initialize(token) ? value : null;
    }
    private bool Initialize(CancellationToken token)
    {
        try
        {
            token.ThrowIfCancellationRequested();
            if (!operatorApproved || !continuity.Arm()) { return Deny(); }
            token.ThrowIfCancellationRequested();
            vendorPins.InitializeAndVerify(token);
            token.ThrowIfCancellationRequested();
            CpuPmAuthenticatedChildRun.PinAncestors(files, trusted.PayloadPath);
            files.PinDirectory(trusted.PayloadPath, true, true);
            var pin = files.PinFile(Path.Combine(trusted.PayloadPath, "manifest.json"), CpuPmLaunchSchema.ManifestLimit, true);
            manifest = CpuPmLaunchSchema.ParseManifest(pin.ReadBounded(CpuPmLaunchSchema.ManifestLimit), trusted);
            CpuPmAuthenticatedChildRun.ValidatePayload(files, trusted.PayloadPath, manifest, token);
            token.ThrowIfCancellationRequested(); ready = true;
            if (!ReadFresh().Admits) { return Deny(); }
            token.ThrowIfCancellationRequested(); return true;
        }
        catch { return Deny(); }
    }
    public CpuPmDriverSafetySnapshot ReadFresh()
    {
        lock (gate)
        {
            if (!ready || refused || released || !operatorApproved || manifest is null) { return Unavailable(); }
            try
            {
                if (!continuity.ReadFresh()) { return Refuse(); }
                CpuPmAuthenticatedChildRun.ValidatePayload(files, trusted.PayloadPath, manifest, CancellationToken.None);
                if (!continuity.ReadFresh()) { return Refuse(); }
                vendorPins.AssertHeld();
                CpuPmParentPassiveEvidence fresh = source.ReadFresh();
                // Revalidate callbacks/session/tick after synchronous host,
                // PnP and controller work before admitting a step.
                if (!continuity.ReadFresh()) { return Refuse(); }
                files.AssertAllPins();
                if (!continuity.ReadFresh()) { return Refuse(); }
                if (!fresh.ExactHostAndPackage || !fresh.HealthyUniqueUsb || !fresh.ControllersAbsent) { return Refuse(); }
                vendorPins.AssertHeld();
                if (!continuity.ReadFresh()) { return Refuse(); }
                fullAdmitted = true; return new(true, true, true, true, true, true, true);
            }
            catch { return Refuse(); }
        }
    }
    public CpuPmDriverProofSafetySnapshot ReadFreshProof()
    {
        lock (gate)
        {
            if (!ready || !fullAdmitted || refused || released || !operatorApproved || owner is null)
            { return new(false, false, false, false); }
            try
            {
                if (!continuity.ReadFresh()) { throw new IOException("Parent continuity interrupted."); }
                // Same deny-write/delete handles retain the initially reviewed
                // hashes/signatures. No WMI, PnP, service enumeration, signature
                // process or directory enumeration runs on a mailbox proof.
                vendorPins.AssertHeld(); files.AssertAllPins();
                if (!continuity.ReadFresh()) { throw new IOException("Parent continuity interrupted."); }
                return new(true, true, true, true);
            }
            catch { refused = true; return new(false, false, false, false); }
        }
    }
    private static CpuPmDriverSafetySnapshot Unavailable() => new(false, false, false, false, false, false, false);
    private CpuPmDriverSafetySnapshot Refuse() { refused = true; return Unavailable(); }
    private bool Deny() { lock (gate) { refused = true; return false; } }
    public void RetainUntilVerifiedCleanup(CpuPmOwnedDriverLifecycle value)
    {
        lock (gate)
        {
            if (value is null || owner is not null || !ReadFresh().Admits) { refused = true; throw new IOException("Parent safety cannot retain owner."); }
            vendorPins.BindOwner(value); owner = value;
            // Native factory already roots this provider. It now also retains
            // the exact lifecycle, including pending calls and child handles.
            GC.KeepAlive(nativeRoot);
        }
    }
    public void ReleaseAfterVerifiedCleanup()
    {
        lock (gate)
        {
            if (released) { return; }
            if (owner?.Phase != CpuPmDriverPhase.Cleaned || owner.MutationInFlight)
            { throw new IOException("Verified owned cleanup is required."); }
            // Lifecycle reached Cleaned only after its durable absence record.
            // No cancellation, failed guard, pipe loss or pending call gets here.
            released = true; ready = false;
            bool callbacksReleased = continuity.Release();
            try { vendorPins.ReleaseAfterVerifiedCleanup(owner); }
            finally { files.Dispose(); }
            if (!callbacksReleased) { throw new IOException("Parent callback cleanup is uncertain."); }
        }
    }
}
