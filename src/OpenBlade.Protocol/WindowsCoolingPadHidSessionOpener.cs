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
        List<(OpenedCandidateHandle Handle, string Path)> candidates = [];
        try
        {
            HardwareAccessDecision access = guard.Check();
            if (access.DetectionFailure is not null) { return new(null, HidSessionFailureKind.ConflictDetectionFailed); }
            if (access.Conflicts.Count != 0) { return new(null, HidSessionFailureKind.ConflictBlocked); }

            foreach (string path in CandidatePaths())
            {
                var opened = native.TryOpen(path, WindowsRazerHidEvidenceEvaluator.OperationalOpenParameters);
                // Preserve the established HID access-denied fallback before any
                // firmware I/O: feature IOCTLs can use a zero-desired-access handle.
                if (opened.Candidate is null && opened.Win32Error == 5)
                { opened = native.TryOpen(path, WindowsRazerHidEvidenceEvaluator.MetadataOnlyOpenParameters); }
                if (opened.Candidate is not { } candidate) { return new(null, HidSessionFailureKind.IdentityBlocked); }
                candidates.Add((candidate, path));
                if (!native.ReadIdentity(candidate).MatchesExpected(WindowsCoolingPadDeviceProvider.ExactIdentity))
                { return new(null, HidSessionFailureKind.IdentityBlocked); }
            }
            if (candidates.Count != 1) { return new(null, HidSessionFailureKind.IdentityBlocked); }
            var selected = candidates[0];
            var handle = native.BindSelected(selected.Handle);
            SelectedPath = selected.Path;
            return new(handle, null);
        }
        catch { return new(null, HidSessionFailureKind.NativeIoFailure); }
        finally
        {
            // Binding transfers ownership; disposing that candidate is then a no-op.
            foreach (var candidate in candidates) { candidate.Handle.Dispose(); }
        }
    }

    internal bool StillExact()
    {
        try
        {
            string[] paths = CandidatePaths();
            if (paths.Length != 1 || (SelectedPath is { } selected
                && !string.Equals(paths[0], selected, StringComparison.OrdinalIgnoreCase))) { return false; }
            using var handle = native.TryOpen(paths[0], WindowsRazerHidEvidenceEvaluator.MetadataOnlyOpenParameters).Candidate;
            return handle is not null
                && native.ReadIdentity(handle).MatchesExpected(WindowsCoolingPadDeviceProvider.ExactIdentity);
        }
        catch { return false; }
    }

    private string[] CandidatePaths() => native.EnumerateDevicePaths()
        .Where(path => path.Contains("vid_1532&pid_0f43&", StringComparison.OrdinalIgnoreCase)
            && (path.Contains("&mi_00#", StringComparison.OrdinalIgnoreCase)
                || path.Contains("&mi_00&", StringComparison.OrdinalIgnoreCase))).ToArray();
}
