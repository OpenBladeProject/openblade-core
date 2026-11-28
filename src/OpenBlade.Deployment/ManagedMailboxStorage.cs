namespace OpenBlade.Deployment;

// Storage injection stays internal to the owning adapter. Production consumers
// use the exact protected roots and their existing security policies.
[System.Runtime.Versioning.SupportedOSPlatform("windows")]
internal sealed record ManagedMailboxStorage(Func<bool> LegacyPresent,
    Func<WindowsNativeEpochFiles> OpenLegacy, Func<WindowsNativeEpochFiles> OpenManaged)
{
    // Fixture identity comes from the retained private owner, never a path name
    // or a caller assertion. Refuse omitted dependencies before resolving roots;
    // the same adapters still authenticate explicit private storage normally.
    internal static ManagedMailboxStorage Resolve(ManagedMailboxNativeCustody native, ManagedMailboxStorage? storage,
        Func<ManagedMailboxStorage>? productResolver = null)
    {
        if (storage is not null) { return storage; }
        if (native.IsPrivateFixture) { throw new InvalidOperationException("Private mailbox fixtures require explicit storage."); }
        return (productResolver ?? (() => Product))();
    }

    internal static ManagedMailboxStorage Product { get; } = new(
        () => WindowsNativeEpochFiles.IsPresentDirectory(WindowsNativeEpochFiles.LegacyManagedHandoffRoot),
        () => WindowsNativeEpochFiles.OpenProtected(WindowsNativeEpochFiles.LegacyManagedHandoffRoot),
        WindowsNativeEpochFiles.CreateOrOpenManagedHandoff);
}
