namespace OpenBlade.Deployment;

// Storage injection stays internal to the owning adapter. Production consumers
// use the exact protected roots and their existing security policies.
[System.Runtime.Versioning.SupportedOSPlatform("windows")]
internal sealed record ManagedMailboxStorage(Func<bool> LegacyPresent,
    Func<WindowsNativeEpochFiles> OpenLegacy, Func<WindowsNativeEpochFiles> OpenManaged)
{
    internal static ManagedMailboxStorage Product { get; } = new(
        () => WindowsNativeEpochFiles.IsPresentDirectory(WindowsNativeEpochFiles.LegacyManagedHandoffRoot),
        () => WindowsNativeEpochFiles.OpenProtected(WindowsNativeEpochFiles.LegacyManagedHandoffRoot),
        WindowsNativeEpochFiles.CreateOrOpenManagedHandoff);
}
