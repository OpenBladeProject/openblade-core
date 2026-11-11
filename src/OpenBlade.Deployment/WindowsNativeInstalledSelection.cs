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
                WindowsNativeEpochTransaction.RecoverUnresolvedPublicationUnderWriter(files, NativeCanonical.Digest(selected));
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
        PublishRetirement(directory);
    }
    internal void RetireRecoverableSelection(uint? expectedPid = null, long? expectedCreated = null)
    {
        RequireCurrent();
        if (Record.Phase == NativeEpochPhase.Retired) { return; }
        string directory = Path.Combine(files.Root, Record.Epoch);
        if (!package!.Service.Features.Split(',').Contains("startup-custody-v1", StringComparer.Ordinal))
        { throw new NativeRecoveryException(NativeRecoveryReason.LegacyLaunchEvidenceUnavailable, "Legacy native stopped state lacks original launch/drain evidence; history was preserved."); }
        using var attempts = files.RetainDirectory(Path.Combine(directory, "attempts"));
        // A sharing violation is active/unknown custody, never an absent owner.
        using var custody = AcquireRecoveryCustody(Path.Combine(directory, "attempts", "custody.lock"));
        WindowsNativeEpochFiles.RequireExact(custody, NativeCanonical.Lines("OpenBlade.NativeServiceCustody.v1", Record.Epoch));
        RequireCurrent();
        WindowsNativeTrustLifecycle.RequireOwnerRetired(files);
        byte[] proof;
        try { proof = WindowsNativeAttemptHistory.RequireDrained(files, Record, directory, package.Service.Features, expectedPid, expectedCreated); }
        catch (NativeRecoveryException) { throw; }
        catch (Exception error) when (error is InvalidDataException or IOException or UnauthorizedAccessException)
        { throw new NativeRecoveryException(NativeRecoveryReason.UnknownAttemptHistory, "Native attempt history is unreadable, partial or malformed; original evidence was retained.", error); }
        string proofPath = Path.Combine(directory, "retirement-proof.bin");
        if (File.Exists(proofPath)) { using var held = files.OpenFile(proofPath); WindowsNativeEpochFiles.RequireExact(held, proof); }
        else { files.Publish(proofPath, proof); }
        PublishRetirement(directory);
    }
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
        if (File.Exists(intent)) { using var existing = files.OpenFile(intent); WindowsNativeEpochFiles.RequireExact(existing, bytes); }
        else { files.Publish(intent, bytes); }
        files.Publish(Path.Combine(files.Root, $"selection-{Record.Ordinal:D20}-3.bin"), bytes);
        files.PublishCurrent(bytes);
        if (!(files.ReadCurrent() ?? []).AsSpan().SequenceEqual(bytes)) { throw new IOException("Native retirement publication is unresolved."); }
        selected = bytes; Record = retired;
    }
    public void Dispose()
    {
        package?.Dispose(); package = null;
        for (int i = held.Count - 1; i >= 0; i--) { held[i].Dispose(); }
        held.Clear(); files.Dispose();
    }
    private sealed class NoLease : IDisposable { public void Dispose() { } }
}
