using System.Globalization;
using System.Runtime.Versioning;

namespace OpenBlade.Deployment;

internal sealed record ManagedMailboxSelection(long Ordinal, string PreviousTerminal, byte[] Run, byte[] Request)
{
    internal ManagedMailboxRequest Value => ManagedMailboxRequest.Parse(Request);
    internal byte[] Encode() => NativeCanonical.Lines("OpenBlade.ManagedMailboxTransfer.v2", Ordinal.ToString(CultureInfo.InvariantCulture),
        PreviousTerminal, Convert.ToBase64String(Run), Convert.ToBase64String(Request));
    internal static ManagedMailboxSelection Parse(byte[] bytes)
    {
        var f = NativeCanonical.ParseLines(bytes, "OpenBlade.ManagedMailboxTransfer.v2", 5);
        if (!long.TryParse(f[0], NumberStyles.None, CultureInfo.InvariantCulture, out long ordinal) || ordinal < 1
            || ordinal.ToString(CultureInfo.InvariantCulture) != f[0] || !NativeCanonical.IsHex(f[1], 64))
        { throw new InvalidDataException("Transfer selection differs."); }
        var value = new ManagedMailboxSelection(ordinal, f[1], Convert.FromBase64String(f[2]), Convert.FromBase64String(f[3]));
        _ = ManagedMailboxRun.Parse(value.Run);
        if (value.Value.RunDigest != NativeCanonical.Digest(value.Run) || !value.Encode().AsSpan().SequenceEqual(bytes))
        { throw new InvalidDataException("Transfer selection original differs."); }
        return value;
    }
}

