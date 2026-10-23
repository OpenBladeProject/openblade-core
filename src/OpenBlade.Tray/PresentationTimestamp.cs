namespace OpenBlade.Tray;

/// <summary>Local times in the app; fixed UTC in disconnected localization captures.</summary>
internal static class PresentationTimestamp
{
#if OPENBLADE_DEVELOPER_HARNESS
    private static readonly bool SnapshotMode = !string.IsNullOrWhiteSpace(
        Environment.GetEnvironmentVariable("OPENBLADE_LOCALIZATION_SNAPSHOT_DIR"));
#else
    private const bool SnapshotMode = false;
#endif

    internal static DateTimeOffset ForDisplay(DateTimeOffset value) => ForDisplay(value, SnapshotMode);

    internal static DateTimeOffset ForDisplay(DateTimeOffset value, bool snapshotMode) =>
        snapshotMode ? value.ToUniversalTime() : value.ToLocalTime();
}
