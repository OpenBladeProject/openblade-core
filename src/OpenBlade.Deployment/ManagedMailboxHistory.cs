namespace OpenBlade.Deployment;

// dfdda134's complete managed reader enumerates transfer-*.bin and handoff-*.bin,
// not this flat namespace. Bootstrap's closed namespace applies only before its
// completion marker, before any run exists. Keep candidate legacy bytes in place.
[System.Runtime.Versioning.SupportedOSPlatform("windows")]
internal static class ManagedMailboxHistory
{
    private const string Prefix = "mailbox-history-";
    internal static string RunPath(WindowsNativeEpochFiles files, string run) =>
        Path.Combine(files.Root, Prefix + "run-" + run + ".bin");
    internal static string TransferPath(WindowsNativeEpochFiles files, ManagedMailboxSelection selected) =>
        Path.Combine(files.Root, Prefix + "transfer-" + selected.Value.Request + ".bin");
    internal static byte[]? ReadRun(WindowsNativeEpochFiles files, string run) => ReadPair(files,
        RunPath(files, run), Path.Combine(files.Root, "run-history-" + run + ".bin"));
    internal static byte[]? ReadTransfer(WindowsNativeEpochFiles files, ManagedMailboxSelection selected) => ReadPair(files,
        TransferPath(files, selected), Path.Combine(files.Root, "handoff-" + selected.Value.Request + "-history.bin"));
    private static byte[]? ReadPair(WindowsNativeEpochFiles files, string additive, string legacy)
        => ReadPair(path => ManagedMailboxRecovery.ReadOptional(files, path), additive, legacy);
    private static byte[]? ReadPair(Func<string, byte[]?> read, string additive, string legacy)
    {
        byte[]? newer = read(additive);
        byte[]? older = read(legacy);
        if (newer is not null && older is not null && !newer.AsSpan().SequenceEqual(older))
        { throw new IOException("Additive and legacy historical evidence disagree; both preserved."); }
        return newer ?? older;
    }
    internal static void ValidateNamespace(WindowsNativeEpochFiles files, IReadOnlyDictionary<string, ManagedMailboxSelection> selected) =>
        ValidateEntries(files.Root, Directory.EnumerateFileSystemEntries(files.Root, Prefix + "*"), selected,
            path => ManagedMailboxRecovery.ReadOptional(files, path));

    // Every retained run is inspected. Only the separately bounded transfer
    // lookup and one record's inputs are retained, regardless of run count.
    // The injected reader also exercises these exact streaming mechanics offline.
    internal static void ValidateEntries(string root, IEnumerable<string> entries,
        IReadOnlyDictionary<string, ManagedMailboxSelection> selected, Func<string, byte[]?> read)
    {
        byte[] Required(string path) => read(path) ?? throw new IOException("Historical record is missing.");
        byte[]? RunHistory(string id) => ReadPair(read, Path.Combine(root, Prefix + "run-" + id + ".bin"),
            Path.Combine(root, "run-history-" + id + ".bin"));
        foreach (string path in entries)
        {
            string name = Path.GetFileName(path);
            string[] parts = name.Split('-');
            if (parts.Length != 4 || !parts[3].EndsWith(".bin", StringComparison.Ordinal)
                || !NativeCanonical.IsHex(parts[3][..^4], 32)
                || name != Prefix + parts[2] + "-" + parts[3])
            { throw new IOException("Unknown additive mailbox history was preserved."); }
            string id = parts[3][..^4];
            if (parts[2] == "run")
            {
                byte[] run = Required(Path.Combine(root, "run-" + id + ".bin"));
                if (ManagedMailboxRun.Parse(run).Run != id) { throw new IOException("Historical run name differs."); }
                ValidateRun(root, read, run, RunHistory(id) ?? throw new IOException("Historical run is missing."));
            }
            else if (parts[2] == "transfer" && selected.TryGetValue(id, out var transfer))
            {
                byte[] bytes = ReadPair(read, Path.Combine(root, Prefix + "transfer-" + id + ".bin"),
                    Path.Combine(root, "handoff-" + id + "-history.bin")) ?? throw new IOException("Historical transfer is missing.");
                byte[] history = ParseTransfer(transfer, bytes);
                ValidateRun(root, read, transfer.Run, history);
                byte[]? original = RunHistory(ManagedMailboxRun.Parse(transfer.Run).Run);
                if (original is not null && !original.AsSpan().SequenceEqual(history))
                { throw new IOException("Historical run and transfer disagree."); }
            }
            else { throw new IOException("Unmatched additive mailbox history was preserved."); }
        }
    }
    private static void ValidateRun(string root, Func<string, byte[]?> read, byte[] run, byte[] history)
    {
        string id = ManagedMailboxRun.Parse(run).Run;
        byte[] binding = read(Path.Combine(root, "run-binding-" + id + ".bin"))
            ?? throw new IOException("Historical run binding is missing.");
        _ = ValidateRun(run, binding, history);
    }
    internal static byte[] ParseTransfer(ManagedMailboxSelection selected, byte[] bytes)
    {
        var fields = NativeCanonical.ParseLines(bytes, "OpenBlade.ManagedMailboxTransferHistory.v1", 3);
        byte[] history = Convert.FromBase64String(fields[1]);
        if (fields[0] != NativeCanonical.Digest(selected.Encode()) || !bytes.AsSpan().SequenceEqual(
            NativeCanonical.Lines("OpenBlade.ManagedMailboxTransferHistory.v1", fields[0], Convert.ToBase64String(history))))
        { throw new IOException("Historical transfer name or commitment differs."); }
        return history;
    }
    internal static byte[] ValidateRun(byte[] run, byte[] binding, byte[] history)
    {
        var value = ManagedMailboxBuildBinding.Parse(binding);
        var fields = NativeCanonical.ParseLines(history, "OpenBlade.ManagedMailboxRunHistory.v1", 4);
        byte[] projection = Convert.FromBase64String(fields[2]);
        if (value.RunDigest != NativeCanonical.Digest(run) || fields[0] != value.RunDigest
            || !Convert.FromBase64String(fields[1]).AsSpan().SequenceEqual(binding)
            || NativeCanonical.Digest(projection) != value.Enrollment
            || !history.AsSpan().SequenceEqual(ManagedMailboxRecovery.RunHistory(run, binding, projection)))
        { throw new IOException("Historical run/build namespace binding differs."); }
        if (NativeLocalDevEnrollment.ParseProjection(projection).Installation.ToString("N") != value.Installation)
        { throw new IOException("Historical projection installation differs from its build binding."); }
        return projection;
    }
}