// Terminal decisions retain the native writer, original custody/owner proof and
// the same managed exclusion. Records are immutable; selection is a projection.
[SupportedOSPlatform("windows")]
internal static partial class ManagedMailboxRecovery
{
    internal static string PathFor(WindowsNativeEpochFiles files, ManagedMailboxSelection selected, string name) =>
        name == "history" ? ManagedMailboxHistory.TransferPath(files, selected)
            : Path.Combine(files.Root, "handoff-" + selected.Value.Request + "-" + name + ".bin");
    internal static byte[]? ReadOptional(WindowsNativeEpochFiles files, string path)
    {
        try { _ = File.GetAttributes(path); }
        catch (FileNotFoundException) { return null; }
        using var held = files.OpenFile(path);
        return held.Length == 0 ? [] : WindowsNativeEpochFiles.ReadBounded(held);
    }
    internal static byte[] Read(WindowsNativeEpochFiles files, string path) => ReadOptional(files, path)
        ?? throw new IOException("Required immutable transfer evidence is missing.");
    internal static void ExactOrPublish(WindowsNativeEpochFiles files, string path, byte[] bytes)
    {
        byte[]? actual = ReadOptional(files, path);
        if (actual is null) { files.PublishCustody(path, bytes); }
        else if (!actual.AsSpan().SequenceEqual(bytes)) { throw new IOException("Immutable transfer publication conflicts; history retained."); }
    }
    private sealed record Snapshot(ManagedMailboxSelection[] Transfers, byte[]? Current)
    {
        internal ManagedMailboxSelection? Last => Transfers.LastOrDefault();
    }
    internal static ManagedMailboxSelection? Selected(WindowsNativeEpochFiles files)
    {
        var snapshot = ReadSnapshot(files);
        PublishProjections(files, snapshot);
        return snapshot.Last;
    }
    private static Snapshot ReadSnapshot(WindowsNativeEpochFiles files)
    {
        // Old bytes are evidence, never a silently adopted new request.
        foreach (string legacy in new[] { "request.bin", "receipt.bin", "mapping.bin" })
        { WindowsNativeEpochFiles.RequireAbsent(Path.Combine(files.Root, legacy)); }
        string[] names = Directory.EnumerateFiles(files.Root, "transfer-*.bin").Where(p => Path.GetFileName(p) != "transfer-current.bin")
            .Take(4097).Order(StringComparer.Ordinal).ToArray();
        if (names.Length > 4096) { throw new IOException("Transfer history exceeds its bound."); }
        ManagedMailboxSelection? last = null;
        RetainedRecords? priorRecords = null;
        string previous = new('0', 64);
        var selected = new Dictionary<string, ManagedMailboxSelection>(StringComparer.Ordinal);
        var transfers = new ManagedMailboxSelection[names.Length];
        for (int i = 0; i < names.Length; i++)
        {
            var value = ManagedMailboxSelection.Parse(Read(files, names[i]));
            if (value.Ordinal != i + 1 || Path.GetFileName(names[i]) != $"transfer-{value.Ordinal:D20}.bin"
                || value.PreviousTerminal != previous || !selected.TryAdd(value.Value.Request, value))
            { throw new IOException("Transfer history is unmatched, active or malformed."); }
            // Every predecessor terminal is required before a new selection.
            priorRecords?.RequireCompleted();
            var records = new RetainedRecords(files, value);
            priorRecords = records;
            transfers[i] = value;
            last = value;
            byte[]? terminal = records.Completed;
            previous = terminal is null ? new string('0', 64) : NativeCanonical.Digest(terminal);
        }
        string[] extras = Directory.EnumerateFiles(files.Root, "handoff-*.bin").Where(p => Path.GetFileName(p) != "handoff-root.bin").Take(32769).ToArray();
        if (extras.Length > 32768) { throw new IOException("Transfer evidence exceeds its bound."); }
        foreach (string extra in extras)
        {
            string name = Path.GetFileName(extra);
            string[] parts = name.Split('-');
            string suffix = string.Join("-", parts.Skip(2));
            if (parts.Length < 3 || !selected.ContainsKey(parts[1]) || suffix is not ("selection.bin" or "request.bin" or "run.bin" or "receipt.bin" or "original-exit.bin" or "epoch-intent.bin" or "mapping.bin" or "native-proof.bin" or "terminal-intent.bin" or "terminal.bin" or "history.bin")) { throw new IOException("Unmatched transfer evidence was preserved."); }
        }
        ManagedMailboxHistory.ValidateNamespace(files, selected);
        byte[]? current = ReadOptional(files, Path.Combine(files.Root, "transfer-current.bin"));
        if (last is null)
        {
            if (current is not null) { throw new IOException("Transfer selection has no immutable intent."); }
            return new(transfers, current);
        }
        if (current is not null && !transfers.Any(value => value.Encode().AsSpan().SequenceEqual(current)))
        { throw new IOException("Transfer pointer is not authenticated by immutable history."); }
        return new(transfers, current);
    }
    private static void PublishProjections(WindowsNativeEpochFiles files, Snapshot snapshot)
    {
        // The complete snapshot must pass before planned repair. Individual
        // publications still can race or fail: exact-or-publish preserves conflicts
        // and interruption can leave a truthful, recoverable partial projection.
        foreach (var value in snapshot.Transfers)
        {
            ExactOrPublish(files, PathFor(files, value, "selection"), value.Encode());
            ExactOrPublish(files, PathFor(files, value, "request"), value.Request);
            ExactOrPublish(files, PathFor(files, value, "run"), value.Run);
        }
        var last = snapshot.Last;
        if (last is not null && (snapshot.Current is null || !snapshot.Current.AsSpan().SequenceEqual(last.Encode())))
        { files.Publish(Path.Combine(files.Root, "transfer-current.bin"), last.Encode(), replace: true, privateCustody: true); }
    }
    internal static ManagedMailboxSelection Create(WindowsNativeEpochFiles files, byte[] run, ManagedMailboxRequest request)
    {
        using var writer = files.AcquireWriter();
        var snapshot = ReadSnapshot(files);
        var prior = snapshot.Last;
        if (snapshot.Transfers.Length >= 4096 || snapshot.Transfers.Any(value => value.Value.Request == request.Request))
        { throw new IOException("Transfer history bound or request uniqueness differs."); }
        string previous = new('0', 64);
        if (prior is not null)
        {
            ValidateTerminal(files, prior);
            if (ManagedMailboxRun.Parse(prior.Run).Run == ManagedMailboxRun.Parse(run).Run
                || ManagedMailboxRun.Parse(prior.Run).Process == ManagedMailboxRun.Parse(run).Process)
            { throw new IOException("Renewal requires a new authenticated managed run and request."); }
            previous = NativeCanonical.Digest(Read(files, PathFor(files, prior, "terminal")));
        }
        var next = ManagedMailboxSelection.Parse(new ManagedMailboxSelection(checked((prior?.Ordinal ?? 0) + 1), previous, run, request.Encode()).Encode());
        ExactOrPublish(files, Path.Combine(files.Root, $"transfer-{next.Ordinal:D20}.bin"), next.Encode());
        // The same writer protects the already validated history. Appending one
        // new immutable selection needs no second complete retained-run scan.
        PublishProjections(files, new([.. snapshot.Transfers, next], snapshot.Current));
        return next;
    }
    internal static byte[] Receipt(ManagedMailboxSelection selected) => NativeCanonical.Lines("OpenBlade.ManagedMailboxQuiescent.v1",
        NativeCanonical.Digest(selected.Request), Convert.ToBase64String(selected.Run), "transport-quiescent-no-mutation");
    internal static byte[] OriginalExit(ManagedMailboxSelection selected) => NativeCanonical.Lines("OpenBlade.ManagedMailboxOriginalExited.v2",
        NativeCanonical.Digest(selected.Encode()), ManagedMailboxRun.Parse(selected.Run).Process, NativeCanonical.Digest(Receipt(selected)));
    internal static byte[] Mapping(ManagedMailboxSelection selected, NativeEpochRecord epoch) => NativeCanonical.Lines("OpenBlade.ManagedMailboxEpoch.v2",
        NativeCanonical.Digest(Receipt(selected)), NativeCanonical.Digest(selected.Request), epoch.Epoch, epoch.Installation, epoch.Request, epoch.Package,
        ManagedMailboxRun.Parse(selected.Run).Run, NativeCanonical.Digest(selected.Encode()));
    internal static void RequireActive(WindowsNativeEpochFiles files, ManagedMailboxSelection selected)
    {
        if (!(Selected(files)?.Encode() ?? []).AsSpan().SequenceEqual(selected.Encode())) { throw new IOException("Transfer selection changed."); }
        WindowsNativeEpochFiles.RequireAbsent(PathFor(files, selected, "terminal"));
    }
    internal static void RequireOriginal(WindowsNativeEpochFiles files, ManagedMailboxSelection selected)
    {
        if (!Read(files, PathFor(files, selected, "receipt")).AsSpan().SequenceEqual(Receipt(selected))
            || !Read(files, PathFor(files, selected, "original-exit")).AsSpan().SequenceEqual(OriginalExit(selected)))
        { throw new IOException("Exact original process and transport receipt are unconfirmed."); }
    }
    private static byte[] Terminal(WindowsNativeEpochFiles files, ManagedMailboxSelection selected, string disposition, byte[] proof) =>
        Terminal(selected, ReadOptional(files, PathFor(files, selected, "mapping")), disposition, proof);
    private static byte[] Terminal(ManagedMailboxSelection selected, byte[]? mapping, string disposition, byte[] proof) =>
        NativeCanonical.Lines("OpenBlade.ManagedMailboxTerminal.v2", NativeCanonical.Digest(selected.Encode()),
            NativeCanonical.Digest(Receipt(selected)), NativeCanonical.Digest(OriginalExit(selected)), disposition,
            mapping is null ? new string('0', 64) : NativeCanonical.Digest(mapping), NativeCanonical.Digest(proof));
    internal static void ValidateTerminal(WindowsNativeEpochFiles files, ManagedMailboxSelection selected) =>
        new RetainedRecords(files, selected).RequireCompleted();
    internal static void Resolve(WindowsNativeEpochFiles files, ManagedMailboxNativeCustody native,
        Action<ManagedMailboxSelection>? authenticate = null, Func<NativeLocalDevEnrollment>? acquireEnrollment = null)
    {
        var selected = Selected(files);
        if (selected is null) { native.RequireSafe(); return; }
        // Fresh custody authenticates every retained predecessor before the
        // latest request. A previous in-memory canceled set is never authority.
        foreach (string path in Directory.EnumerateFiles(files.Root, "transfer-*.bin")
            .Where(p => Path.GetFileName(p) != "transfer-current.bin").Order(StringComparer.Ordinal))
        {
            var transfer = ManagedMailboxSelection.Parse(Read(files, path));
            ResolveOne(files, native, transfer, authenticate, terminalOnly: transfer.Ordinal < selected.Ordinal, acquireEnrollment);
        }
        native.RequireSafe();
    }
    // Setup selection may run while a NEW managed run owns exclusion. Under the
    // native writer plus managed metadata writer, authenticate immutable terminals
    // only; do not cancel a live transfer or repair publication without exclusion.
    internal static void AuthenticateRetainedTerminals(WindowsNativeEpochFiles files, ManagedMailboxNativeCustody native, ManagedMailboxSelection? active = null)
    {
        using var writer = files.AcquireWriter();
        var selected = Selected(files);
        if (selected is not null)
        {
            foreach (string path in Directory.EnumerateFiles(files.Root, "transfer-*.bin")
                .Where(p => Path.GetFileName(p) != "transfer-current.bin").Order(StringComparer.Ordinal))
            {
                var transfer = ManagedMailboxSelection.Parse(Read(files, path));
                if (active is not null && transfer.Encode().AsSpan().SequenceEqual(active.Encode())) { continue; }
                ResolveOne(files, native, transfer, null, terminalOnly: true);
            }
        }
        native.RequireSafe();
    }
    private static void ResolveOne(WindowsNativeEpochFiles files, ManagedMailboxNativeCustody native,
        ManagedMailboxSelection selected, Action<ManagedMailboxSelection>? authenticate, bool terminalOnly, Func<NativeLocalDevEnrollment>? acquireEnrollment = null)
    {
        var records = new RetainedRecords(files, selected);
        records.RequireOriginal();
        bool alreadyTerminal = records.Completed is not null;
        if (terminalOnly && !alreadyTerminal) { throw new IOException("Active historical transfer remains fenced."); }
        byte[]? intent = records.EpochIntent;
        NativeEpochRecord? epoch = records.Epoch;
        if (epoch is null && !alreadyTerminal)
        {
            epoch = native.MatchUnboundPreparation(selected);
            if (epoch is not null)
            {
                intent = NativeCanonical.Lines("OpenBlade.ManagedMailboxEpochIntent.v2", NativeCanonical.Digest(selected.Encode()), Convert.ToBase64String(epoch.Encode()));
            }
        }
        if (epoch is not null)
        {
            _ = ParseEpochIntent(selected, intent!);
            byte[] mapping = Mapping(selected, epoch);
            native.RequireBinding(epoch, mapping);
            if (alreadyTerminal)
            {
                // Terminal predecessors must be complete; never repair history
                // to make a terminal string appear authoritative.
                if (!Read(files, PathFor(files, selected, "mapping")).AsSpan().SequenceEqual(mapping))
                { throw new IOException("Terminal mapping is missing or differs."); }
                native.RequirePublishedMapping(epoch, mapping);
            }
        }
        // Leading unreserved terminals retain the same original build contract.
        // Current enrollment is usable only when its exact projection matches
        // the original binding; positive historical bytes survive rotation.
        Action? publishHistory = null;
        if (authenticate is null) { publishHistory = Authenticate(files, selected, records, epoch is null ? null : native.RequireProjection(epoch), acquireEnrollment); }
        else { authenticate(selected); }
        var resolution = native.Resolve(selected, epoch);
        byte[] proof = records.Proof ?? resolution.Proof;
        native.RequirePreservedResolution(selected, epoch, proof, resolution.Disposition);
        // No publication repairs precede exact original/cache/proof admission.
        publishHistory?.Invoke();
        if (!alreadyTerminal && epoch is not null)
        {
            ExactOrPublish(files, PathFor(files, selected, "epoch-intent"), intent!);
            byte[] mapping = Mapping(selected, epoch);
            ExactOrPublish(files, PathFor(files, selected, "mapping"), mapping);
            native.PublishMapping(epoch, mapping);
        }
        byte[] terminal = Terminal(files, selected, resolution.Disposition, proof);
        if (alreadyTerminal)
        {
            if (!Read(files, PathFor(files, selected, "terminal-intent")).AsSpan().SequenceEqual(terminal))
            { throw new IOException("Terminal intent differs from authenticated history."); }
        }
        else
        {
            ExactOrPublish(files, PathFor(files, selected, "native-proof"), proof);
            ExactOrPublish(files, PathFor(files, selected, "terminal-intent"), terminal);
            ExactOrPublish(files, PathFor(files, selected, "terminal"), terminal);
        }
        ValidateTerminal(files, selected);
        native.ConfirmResolved(epoch);
    }
    internal static byte[] RunHistory(byte[] run, byte[] binding, byte[] projection) =>
        NativeCanonical.Lines("OpenBlade.ManagedMailboxRunHistory.v1", NativeCanonical.Digest(run),
            Convert.ToBase64String(binding), Convert.ToBase64String(projection));
    internal static void PreserveRunHistory(WindowsNativeEpochFiles files, byte[] run, byte[] binding, byte[] projection) =>
        ExactOrPublish(files, ManagedMailboxHistory.RunPath(files, ManagedMailboxRun.Parse(run).Run), RunHistory(run, binding, projection));

