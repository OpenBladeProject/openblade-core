using OpenBlade.Protocol;

namespace OpenBlade.Capture;

internal interface ICpuPmParentVendorLease
{
    void InitializeAndVerify(CancellationToken token);
    void AssertHeld();
    void BindOwner(CpuPmOwnedDriverLifecycle owner);
    void ReleaseAfterVerifiedCleanup(CpuPmOwnedDriverLifecycle owner);
}

// Three immutable signed files. Signature tools run once while the same file
// and ancestor handles deny writes/deletion; subsequent checks use those pins.
// No IDisposable/finalizer can release them while a native call is uncertain.
internal sealed class CpuPmParentVendorLease : ICpuPmParentVendorLease
{
    private readonly ICpuPmLaunchFiles pins;
    private readonly Func<IReadOnlyList<CpuBackendFileMetadata>> readMetadata;
    private IReadOnlyList<CpuBackendFileMetadata>? metadata;
    private CpuPmOwnedDriverLifecycle? owner;
    private bool begun, failed, released;
    internal CpuPmParentVendorLease(ICpuPmLaunchFiles pins, Func<IReadOnlyList<CpuBackendFileMetadata>> readMetadata)
    { this.pins = pins; this.readMetadata = readMetadata; }
    public void InitializeAndVerify(CancellationToken token)
    {
        if (begun || released || failed) { throw new IOException("Vendor lease already used."); }
        begun = true;
        try
        {
            token.ThrowIfCancellationRequested();
            CpuPmAuthenticatedChildRun.PinAncestors(pins, Rz090528InstalledCpuBackendInspector.DirectoryPath);
            pins.PinDirectory(Rz090528InstalledCpuBackendInspector.DirectoryPath, false, false);
            foreach (var expected in Rz090528InstalledCpuBackendInspector.ExpectedHashes)
            {
                token.ThrowIfCancellationRequested();
                var file = pins.PinFile(expected.Key, CpuPmLaunchSchema.MaximumFileBytes, false);
                if (file.Length <= 0 || file.Sha256 != expected.Value) { throw new IOException("Vendor pin mismatch."); }
            }
            token.ThrowIfCancellationRequested(); pins.AssertAllPins();
            var fresh = readMetadata();
            token.ThrowIfCancellationRequested();
            // Copy before validation; callers cannot alter the admitted vector.
            var copy = Array.AsReadOnly(fresh.ToArray());
            if (!WindowsRz090528CpuQueryAdapter.FilesExact(copy)) { throw new IOException("Vendor signature mismatch."); }
            pins.AssertAllPins(); token.ThrowIfCancellationRequested(); metadata = copy;
        }
        catch { failed = true; throw; }
    }
    public void AssertHeld()
    {
        if (failed || released || metadata is null) { throw new IOException("Vendor lease unavailable."); }
        try { pins.AssertAllPins(); }
        catch { failed = true; throw; }
    }
    public void BindOwner(CpuPmOwnedDriverLifecycle value)
    {
        if (owner is not null || value is null) { failed = true; throw new IOException("Vendor owner already bound."); }
        AssertHeld(); owner = value;
    }
    public void ReleaseAfterVerifiedCleanup(CpuPmOwnedDriverLifecycle value)
    {
        if (!ReferenceEquals(owner, value) || value.Phase != CpuPmDriverPhase.Cleaned || value.MutationInFlight)
        { throw new IOException("Exact verified vendor owner cleanup required."); }
        if (released) { return; }
        released = true; pins.Dispose();
    }
}
