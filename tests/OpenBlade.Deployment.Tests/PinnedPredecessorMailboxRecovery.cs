using System.Runtime.Versioning;
using OpenBlade.Deployment;

namespace OpenBlade.Deployment.Tests;

// Pinned dfdda134 reader class; only the class name/namespace changed for private unit invocation.
// Terminal decisions retain the native writer, original custody/owner proof and
// the same managed exclusion. Records are immutable; selection is a projection.
[SupportedOSPlatform("windows")]
internal static class PinnedPredecessorMailboxRecovery
{
    internal static string PathFor(WindowsNativeEpochFiles files, ManagedMailboxSelection selected, string name) =>
        Path.Combine(files.Root, "handoff-" + selected.Value.Request + "-" + name + ".bin");
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
    internal static ManagedMailboxSelection? Selected(WindowsNativeEpochFiles files)
    {
        // Old bytes are evidence, never a silently adopted new request.
        foreach (string legacy in new[] { "request.bin", "receipt.bin", "mapping.bin" })
        { WindowsNativeEpochFiles.RequireAbsent(Path.Combine(files.Root, legacy)); }
        string[] names = Directory.EnumerateFiles(files.Root, "transfer-*.bin").Where(p => Path.GetFileName(p) != "transfer-current.bin")
            .Take(4097).Order(StringComparer.Ordinal).ToArray();
        if (names.Length > 4096) { throw new IOException("Transfer history exceeds its bound."); }
        ManagedMailboxSelection? last = null;
        string previous = new('0', 64);
        var ids = new HashSet<string>(StringComparer.Ordinal);
        for (int i = 0; i < names.Length; i++)
        {
            var value = ManagedMailboxSelection.Parse(Read(files, names[i]));
            if (value.Ordinal != i + 1 || Path.GetFileName(names[i]) != $"transfer-{value.Ordinal:D20}.bin"
                || value.PreviousTerminal != previous || !ids.Add(value.Value.Request))
            { throw new IOException("Transfer history is unmatched, active or malformed."); }
            // Every predecessor terminal is required before a new selection.
            if (last is not null) { ValidateTerminal(files, last); }
            ExactOrPublish(files, PathFor(files, value, "selection"), value.Encode());
            ExactOrPublish(files, PathFor(files, value, "request"), value.Request);
            ExactOrPublish(files, PathFor(files, value, "run"), value.Run);
            last = value;
            byte[]? terminal = ReadOptional(files, PathFor(files, value, "terminal"));
            previous = terminal is null ? new string('0', 64) : NativeCanonical.Digest(terminal);
        }
        string[] extras = Directory.EnumerateFiles(files.Root, "handoff-*.bin").Where(p => Path.GetFileName(p) != "handoff-root.bin").Take(32769).ToArray();
        if (extras.Length > 32768) { throw new IOException("Transfer evidence exceeds its bound."); }
        foreach (string extra in extras)
        {
            string name = Path.GetFileName(extra);
            string[] parts = name.Split('-');
            string suffix = string.Join("-", parts.Skip(2));
            if (parts.Length < 3 || !ids.Contains(parts[1]) || suffix is not ("selection.bin" or "request.bin" or "run.bin" or "receipt.bin" or "original-exit.bin" or "epoch-intent.bin" or "mapping.bin" or "native-proof.bin" or "terminal-intent.bin" or "terminal.bin")) { throw new IOException("Unmatched transfer evidence was preserved."); }
        }
        byte[]? current = ReadOptional(files, Path.Combine(files.Root, "transfer-current.bin"));
        if (last is null)
        {
            if (current is not null) { throw new IOException("Transfer selection has no immutable intent."); }
            return null;
        }
        if (current is not null && !names.Any(p => Read(files, p).AsSpan().SequenceEqual(current)))
        { throw new IOException("Transfer pointer is not authenticated by immutable history."); }
        // Finishes only the projection of a positively recorded selection. This
        // neither retires a transfer nor authorizes managed/native mailbox use.
        if (current is null || !current.AsSpan().SequenceEqual(last.Encode()))
        { files.Publish(Path.Combine(files.Root, "transfer-current.bin"), last.Encode(), replace: true, privateCustody: true); }
        return last;
    }
    internal static ManagedMailboxSelection Create(WindowsNativeEpochFiles files, byte[] run, ManagedMailboxRequest request)
    {
        using var writer = files.AcquireWriter();
        var prior = Selected(files);
        string previous = new('0', 64);
        if (prior is not null)
        {
            ValidateTerminal(files, prior);
            if (ManagedMailboxRun.Parse(prior.Run).Run == ManagedMailboxRun.Parse(run).Run
                || ManagedMailboxRun.Parse(prior.Run).Process == ManagedMailboxRun.Parse(run).Process)
            { throw new IOException("Renewal requires a new authenticated managed run and request."); }
            previous = NativeCanonical.Digest(Read(files, PathFor(files, prior, "terminal")));
        }
        var next = new ManagedMailboxSelection(checked((prior?.Ordinal ?? 0) + 1), previous, run, request.Encode());
        ExactOrPublish(files, Path.Combine(files.Root, $"transfer-{next.Ordinal:D20}.bin"), next.Encode());
        return Selected(files)!;
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
    private static byte[] Terminal(WindowsNativeEpochFiles files, ManagedMailboxSelection selected, string disposition, byte[] proof)
    {
        byte[]? mapping = ReadOptional(files, PathFor(files, selected, "mapping"));
        return NativeCanonical.Lines("OpenBlade.ManagedMailboxTerminal.v2", NativeCanonical.Digest(selected.Encode()),
            NativeCanonical.Digest(Receipt(selected)), NativeCanonical.Digest(OriginalExit(selected)), disposition,
            mapping is null ? new string('0', 64) : NativeCanonical.Digest(mapping), NativeCanonical.Digest(proof));
    }
    internal static void ValidateTerminal(WindowsNativeEpochFiles files, ManagedMailboxSelection selected)
    {
        RequireOriginal(files, selected);
        byte[] proof = Read(files, PathFor(files, selected, "native-proof"));
        string[] f = NativeCanonical.ParseLines(Read(files, PathFor(files, selected, "terminal")), "OpenBlade.ManagedMailboxTerminal.v2", 7);
        if (f[3] is not ("canceled-before-native-use" or "consumed-and-retired")
            || !Read(files, PathFor(files, selected, "terminal")).AsSpan().SequenceEqual(Terminal(files, selected, f[3], proof)))
        { throw new IOException("Terminal evidence differs from the exact original request."); }
        // The native proof is the full protected, custody-produced snapshot, not
        // a tombstone, absent-file assertion or mutable selected-current marker.
        var native = NativeCanonical.ParseLines(proof, "OpenBlade.ManagedNativeResolution.v2", 7);
        byte[] evidence = Convert.FromBase64String(native[5]);
        if (native[0] != NativeCanonical.Digest(selected.Encode()) || native[1] != f[3]
            || native[3] != NativeCanonical.Digest(evidence) || !NativeCanonical.IsHex(native[4], 64)
            || native[2] != "not-reserved" && !NativeCanonical.IsHex(native[2], 32))
        { throw new IOException("Terminal native proof is unmatched."); }
    }
    internal static void Resolve(WindowsNativeEpochFiles files, ManagedMailboxNativeCustody native,
        Action<ManagedMailboxSelection>? authenticate = null)
    {
        var selected = Selected(files);
        if (selected is null) { native.RequireSafe(); return; }
        // Fresh custody authenticates every retained predecessor before the
        // latest request. A previous in-memory canceled set is never authority.
        foreach (string path in Directory.EnumerateFiles(files.Root, "transfer-*.bin")
            .Where(p => Path.GetFileName(p) != "transfer-current.bin").Order(StringComparer.Ordinal))
        {
            var transfer = ManagedMailboxSelection.Parse(Read(files, path));
            ResolveOne(files, native, transfer, authenticate, terminalOnly: transfer.Ordinal < selected.Ordinal);
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
        ManagedMailboxSelection selected, Action<ManagedMailboxSelection>? authenticate, bool terminalOnly)
    {
        RequireOriginal(files, selected);
        bool alreadyTerminal = ReadOptional(files, PathFor(files, selected, "terminal")) is not null;
        if (terminalOnly && !alreadyTerminal) { throw new IOException("Active historical transfer remains fenced."); }
        if (alreadyTerminal) { ValidateTerminal(files, selected); }
        byte[]? intent = ReadOptional(files, PathFor(files, selected, "epoch-intent"));
        if (intent is null && ReadOptional(files, PathFor(files, selected, "mapping")) is not null)
        { throw new IOException("A mapping without its immutable binding intent remains fenced."); }
        NativeEpochRecord? epoch = intent is null ? null : NativeEpochRecord.Parse(Convert.FromBase64String(
            NativeCanonical.ParseLines(intent, "OpenBlade.ManagedMailboxEpochIntent.v2", 3)[1]));
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
            byte[] expectedIntent = NativeCanonical.Lines("OpenBlade.ManagedMailboxEpochIntent.v2", NativeCanonical.Digest(selected.Encode()), Convert.ToBase64String(epoch.Encode()));
            if (!intent!.AsSpan().SequenceEqual(expectedIntent) || epoch.Request != selected.Value.Request
                || epoch.Installation != selected.Value.Installation || epoch.Package != selected.Value.Package)
            { throw new IOException("Transfer epoch intent differs."); }
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
        // A retained preparation carries its own signed enrollment projection.
        // Reauthenticate its original managed cache even after enrollment rotates.
        if (!alreadyTerminal || epoch is not null)
        {
            if (authenticate is null) { Authenticate(files, selected, epoch is null ? null : native.RequireProjection(epoch)); }
            else { authenticate(selected); }
        }
        var resolution = native.Resolve(selected, epoch);
        byte[] proof = ReadOptional(files, PathFor(files, selected, "native-proof")) ?? resolution.Proof;
        native.RequirePreservedResolution(selected, epoch, proof, resolution.Disposition);
        // No publication repairs precede exact original/cache/proof admission.
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
    private static void Authenticate(WindowsNativeEpochFiles files, ManagedMailboxSelection selected, byte[]? projection)
    {
        var run = ManagedMailboxRun.Parse(selected.Run);
        var binding = ManagedMailboxBuildBinding.Parse(Read(files, Path.Combine(files.Root, "run-binding-" + run.Run + ".bin")));
        if (binding.RunDigest != NativeCanonical.Digest(selected.Run) || binding.Installation != selected.Value.Installation)
        { throw new IOException("Managed original signed binding differs."); }
        if (!Read(files, Path.Combine(files.Root, "run-" + run.Run + ".bin")).AsSpan().SequenceEqual(selected.Run))
        { throw new IOException("Managed original run differs from immutable history."); }
        using var package = projection is null ? WindowsManagedMailboxPackage.OpenCached(files, binding.Package)
            : WindowsManagedMailboxPackage.OpenCachedHistorical(files, binding.Package, projection);
        if (package.Installation != binding.Installation || package.EnrollmentDigest != binding.Enrollment)
        { throw new IOException("Managed original enrolled package differs."); }
    }
}