    private static Action Authenticate(WindowsNativeEpochFiles files, ManagedMailboxSelection selected, RetainedRecords records, byte[]? projection, Func<NativeLocalDevEnrollment>? acquireEnrollment)
    {
        byte[] bindingBytes = records.BindingBytes;
        var binding = records.Binding;
        string runPath = ManagedMailboxHistory.RunPath(files, ManagedMailboxRun.Parse(selected.Run).Run);
        byte[]? transferHistory = records.TransferHistory;
        if (records.Projection is { } historicalProjection)
        {
            // Native custody is an independent anchor, never replaced by managed history.
            if (projection is not null && !projection.AsSpan().SequenceEqual(historicalProjection))
            { throw new IOException("Historical managed projection differs from native original projection."); }
            projection ??= historicalProjection;
        }
        // Legacy data can upgrade only from positively recovered exact original
        // projection bytes, never from a merely compatible current enrollment.
        using var package = projection is null ? WindowsManagedMailboxPackage.OpenCached(files, binding.Package, acquireEnrollment)
            : WindowsManagedMailboxPackage.OpenCachedHistorical(files, binding.Package, projection);
        if (package.Installation != binding.Installation || package.EnrollmentDigest != binding.Enrollment)
        { throw new IOException("Managed original enrolled package differs."); }
        byte[] originalHistory = RunHistory(selected.Run, bindingBytes, package.VerificationProjection);
        byte[] boundHistory = NativeCanonical.Lines("OpenBlade.ManagedMailboxTransferHistory.v1", NativeCanonical.Digest(selected.Encode()), Convert.ToBase64String(originalHistory));
        if (transferHistory is not null && !transferHistory.AsSpan().SequenceEqual(boundHistory))
        { throw new IOException("Transfer original build commitment differs."); }
        return () =>
        {
            ExactOrPublish(files, runPath, originalHistory);
            ExactOrPublish(files, PathFor(files, selected, "history"), boundHistory);
        };
    }
}
