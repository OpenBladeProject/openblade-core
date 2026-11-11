namespace OpenBlade.Deployment;

internal enum NativeInstallationState { VerifiedBootstrap, Selected, InterruptedPublication, UnresolvedHistory }
internal enum NativeRecoveryReason
{
    UnknownBootstrapHistory, InterruptedPublication, LegacyLaunchEvidenceUnavailable,
    UnknownAttemptHistory, ActiveOrUnknownCustody, MissingAttemptDrain, FirmwareOwnerUnresolved, PreStartTransitionInterrupted,
    RetirementPublicationFailed, AttemptBudgetExhausted, AttemptHistoryOverLimit
}
internal sealed class NativeRecoveryException(NativeRecoveryReason reason, string message, Exception? inner = null) : IOException(message, inner)
{ internal NativeRecoveryReason Reason { get; } = reason; }
internal sealed record NativeInstallationInspection(NativeInstallationState State, byte[]? Selection);

[System.Runtime.Versioning.SupportedOSPlatform("windows")]
internal static class WindowsNativeRecoveryState
{
    // Caller retains the root and exclusive epoch writer. Null current is not an
    // invitation to reset counters, reservations, mappings or unknown history.
    internal static NativeInstallationInspection Inspect(WindowsNativeEpochFiles files)
    {
        byte[]? selected = files.ReadCurrent();
        byte[]? frontier = WindowsNativeEpochTransaction.ReadFrontier(files);
        if (frontier is not null)
        {
            if (selected is not null && selected.AsSpan().SequenceEqual(frontier))
            { return new(NativeInstallationState.Selected, selected); }
            return new(NativeInstallationState.InterruptedPublication, frontier);
        }
        if (selected is not null) { return new(NativeInstallationState.UnresolvedHistory, selected); }
        using (var marker = files.OpenFile(Path.Combine(files.Root, "root.bin")))
        { WindowsNativeEpochFiles.RequireExact(marker, NativeCanonical.Lines("OpenBlade.NativeRoot.v1", "legacy-store-absent")); }
        string[] names = Directory.EnumerateFileSystemEntries(files.Root).Select(Path.GetFileName).Order(StringComparer.Ordinal).ToArray()!;
        if (!names.SequenceEqual(new[] { "owner", "root.bin", "writer.lock" }))
        { return new(NativeInstallationState.UnresolvedHistory, null); }
        using var owner = WindowsNativeTrustLifecycle.AcquireRetiredOwner(files);
        if (Directory.EnumerateFileSystemEntries(Path.Combine(files.Root, "owner")).Any(p => Path.GetFileName(p) != "writer.lock"))
        { return new(NativeInstallationState.UnresolvedHistory, null); }
        return new(NativeInstallationState.VerifiedBootstrap, null);
    }
}
