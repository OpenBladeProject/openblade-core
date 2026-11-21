namespace OpenBlade.Protocol;

// The accessory's own HID contract admits its handle. Laptop admission remains
// exclusively in the laptop openers and is not evidence for this USB device.
internal sealed class WindowsCoolingPadHidSessionOpener(IHidCandidateNative native)
{
    private readonly IHidCandidateNative native = native ?? throw new ArgumentNullException(nameof(native));
    internal string? SelectedPath { get; private set; }

    internal HidSessionResult<OpenedHidHandle> Open(IHardwareAccessGuard guard)
    {
        ArgumentNullException.ThrowIfNull(guard);
        OpenedCandidateHandle? candidate = null;
        try
        {
            HardwareAccessDecision access = guard.Check();
            if (access.DetectionFailure is not null) { return new(null, HidSessionFailureKind.ConflictDetectionFailed); }
            if (access.Conflicts.Count != 0) { return new(null, HidSessionFailureKind.ConflictBlocked); }

            if (ExactControlPaths() is not [string path]) { return new(null, HidSessionFailureKind.IdentityBlocked); }
            var opened = native.TryOpen(path, WindowsRazerHidEvidenceEvaluator.OperationalOpenParameters);
            // Preserve the established HID access-denied fallback before any
            // firmware I/O: feature IOCTLs can use a zero-desired-access handle.
            if (opened.Candidate is null && opened.Win32Error == 5)
            { opened = native.TryOpen(path, WindowsRazerHidEvidenceEvaluator.MetadataOnlyOpenParameters); }
            candidate = opened.Candidate;
            // Re-read the identity on the handle that will be bound; the classification handle is closed.
            if (candidate is null
                || !native.ReadIdentity(candidate).MatchesExpected(WindowsCoolingPadDeviceProvider.ExactIdentity))
            { return new(null, HidSessionFailureKind.IdentityBlocked); }
            var handle = native.BindSelected(candidate);
            SelectedPath = path;
            return new(handle, null);
        }
        catch { return new(null, HidSessionFailureKind.NativeIoFailure); }
        finally
        {
            // Binding transfers ownership; disposing that candidate is then a no-op.
            candidate?.Dispose();
        }
    }

    internal bool StillExact()
    {
        try
        {
            return ExactControlPaths() is [string path]
                && (SelectedPath is not { } selected || string.Equals(path, selected, StringComparison.OrdinalIgnoreCase));
        }
        catch { return false; }
    }

    // Classifies every MI_00 interface with a metadata-only handle. The firmware descriptor
    // admits one feature-bearing control collection; input-only siblings are skipped by the
    // same rule discovery uses and are never opened for I/O. Any unreadable or feature-bearing
    // non-exact MI_00 interface still blocks admission (null).
    private string[]? ExactControlPaths()
    {
        List<string> exact = [];
        foreach (string path in CandidatePaths())
        {
            using var handle = native.TryOpen(path, WindowsRazerHidEvidenceEvaluator.MetadataOnlyOpenParameters).Candidate;
            if (handle is null) { return null; }
            HidHandleIdentity identity = native.ReadIdentity(handle);
            if (identity.MatchesExpected(WindowsCoolingPadDeviceProvider.ExactIdentity)) { exact.Add(path); }
            else if (!WindowsCoolingPadDiscoverySource.IsInputOnlySibling(identity)) { return null; }
        }
        return [.. exact];
    }

    private string[] CandidatePaths() => native.EnumerateDevicePaths()
        .Where(path => path.Contains("vid_1532&pid_0f43&", StringComparison.OrdinalIgnoreCase)
            && (path.Contains("&mi_00#", StringComparison.OrdinalIgnoreCase)
                || path.Contains("&mi_00&", StringComparison.OrdinalIgnoreCase))).ToArray();
}
