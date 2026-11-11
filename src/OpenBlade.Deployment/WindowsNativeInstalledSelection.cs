namespace OpenBlade.Deployment;

// Cleanup keeps immutable package guards separate from replaceable selection.
// This is not firmware ownership or a drain certificate by itself.
[System.Runtime.Versioning.SupportedOSPlatform("windows")]
internal sealed class WindowsNativeInstalledSelection : IDisposable
{
    private readonly WindowsNativeEpochFiles files;
    private readonly List<IDisposable> held = [];
    private byte[] selected;
    private WindowsNativePackage? package;
    internal NativeEpochRecord Record { get; private set; }
    internal string ServicePath => Path.Combine(files.Root, Record.Epoch, "app", "OpenBlade.Service.exe");
    internal string AgentPath => Path.Combine(files.Root, Record.Epoch, "app", "OpenBlade.SessionAgent.exe");
    internal string ServiceSha256 => package?.Service.Sha256 ?? throw new ObjectDisposedException(nameof(WindowsNativeInstalledSelection));
    private WindowsNativeInstalledSelection(WindowsNativeEpochFiles files, byte[] selected)
    { this.files = files; this.selected = selected; Record = NativeEpochRecord.Parse(selected); }
    internal static async Task<WindowsNativeInstalledSelection?> OpenProductAsync(CancellationToken token)
    {
        if (!WindowsNativeEpochFiles.IsPresentDirectory(WindowsNativeEpochFiles.ProductRoot)) { return null; }
        var files = WindowsNativeEpochFiles.OpenProduct();
        try { return await OpenForSetupAsync(files, token); }
        catch { files.Dispose(); throw; }
    }
    internal static async Task<WindowsNativeInstalledSelection?> OpenForSetupAsync(WindowsNativeEpochFiles files, CancellationToken token)
    {
        var writer = files.AcquireWriter();
        try
        {
            var inspection = WindowsNativeRecoveryState.Inspect(files);
            if (inspection.State == NativeInstallationState.VerifiedBootstrap)
            { writer.Dispose(); files.Dispose(); return null; }
            if (inspection.State == NativeInstallationState.UnresolvedHistory)
            { throw new NativeRecoveryException(NativeRecoveryReason.UnknownBootstrapHistory, "Native selection is absent/inconsistent with preserved history; no bootstrap reset was performed."); }
            var value = await OpenWithWriterAsync(files, inspection.Selection!, writer, token,
                inspection.State == NativeInstallationState.InterruptedPublication);
            return value;
        }
        catch { writer.Dispose(); throw; }
    }
    internal static async Task<WindowsNativeInstalledSelection> OpenAsync(WindowsNativeEpochFiles files, CancellationToken token)
    {
        var writer = files.AcquireWriter();
        try { return await OpenWithWriterAsync(files, files.ReadCurrent() ?? throw new IOException("Native selection is unavailable."), writer, token, false); }
        catch { writer.Dispose(); throw; }
    }
    private static async Task<WindowsNativeInstalledSelection> OpenWithWriterAsync(WindowsNativeEpochFiles files, byte[] selected,
        FileStream writer, CancellationToken token, bool interrupted)
    {
        var value = new WindowsNativeInstalledSelection(files, selected);
        try
        {
            value.held.Add(writer);
            if (!interrupted) { value.RequireCurrent(); }
            string directory = Path.Combine(files.Root, value.Record.Epoch);
            value.held.Add(files.RetainDirectory(directory));
            value.held.Add(files.RetainDirectory(Path.Combine(directory, "app")));
            byte[] Read(string name, int max = 16384)
            {
                var stream = files.OpenFile(Path.Combine(directory, name)); value.held.Add(stream);
                return WindowsNativeEpochFiles.ReadBounded(stream, max);
            }
            byte[] phase = Read(value.Record.Phase + ".bin");
            if (!phase.AsSpan().SequenceEqual(selected)) { throw new InvalidDataException("Native phase differs."); }
            byte[] projection = Read("verification.bin", 2048);
            byte[] record = Read("package.bin");
            string[] fields = NativeCanonical.ParseLines(record, "OpenBlade.NativePackage.v2", 13);
            if (NativeCanonical.Digest(record) != value.Record.Package || fields[11] != NativeCanonical.Digest(projection))
            { throw new InvalidDataException("Native cleanup package commitment differs."); }
            var original = files.OpenFile(Path.Combine(directory, "package.exe"));
            value.package = await WindowsNativePackage.OpenLocalDevAsync(original, new NoLease(), Read("release.json", 4096), Read("release.sig", 64),
                NativeLocalDevEnrollment.FromProtectedProjection(projection), token);
            byte[] roles = Read("roles.bin", 4096);
            var origin = NativeEpochRecord.Parse(Read("origin.bin"));
            if (origin.Phase != NativeEpochPhase.Provisional || origin with { Phase = value.Record.Phase, Readiness = value.Record.Readiness } != value.Record
                || value.Record.ServiceId != value.package.Service.Id || value.Record.AgentId != value.package.Agent.Id
                || !roles.AsSpan().SequenceEqual(value.package.Inventory) || !value.package.Record.AsSpan().SequenceEqual(record)) { throw new InvalidDataException("Native cleanup original signed package differs."); }
            foreach (var role in new[] { ("OpenBlade.Service.exe", value.package.Service), ("OpenBlade.SessionAgent.exe", value.package.Agent) })
            {
                var stream = files.OpenFile(Path.Combine(directory, "app", role.Item1)); value.held.Add(stream);
                if (stream.Length != role.Item2.Size || Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(stream)) != role.Item2.Sha256)
                { throw new InvalidDataException("Native cleanup executable differs."); }
            }
            if (interrupted)
            {
                // Authenticate the exact signed package and phase above before
                // finishing metadata publication. This grants no drain authority.
                Recover(value.Record.Phase == NativeEpochPhase.Retired ? NativeRecoveryReason.RetirementPublicationFailed : NativeRecoveryReason.InterruptedPublication,
                    () =>
                    {
                        WindowsNativeEpochTransaction.RecoverUnresolvedPublicationUnderWriter(files, NativeCanonical.Digest(selected),
                            retirementAdmission: value.AcquireRetirementProof);
                        return true;
                    });
                value.selected = files.ReadCurrent()!;
                value.Record = NativeEpochRecord.Parse(value.selected);
            }
            value.RequireCurrent();
            return value;
        }
        catch { value.Dispose(); throw; }
    }
    internal void RequireCurrent()
    {
        string? frontier = Directory.EnumerateFiles(files.Root, "selection-*.bin").Order(StringComparer.Ordinal).LastOrDefault();
        if (frontier is null || !(files.ReadCurrent() ?? []).AsSpan().SequenceEqual(selected)) { throw new IOException("Native cleanup selection changed."); }
        using var held = files.OpenFile(frontier);
        WindowsNativeEpochFiles.RequireExact(held, selected);
        if (Path.GetFileName(frontier) != $"selection-{Record.Ordinal:D20}-{(int)Record.Phase}.bin") { throw new IOException("Native cleanup frontier differs."); }
    }
    internal void RetireAfterStatusDrain(uint pid, long created)
    {
        RequireCurrent();
        if (package!.Service.Features.Split(',').Contains("startup-custody-v1", StringComparer.Ordinal))
        { RetireRecoverableSelection(pid, created); return; }
        string directory = Path.Combine(files.Root, Record.Epoch);
        using var drain = files.OpenFile(Path.Combine(directory, $"status-drain-{pid}-{created}.bin"));
        WindowsNativeEpochFiles.RequireExact(drain, NativeCanonical.Lines("OpenBlade.NativeStatusDrain.v1", Record.Epoch,
            pid.ToString(System.Globalization.CultureInfo.InvariantCulture), created.ToString(System.Globalization.CultureInfo.InvariantCulture)));
        // Legacy recovery requires the independently retained live original and
        // actual exit at the caller. An absent process never enters this route.
        Recover(NativeRecoveryReason.RetirementPublicationFailed, () => { PublishRetirement(directory); return true; });
    }
    internal void RetireRecoverableSelection(uint? expectedPid = null, long? expectedCreated = null)
    {
        RequireCurrent();
        if (Record.Phase == NativeEpochPhase.Retired) { return; }
        string directory = Path.Combine(files.Root, Record.Epoch);
        if (!package!.Service.Features.Split(',').Contains("startup-custody-v1", StringComparer.Ordinal))
        { throw new NativeRecoveryException(NativeRecoveryReason.LegacyLaunchEvidenceUnavailable, "Legacy native stopped state lacks original launch/drain evidence; history was preserved."); }
        using var attempts = Recover(NativeRecoveryReason.UnknownAttemptHistory,
            () => files.RetainDirectory(Path.Combine(directory, "attempts")));
        // Sharing/ACL/content failures all mean active or unknown custody.
        using var custody = AcquireRecoveryCustody(Path.Combine(directory, "attempts", "custody.lock"));
        Recover(NativeRecoveryReason.ActiveOrUnknownCustody, () =>
        {
            WindowsNativeEpochFiles.RequireExact(custody, NativeCanonical.Lines("OpenBlade.NativeServiceCustody.v1", Record.Epoch));
            return true;
        });
        RequireCurrent();
        WindowsNativeTrustLifecycle.RequireOwnerRetired(files);
        byte[] proof = Recover(NativeRecoveryReason.UnknownAttemptHistory,
            () => WindowsNativeAttemptHistory.RequireDrained(files, Record, directory, package.Service.Features, expectedPid, expectedCreated));
        Recover(NativeRecoveryReason.RetirementPublicationFailed, () =>
        {
            PublishProofSnapshot(directory, proof);
            PublishRetirement(directory);
            return true;
        });
    }
    private IDisposable AcquireRetirementProof(NativeEpochRecord proofRecord)
    {
        string directory = Path.Combine(files.Root, Record.Epoch);
        var attempts = Recover(NativeRecoveryReason.UnknownAttemptHistory,
            () => files.RetainDirectory(Path.Combine(directory, "attempts")));
        FileStream? custody = null;
        try
        {
            custody = AcquireRecoveryCustody(Path.Combine(directory, "attempts", "custody.lock"));
            Recover(NativeRecoveryReason.ActiveOrUnknownCustody, () =>
            {
                WindowsNativeEpochFiles.RequireExact(custody, NativeCanonical.Lines("OpenBlade.NativeServiceCustody.v1", Record.Epoch));
                return true;
            });
            WindowsNativeTrustLifecycle.RequireOwnerRetired(files);
            byte[] proof = Recover(NativeRecoveryReason.UnknownAttemptHistory,
                () => WindowsNativeAttemptHistory.RequireDrained(files, proofRecord, directory, package!.Service.Features));
            Recover(NativeRecoveryReason.RetirementPublicationFailed, () => { PublishProofSnapshot(directory, proof); return true; });
            return new RecoveryCustody(attempts, custody);
        }
        catch { custody?.Dispose(); attempts.Dispose(); throw; }
    }
    private sealed class RecoveryCustody(IDisposable attempts, IDisposable custody) : IDisposable
    {
        public void Dispose() { try { custody.Dispose(); } finally { attempts.Dispose(); } }
    }
    private static T Recover<T>(NativeRecoveryReason reason, Func<T> operation)
    {
        try { return operation(); }
        catch (NativeRecoveryException) { throw; }
        catch (Exception error) when (error is InvalidDataException or IOException or UnauthorizedAccessException)
        { throw new NativeRecoveryException(reason, "Native recovery refused uncertain evidence or publication; original history was retained.", error); }
    }
    private void PublishProofSnapshot(string directory, byte[] proof)
    {
        // Snapshot history has a separate bound from attempts. Old snapshots are
        // immutable audit evidence, never admission for a later phase/attempt set.
        const int maximumSnapshots = 16384;
        string snapshots = Path.Combine(directory, "retirement-proofs");
        if (!WindowsNativeEpochFiles.IsPresentDirectory(snapshots)) { files.CreateDirectoryOnce(snapshots); }
        using var pinned = files.RetainDirectory(snapshots);
        string path = Path.Combine(snapshots, NativeCanonical.Digest(proof) + ".bin");
        bool found = false;
        int count = 0;
        foreach (string existing in Directory.EnumerateFileSystemEntries(snapshots))
        {
            if (++count > maximumSnapshots) { throw new InvalidDataException("Native retirement snapshot history exceeds its bound."); }
            using var held = files.OpenFile(existing);
            string name = Path.GetFileName(existing);
            // A power loss before rename may leave an unpublished partial file.
            // Preserve bounded canonical staging files; they prove no drain.
            if (name.Length == 44 && name.StartsWith("receipt-", StringComparison.Ordinal) && name.EndsWith(".tmp", StringComparison.Ordinal)
                && NativeCanonical.IsHex(name[8..40], 32))
            {
                if (held.Length > 2048) { throw new InvalidDataException("Unpublished retirement snapshot exceeds its bound."); }
                continue;
            }
            byte[] bytes = WindowsNativeEpochFiles.ReadBounded(held, 2048);
            string[] fields = NativeCanonical.ParseLines(bytes, "OpenBlade.NativeRetirementProof.v1", 7);
            if (Path.GetFileName(existing) != NativeCanonical.Digest(bytes) + ".bin" || fields[0] != Record.Epoch
                || !IsDigest(fields[1]) || !IsDigest(fields[2]) || !IsDigest(fields[5])
                || fields[3] is not ("submission-retained" or "never-submitted")
                || fields[4] is not ("no-admitted-metadata-attempt" or "all-metadata-attempts-drained"))
            { throw new InvalidDataException("Native retirement snapshot is malformed or differs from its content address."); }
            if (existing == path)
            {
                if (!bytes.AsSpan().SequenceEqual(proof)) { throw new InvalidDataException("Fresh retirement snapshot differs."); }
                found = true;
            }
        }
        if (!found)
        {
            if (count == maximumSnapshots) { throw new InvalidDataException("Native retirement snapshot budget is exhausted."); }
            files.Publish(path, proof);
        }
        using var current = files.OpenFile(path);
        WindowsNativeEpochFiles.RequireExact(current, proof);
    }
    private static bool IsDigest(string value) => value.Length == 64 && value.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');
    private FileStream AcquireRecoveryCustody(string path)
    {
        try { return files.AcquireServiceCustody(path); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        { throw new NativeRecoveryException(NativeRecoveryReason.ActiveOrUnknownCustody, "Native service custody is active or unavailable; recovery did not infer process exit or drain.", error); }
    }
    private void PublishRetirement(string directory)
    {
        WindowsNativeTrustLifecycle.RequireOwnerRetired(files);
        RequireCurrent();
        var retired = Record with { Phase = NativeEpochPhase.Retired, Readiness = new string('0', 64) };
        byte[] bytes = retired.Encode();
        string intent = Path.Combine(directory, "Retired.bin");
        if (PresentFile(intent)) { using var existing = files.OpenFile(intent); WindowsNativeEpochFiles.RequireExact(existing, bytes); }
        else { files.Publish(intent, bytes); }
        string frontier = Path.Combine(files.Root, $"selection-{Record.Ordinal:D20}-3.bin");
        if (PresentFile(frontier)) { using var existing = files.OpenFile(frontier); WindowsNativeEpochFiles.RequireExact(existing, bytes); }
        else { files.Publish(frontier, bytes); }
        files.PublishCurrent(bytes);
        if (!(files.ReadCurrent() ?? []).AsSpan().SequenceEqual(bytes)) { throw new IOException("Native retirement publication is unresolved."); }
        selected = bytes; Record = retired;
    }
    private static bool PresentFile(string path)
    {
        try { _ = File.GetAttributes(path); return true; }
        catch (FileNotFoundException) { return false; }
    }
    public void Dispose()
    {
        package?.Dispose(); package = null;
        for (int i = held.Count - 1; i >= 0; i--) { held[i].Dispose(); }
        held.Clear(); files.Dispose();
    }
    private sealed class NoLease : IDisposable { public void Dispose() { } }
}
