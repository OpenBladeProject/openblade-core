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
    {
        byte[]? newer = ManagedMailboxRecovery.ReadOptional(files, additive);
        byte[]? older = ManagedMailboxRecovery.ReadOptional(files, legacy);
        if (newer is not null && older is not null && !newer.AsSpan().SequenceEqual(older))
        { throw new IOException("Additive and legacy historical evidence disagree; both preserved."); }
        return newer ?? older;
    }
    internal static void ValidateNamespace(WindowsNativeEpochFiles files, string[] transfers)
    {
        string[] entries = Directory.EnumerateFileSystemEntries(files.Root, Prefix + "*").Take(8193).ToArray();
        if (entries.Length > 8192) { throw new IOException("Additive mailbox history exceeds its bound."); }
        var selected = transfers.Select(p => ManagedMailboxSelection.Parse(ManagedMailboxRecovery.Read(files, p)))
            .ToDictionary(s => s.Value.Request, StringComparer.Ordinal);
        foreach (string path in entries)
        {
            string name = Path.GetFileName(path);
            string[] parts = name.Split('-');
            if (parts.Length != 4 || !parts[3].EndsWith(".bin", StringComparison.Ordinal)
                || !NativeCanonical.IsHex(parts[3][..^4], 32))
            { throw new IOException("Unknown additive mailbox history was preserved."); }
            string id = parts[3][..^4];
            if (parts[2] == "run")
            {
                byte[] run = ManagedMailboxRecovery.Read(files, Path.Combine(files.Root, "run-" + id + ".bin"));
                if (ManagedMailboxRun.Parse(run).Run != id) { throw new IOException("Historical run name differs."); }
                ValidateRun(files, run, ReadRun(files, id)!);
            }
            else if (parts[2] == "transfer" && selected.TryGetValue(id, out var transfer))
            {
                byte[] bytes = ReadTransfer(files, transfer)!;
                var fields = NativeCanonical.ParseLines(bytes, "OpenBlade.ManagedMailboxTransferHistory.v1", 3);
                byte[] history = Convert.FromBase64String(fields[1]);
                if (fields[0] != NativeCanonical.Digest(transfer.Encode()) || !bytes.AsSpan().SequenceEqual(
                    NativeCanonical.Lines("OpenBlade.ManagedMailboxTransferHistory.v1", fields[0], Convert.ToBase64String(history))))
                { throw new IOException("Historical transfer name or commitment differs."); }
                ValidateRun(files, transfer.Run, history);
                byte[]? original = ReadRun(files, ManagedMailboxRun.Parse(transfer.Run).Run);
                if (original is not null && !original.AsSpan().SequenceEqual(history))
                { throw new IOException("Historical run and transfer disagree."); }
            }
            else { throw new IOException("Unmatched additive mailbox history was preserved."); }
        }
    }
    private static void ValidateRun(WindowsNativeEpochFiles files, byte[] run, byte[] history)
    {
        string id = ManagedMailboxRun.Parse(run).Run;
        byte[] binding = ManagedMailboxRecovery.Read(files, Path.Combine(files.Root, "run-binding-" + id + ".bin"));
        var value = ManagedMailboxBuildBinding.Parse(binding);
        var fields = NativeCanonical.ParseLines(history, "OpenBlade.ManagedMailboxRunHistory.v1", 4);
        byte[] projection = Convert.FromBase64String(fields[2]);
        if (value.RunDigest != NativeCanonical.Digest(run) || fields[0] != value.RunDigest
            || !Convert.FromBase64String(fields[1]).AsSpan().SequenceEqual(binding)
            || NativeCanonical.Digest(projection) != value.Enrollment
            || !history.AsSpan().SequenceEqual(ManagedMailboxRecovery.RunHistory(run, binding, projection)))
        { throw new IOException("Historical run/build namespace binding differs."); }
    }
}
