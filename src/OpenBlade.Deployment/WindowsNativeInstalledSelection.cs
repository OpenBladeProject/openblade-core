namespace OpenBlade.Deployment;

// Cleanup keeps immutable package guards separate from replaceable selection.
// This is not firmware ownership or a drain certificate by itself.
[System.Runtime.Versioning.SupportedOSPlatform("windows")]
internal sealed class WindowsNativeInstalledSelection : IDisposable
{
    private readonly WindowsNativeEpochFiles files;
    private readonly List<IDisposable> held = [];
    private readonly byte[] selected;
    private WindowsNativePackage? package;
    internal NativeEpochRecord Record { get; }
    internal string ServicePath => Path.Combine(files.Root, Record.Epoch, "app", "OpenBlade.Service.exe");
    internal string AgentPath => Path.Combine(files.Root, Record.Epoch, "app", "OpenBlade.SessionAgent.exe");
    internal string ServiceSha256 => package?.Service.Sha256 ?? throw new ObjectDisposedException(nameof(WindowsNativeInstalledSelection));
    private WindowsNativeInstalledSelection(WindowsNativeEpochFiles files, byte[] selected)
    { this.files = files; this.selected = selected; Record = NativeEpochRecord.Parse(selected); }
    internal static async Task<WindowsNativeInstalledSelection?> OpenProductAsync(CancellationToken token)
    {
        if (!Directory.Exists(WindowsNativeEpochFiles.ProductRoot)) { return null; }
        var files = WindowsNativeEpochFiles.OpenProduct();
        try { return await OpenAsync(files, token); }
        catch { files.Dispose(); throw; }
    }
    internal static async Task<WindowsNativeInstalledSelection> OpenAsync(WindowsNativeEpochFiles files, CancellationToken token)
    {
        var selected = files.ReadCurrent() ?? throw new IOException("Native selection is unavailable.");
        var value = new WindowsNativeInstalledSelection(files, selected);
        try
        {
            value.held.Add(files.AcquireWriter());
            value.RequireCurrent();
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
        string directory = Path.Combine(files.Root, Record.Epoch);
        using var drain = files.OpenFile(Path.Combine(directory, $"status-drain-{pid}-{created}.bin"));
        WindowsNativeEpochFiles.RequireExact(drain, NativeCanonical.Lines("OpenBlade.NativeStatusDrain.v1", Record.Epoch,
            pid.ToString(System.Globalization.CultureInfo.InvariantCulture), created.ToString(System.Globalization.CultureInfo.InvariantCulture)));
        // Metadata drain cannot clear a native firmware-owner fence.
        WindowsNativeTrustLifecycle.RequireOwnerRetired(files);
        if (Record.Phase != NativeEpochPhase.Committed) { throw new IOException("Interrupted native startup needs explicit same-attempt metadata recovery."); }
        var retired = Record with { Phase = NativeEpochPhase.Retired, Readiness = new string('0', 64) };
        byte[] bytes = retired.Encode();
        files.Publish(Path.Combine(directory, "Retired.bin"), bytes);
        files.Publish(Path.Combine(files.Root, $"selection-{Record.Ordinal:D20}-3.bin"), bytes);
        files.PublishCurrent(bytes);
        if (!(files.ReadCurrent() ?? []).AsSpan().SequenceEqual(bytes)) { throw new IOException("Native retirement publication is unresolved."); }
    }
    public void Dispose()
    {
        package?.Dispose(); package = null;
        for (int i = held.Count - 1; i >= 0; i--) { held[i].Dispose(); }
        held.Clear(); files.Dispose();
    }
    private sealed class NoLease : IDisposable { public void Dispose() { } }
}
