using System.Runtime.Versioning;

namespace OpenBlade.Deployment;

// Independently authenticates every retained native generation. Holding original
// attempt custody and the SAME owner writer fences late startup/firmware owners.
[SupportedOSPlatform("windows")]
internal sealed class ManagedMailboxNativeCustody : IDisposable
{
    private readonly WindowsNativeEpochFiles? files;
    internal bool IsPrivateFixture => files?.IsPrivateFixture == true;
    // Epoch to history commitment of each native handback, freshly derived.
    internal IReadOnlyDictionary<string, string> Handbacks { get; } = new Dictionary<string, string>(StringComparer.Ordinal);
    // The binding the Service adopts records with. Its view reopens the namespace
    // custody authenticated; custody itself ends with startup admission.
    internal NativeHandbackBinding HandbackBinding => files is null || Handbacks.Count == 0 ? NativeHandbackBinding.None : new(Handbacks, files.ReopenLater());
    private FileStream? writer;
    private readonly List<IDisposable> held = [];
    private readonly Dictionary<string, Generation> generations = new(StringComparer.Ordinal);
    private readonly HashSet<string> canceled = new(StringComparer.Ordinal);
    private readonly string ownerCommitment;
    private readonly byte[] namespaceProof;
    private sealed record Generation(NativeEpochRecord Record, byte[] Proof, byte[] Reservation, bool Preparation, bool RequiresMailboxHandoff, byte[] Projection, string Source, byte[]? PreparationBinding);
    private ManagedMailboxNativeCustody()
    {
        ownerCommitment = NativeCanonical.Digest(NativeCanonical.Lines("OpenBlade.NativeOwnerAbsent.v2", "verified-native-namespace-absent"));
        namespaceProof = NativeCanonical.Lines("OpenBlade.ManagedNativeNamespace.v2", "native-namespace-absent", ownerCommitment);
    }
    internal static ManagedMailboxNativeCustody Acquire()
    {
        WindowsNativeEpochFiles.RequireLegacyStoreAbsent();
        if (!WindowsNativeEpochFiles.IsPresentDirectory(WindowsNativeEpochFiles.ProductRoot)) { return new(); }
        return new(WindowsNativeEpochFiles.OpenProduct());
    }
#if OPENBLADE_NATIVE_PACKAGE_TESTS
    internal static ManagedMailboxNativeCustody AcquireForTests(WindowsNativeEpochFiles files) => new(files);
#endif
    // Caller already retains the SAME native writer; this view never releases it.
    internal static ManagedMailboxNativeCustody AcquireUnderWriter(WindowsNativeEpochFiles files) => new(files, acquireWriter: false);
    private ManagedMailboxNativeCustody(WindowsNativeEpochFiles files, bool acquireWriter = true)
    {
        this.files = files;
        try
        {
            if (acquireWriter) { writer = files.AcquireWriter(); }
            var inspection = WindowsNativeRecoveryState.Inspect(files);
            bool firstPreparation = inspection.State == NativeInstallationState.UnresolvedHistory
                && inspection.Selection is null && WindowsNativeEpochTransaction.ReadFrontier(files) is null;
            if (inspection.State is not (NativeInstallationState.Selected or NativeInstallationState.VerifiedBootstrap) && !firstPreparation)
            { throw new IOException("Native startup custody is unresolved; managed operation remains fenced."); }
            var selected = inspection.Selection is null ? null : NativeEpochRecord.Parse(inspection.Selection);
            if (selected is not null && selected.Phase != NativeEpochPhase.Retired)
            { throw new IOException("Selected native generation is active or unretired."); }
            using (var marker = files.OpenFile(Path.Combine(files.Root, "root.bin")))
            { WindowsNativeEpochFiles.RequireExact(marker, NativeCanonical.Lines("OpenBlade.NativeRoot.v1", "legacy-store-absent")); }
            var authenticated = new List<(WindowsNativeInstalledSelection Package, NativeEpochRecord Record, string Directory, bool Preparation, byte[]? PreparationBinding)>();
            string[] directories = Directory.EnumerateDirectories(files.Root).Where(p => Path.GetFileName(p) != "owner").Take(4097).ToArray();
            if (directories.Length > 4096) { throw new IOException("Native history exceeds its bound."); }
            foreach (string directory in directories)
            {
                string epoch = Path.GetFileName(directory);
                if (!NativeCanonical.IsHex(epoch, 32)) { throw new IOException("Unknown native namespace was preserved."); }
                held.Add(files.RetainDirectory(directory));
                byte[] reservation = ManagedMailboxRecovery.Read(files, directory + ".reserved");
                if (reservation.Length != 0) { throw new IOException("Native permanent reservation differs."); }
                byte[]? retired = ManagedMailboxRecovery.ReadOptional(files, Path.Combine(directory, "Retired.bin"));
                bool preparation = retired is null;
                byte[]? origin = ManagedMailboxRecovery.ReadOptional(files, Path.Combine(directory, "origin.bin"));
                byte[]? preparationBinding = null;
                byte[]? preparationOrigin = null;
                byte[]? preparationBytes = ManagedMailboxRecovery.ReadOptional(files, Path.Combine(directory, "preparation.bin"));
                if (preparationBytes is not null)
                {
                    var f = NativeCanonical.ParseLines(preparationBytes, "OpenBlade.ManagedNativePreparation.v2", 3);
                    preparationOrigin = Convert.FromBase64String(f[0]); preparationBinding = Convert.FromBase64String(f[1]);
                    _ = NativeEpochRecord.Parse(preparationOrigin);
                    if (origin is not null && !origin.AsSpan().SequenceEqual(preparationOrigin)) { throw new IOException("Native preparation origin differs."); }
                    held.Add(files.OpenFile(Path.Combine(directory, "preparation.bin")));
                }
                var record = NativeEpochRecord.Parse(retired ?? origin ?? preparationOrigin
                    ?? throw new IOException("Native preparation has no positive origin intent."));
                if (record.Epoch != epoch || record.Phase != (preparation ? NativeEpochPhase.Provisional : NativeEpochPhase.Retired))
                { throw new IOException("Native retained generation differs."); }
                if (preparation)
                {
                    // An origin alone is not a no-launch certificate. Every
                    // frontier is checked under the writer; original custody,
                    // policy, signed package and attempt set are checked below.
                    foreach (string frontier in Directory.EnumerateFiles(files.Root, "selection-*.bin").Take(16385))
                    {
                        if (NativeEpochRecord.Parse(ManagedMailboxRecovery.Read(files, frontier)).Epoch == epoch)
                        { throw new IOException("A selected native attempt cannot be canceled as an unselected preparation."); }
                    }
                    WindowsNativeEpochFiles.RequireAbsent(Path.Combine(directory, "submission.bin"));
                }
                var package = WindowsNativeInstalledSelection.AuthenticateManagedHistoryAsync(files.RetainView(), record, preparation, CancellationToken.None, preparation ? preparationOrigin : null).GetAwaiter().GetResult();
                held.Add(package);
                var attempts = files.RetainDirectory(Path.Combine(directory, "attempts")); held.Add(attempts);
                var custody = files.AcquireServiceCustody(Path.Combine(directory, "attempts", "custody.lock")); held.Add(custody);
                WindowsNativeEpochFiles.RequireExact(custody, NativeCanonical.Lines("OpenBlade.NativeServiceCustody.v1", epoch));
                authenticated.Add((package, record, directory, preparation, preparationBinding));
            }
            var selections = new List<NativeEpochRecord>();
            if (selected is not null) { selections.Add(selected); }
            var knownEpochs = directories.Select(Path.GetFileName).ToHashSet(StringComparer.Ordinal);
            string[] entries = Directory.EnumerateFileSystemEntries(files.Root).Take(32769).ToArray();
            if (entries.Length > 32768) { throw new IOException("Native namespace exceeds its bound."); }
            foreach (string entry in entries)
            {
                string name = Path.GetFileName(entry);
                if (name is "owner" or "root.bin" or "writer.lock" or "current.bin" || knownEpochs.Contains(name)) { continue; }
                if (name.EndsWith(".reserved", StringComparison.Ordinal) && knownEpochs.Contains(name[..^9])) { continue; }
                if (name.StartsWith("selection-", StringComparison.Ordinal) && name.EndsWith(".bin", StringComparison.Ordinal))
                {
                    byte[] bytes = ManagedMailboxRecovery.Read(files, entry);
                    var frontier = NativeEpochRecord.Parse(bytes);
                    if (name != WindowsNativeEpochTransaction.FrontierName(frontier)
                        || !ManagedMailboxRecovery.Read(files, Path.Combine(files.Root, frontier.Epoch, frontier.Phase + ".bin")).AsSpan().SequenceEqual(bytes))
                    { throw new IOException("Historical selection phase or filename differs."); }
                    selections.Add(frontier); continue;
                }
                throw new IOException("Unknown or unmatched native namespace evidence was preserved.");
            }
            // All original custody precedes the one global owner writer.
            var owner = WindowsNativeTrustLifecycle.AcquireRetiredOwner(files); held.Add(owner);
            ownerCommitment = owner.Commitment;
            // A crashed generation hands back only what this fresh read derives.
            if (owner.Thermal.Handback is { IsEmpty: false } handback) { Handbacks = new Dictionary<string, string>(StringComparer.Ordinal) { [handback.Epoch] = handback.Commitment }; }
            // Setup writes a record only for the generation its history hands back.
            foreach (var item in authenticated.Where(item => !Handbacks.ContainsKey(item.Record.Epoch)))
            {
                WindowsNativeEpochFiles.RequireAbsent(WindowsNativeHandbackStorage.RecordPath(files, item.Record.Epoch));
                WindowsNativeEpochFiles.RequireAbsent(WindowsNativeHandbackStorage.ResolutionPath(files, item.Record.Epoch));
            }
            // A complete owner chain is not proof that its native generations
            // survived. Never reset ordinal/predecessor against retained owners.
            if (firstPreparation && (!owner.IsKnownEmpty || authenticated.Count == 0
                || authenticated.Any(item => !item.Preparation || item.Record.Ordinal != 1 || item.Record.Predecessor != new string('0', 64))))
            { throw new IOException("Unselected history is not a complete first-preparation prefix."); }
            foreach (var item in authenticated)
            {
                if (!item.Preparation && !ManagedMailboxRecovery.Read(files, Path.Combine(files.Root, WindowsNativeEpochTransaction.FrontierName(item.Record))).AsSpan().SequenceEqual(item.Record.Encode()))
                { throw new IOException("Retired generation selection history is missing."); }
                if (item.Record.Ordinal == 1 ? item.Record.Predecessor != new string('0', 64)
                    : !authenticated.Any(prior => !prior.Preparation && prior.Record.Ordinal == item.Record.Ordinal - 1
                        && prior.Record.Installation == item.Record.Installation && NativeCanonical.Digest(prior.Record.Encode()) == item.Record.Predecessor))
                { throw new IOException("Native generation predecessor history is incomplete."); }
            }
            foreach (var reference in owner.References)
            {
                if (!authenticated.Any(item => !item.Preparation && item.Record.Epoch == reference.Epoch
                    && item.Record.Installation == reference.Installation && item.Record.ServiceId == reference.Service))
                { throw new IOException("Owner history has no matching retained native generation."); }
            }
            foreach (var selection in selections)
            {
                if (!authenticated.Any(item => !item.Preparation
                    && item.Record with { Phase = selection.Phase, Readiness = selection.Readiness } == selection))
                { throw new IOException("Selected history has no matching retained native generation."); }
            }
            var commitments = new List<string>();
            foreach (var item in authenticated)
            {
                byte[] proof = WindowsNativeAttemptHistory.RequireDrained(files, item.Record, item.Directory, item.Package.ServiceArtifact, owner, preparationOrigin: item.Preparation ? item.Record.Encode() : null);
                if (item.Preparation && Directory.EnumerateFileSystemEntries(Path.Combine(item.Directory, "attempts")).Any(p => Path.GetFileName(p) != "custody.lock"))
                { throw new IOException("Unselected preparation contains a possible native attempt."); }
                byte[] reservation = ManagedMailboxRecovery.Read(files, item.Directory + ".reserved");
                generations.Add(item.Record.Epoch, new(item.Record, proof, reservation, item.Preparation, item.Package.ServiceArtifact.RequiresMailboxHandoff, item.Package.ManagedVerificationProjection, item.Package.SourceRevision, item.PreparationBinding));
                commitments.Add(NativeCanonical.Digest(proof));
            }
            namespaceProof = NativeCanonical.Lines("OpenBlade.ManagedNativeNamespace.v2", selected is null ? firstPreparation ? "unselected-authenticated-prefix" : "verified-bootstrap" : NativeCanonical.Digest(selected.Encode()),
                ownerCommitment, NativeCanonical.Digest(System.Text.Encoding.ASCII.GetBytes(string.Join(",", commitments.Order(StringComparer.Ordinal)))));
        }
        catch { Dispose(); throw; }
    }
    internal void ReleaseWriter() { writer?.Dispose(); writer = null; }
    internal void RequireSafe()
    {
        if (generations.Any(pair => pair.Value.Preparation && !canceled.Contains(pair.Key)))
        { throw new IOException("Unknown or unmatched native preparation remains fenced."); }
    }
    internal void RequireBinding(NativeEpochRecord epoch, byte[] mapping)
    {
        if (files is null || !generations.TryGetValue(epoch.Epoch, out var generation)
            || !generation.RequiresMailboxHandoff || generation.Record with { Phase = epoch.Phase, Readiness = epoch.Readiness } != epoch)
        { throw new IOException("Transfer epoch/package/reservation is unmatched."); }
        if (generation.PreparationBinding is not null && !generation.PreparationBinding.AsSpan().SequenceEqual(mapping))
        { throw new IOException("Native preparation binding differs from the exact transfer."); }
        byte[]? existing = ManagedMailboxRecovery.ReadOptional(files, Path.Combine(files.Root, epoch.Epoch, "managed-handoff.bin"));
        if (existing is not null && !existing.AsSpan().SequenceEqual(mapping)) { throw new IOException("Transfer mapping differs."); }
    }
    internal byte[] RequireProjection(NativeEpochRecord epoch) =>
        generations.TryGetValue(epoch.Epoch, out var generation) ? generation.Projection
            : throw new IOException("Historical transfer has no authenticated native projection.");

    // This parser establishes retained-byte consistency only. Fresh generation,
    // reservation, owner and attempt custody remain required below.
    internal static (string Disposition, string Epoch, NativeEpochRecord? Record, string[]? Retirement, string? Reservation)
        ParseResolution(ManagedMailboxSelection transfer, NativeEpochRecord? epoch, byte[] proof)
    {
        var f = NativeCanonical.ParseLines(proof, "OpenBlade.ManagedNativeResolution.v2", 7);
        byte[] evidence = Convert.FromBase64String(f[5]);
        if (f[0] != NativeCanonical.Digest(transfer.Encode()) || f[1] is not ("canceled-before-native-use" or "consumed-and-retired")
            || f[3] != NativeCanonical.Digest(evidence) || !NativeCanonical.IsHex(f[4], 64)
            || f[2] != "not-reserved" && !NativeCanonical.IsHex(f[2], 32)
            || epoch is not null && f[2] != epoch.Epoch)
        { throw new IOException("Preserved native resolution differs from its exact transfer."); }
        NativeEpochRecord? record = null; string[]? retired = null; string? reservation = null;
        byte[] spaceBytes = evidence;
        if (f[2] == "not-reserved")
        {
            if (f[1] != "canceled-before-native-use") { throw new IOException("Unreserved resolution cannot claim retirement."); }
        }
        else
        {
            var g = NativeCanonical.ParseLines(evidence, "OpenBlade.ManagedNativeGenerationProof.v2", 5);
            record = NativeEpochRecord.Parse(Convert.FromBase64String(g[0]));
            retired = ParseRetirement(Convert.FromBase64String(g[1]));
            reservation = g[2]; spaceBytes = Convert.FromBase64String(g[3]);
            if (record.Epoch != f[2] || record.Request != transfer.Value.Request || record.Installation != transfer.Value.Installation
                || record.Package != transfer.Value.Package || !NativeCanonical.IsHex(reservation, 64)
                || record.Phase != (f[1] == "canceled-before-native-use" ? NativeEpochPhase.Provisional : NativeEpochPhase.Retired)
                || epoch is not null && record with { Phase = epoch.Phase, Readiness = epoch.Readiness } != epoch
                || retired[0] != record.Epoch || retired[1] != NativeCanonical.Digest(record.Encode())
                || !NativeCanonical.IsHex(retired[2], 64) || retired[3] is not ("submission-retained" or "never-submitted")
                || retired[4] is not ("no-admitted-metadata-attempt" or "metadata-attempts-classified" or "all-metadata-attempts-drained")
                || !NativeCanonical.IsHex(retired[5], 64) || retired[6] != f[4])
            { throw new IOException("Preserved generation proof differs from its transfer."); }
        }
        bool absent = spaceBytes.AsSpan().SequenceEqual(NativeCanonical.Lines("OpenBlade.ManagedNativeNamespace.v2", "native-namespace-absent", f[4]));
        if (absent)
        {
            if (record is not null || f[4] != NativeCanonical.Digest(NativeCanonical.Lines("OpenBlade.NativeOwnerAbsent.v2", "verified-native-namespace-absent")))
            { throw new IOException("Preserved absent namespace proof differs."); }
        }
        else
        {
            var space = NativeCanonical.ParseLines(spaceBytes, "OpenBlade.ManagedNativeNamespace.v2", 4);
            if (space[0] is not ("unselected-authenticated-prefix" or "verified-bootstrap") && !NativeCanonical.IsHex(space[0], 64)
                || space[1] != f[4] || !NativeCanonical.IsHex(space[2], 64))
            { throw new IOException("Preserved native namespace proof differs."); }
        }
        return (f[1], f[2], record, retired, reservation);
    }
    internal static string[] ParseRetirement(byte[] proof)
    {
        // v4 adds the handback commitment after v3's thermal generation.
        bool v4 = proof.AsSpan().StartsWith("OpenBlade.NativeRetirementProof.v4\n"u8);
        bool v3 = v4 || proof.AsSpan().StartsWith("OpenBlade.NativeRetirementProof.v3\n"u8);
        var fields = NativeCanonical.ParseLines(proof, v4 ? "OpenBlade.NativeRetirementProof.v4" : v3 ? "OpenBlade.NativeRetirementProof.v3" : "OpenBlade.NativeRetirementProof.v2", v4 ? 10 : v3 ? 9 : 8);
        if (v3 && !NativeCanonical.IsHex(fields[7], 64) || v4 && !NativeCanonical.IsHex(fields[8], 64)) { throw new IOException("Thermal settlement commitment differs."); }
        return fields;
    }
    internal void RequirePreservedResolution(ManagedMailboxSelection transfer, NativeEpochRecord? epoch, byte[] proof, string disposition)
    {
        var retained = ParseResolution(transfer, epoch, proof);
        if (retained.Disposition != disposition || retained.Epoch != (epoch?.Epoch ?? "not-reserved"))
        { throw new IOException("Preserved native resolution differs from its exact transfer."); }
        if (epoch is null) { return; }
        var generation = generations[epoch.Epoch];
        var fresh = ParseRetirement(generation.Proof);
        // Owner history can advance; today's owner is independently fenced.
        if (retained.Record != generation.Record || retained.Reservation != NativeCanonical.Digest(generation.Reservation)
            || !retained.Retirement![..6].SequenceEqual(fresh[..6])
            || !ThermalMatches(retained.Retirement, fresh, WindowsNativeThermalHistory.Empty(generation.Record.Installation, generation.Record.Epoch, generation.Record.ServiceId)))
        { throw new IOException("Preserved generation/attempt/reservation proof differs from fresh custody."); }
    }
    // Fresh v3/v4 admission independently proves the complete original history.
    // A preserved v2 may survive only beside a fresh v3 with no thermal rows; it
    // never supplies a settlement or handback guarantee. v3 and v4 compare every
    // thermal field, so a retained v3 never matches a fresh v4, nor the reverse.
    internal static bool ThermalMatches(string[] retained, string[] fresh, string emptyThermal) =>
        retained.Length == fresh.Length && retained[7..].SequenceEqual(fresh[7..])
        || retained.Length == 7 && fresh.Length == 8 && fresh[7] == emptyThermal;
    internal (NativeEpochRecord Epoch, byte[] Projection, string Source) RequireLegacyBinding(byte[] mapping)
    {
        var f = NativeCanonical.ParseLines(mapping, "OpenBlade.ManagedMailboxEpoch.v1", 7);
        if (!NativeCanonical.IsHex(f[0], 64) || !NativeCanonical.IsHex(f[1], 64)
            || !NativeCanonical.IsHex(f[2], 32) || !NativeCanonical.IsHex(f[3], 32)
            || !NativeCanonical.IsHex(f[4], 32) || !NativeCanonical.IsHex(f[5], 64)
            || !generations.TryGetValue(f[2], out var generation))
        { throw new IOException("Legacy transfer mapping is unmatched."); }
        var epoch = generation.Record with { Phase = NativeEpochPhase.Provisional, Readiness = new string('0', 64) };
        if (epoch.Installation != f[3] || epoch.Request != f[4] || epoch.Package != f[5])
        { throw new IOException("Legacy request/package differs from signed native origin."); }
        RequireBinding(epoch, mapping);
        // A legacy mapping cannot be reconstructed from absence. Its SAME
        // immutable native copy is mandatory; no receipt is rebound or replayed.
        if (!ManagedMailboxRecovery.Read(files!, Path.Combine(files!.Root, epoch.Epoch, "origin.bin")).AsSpan().SequenceEqual(epoch.Encode())
            || !ManagedMailboxRecovery.Read(files!, Path.Combine(files!.Root, epoch.Epoch, "managed-handoff.bin")).AsSpan().SequenceEqual(mapping))
        { throw new IOException("Legacy original mapping is missing."); }
        return (epoch, generation.Projection, generation.Source);
    }

    internal void ConfirmResolved(NativeEpochRecord? epoch)
    { if (epoch is not null && generations[epoch.Epoch].Preparation) { canceled.Add(epoch.Epoch); } }

    internal NativeEpochRecord? MatchUnboundPreparation(ManagedMailboxSelection transfer)
    {
        var matches = generations.Values.Where(g => g.Record.Request == transfer.Value.Request).ToArray();
        if (matches.Length == 0) { return null; }
        if (matches.Length != 1 || !matches[0].Preparation) { throw new IOException("Unmatched native request history remains fenced."); }
        var epoch = matches[0].Record;
        // This exact native copy was published under handoff exclusion BEFORE
        // origin. It positively binds the crash gap before managed epoch-intent;
        // a bare origin/reservation or guessed request is never sufficient.
        byte[] mapping = ManagedMailboxRecovery.Mapping(transfer, epoch);
        RequireBinding(epoch, mapping);
        if (matches[0].PreparationBinding is null) { RequirePublishedMapping(epoch, mapping); }
        return epoch;
    }
    internal void RequirePublishedMapping(NativeEpochRecord epoch, byte[] mapping)
    {
        RequireBinding(epoch, mapping);
        if (!ManagedMailboxRecovery.Read(files!, Path.Combine(files!.Root, epoch.Epoch, "origin.bin")).AsSpan().SequenceEqual(epoch.Encode())
            || !ManagedMailboxRecovery.Read(files!, Path.Combine(files!.Root, epoch.Epoch, "managed-handoff.bin")).AsSpan().SequenceEqual(mapping))
        { throw new IOException("Historical native mapping is missing or differs."); }
    }
    internal void PublishMapping(NativeEpochRecord epoch, byte[] mapping)
    {
        RequireBinding(epoch, mapping);
        ManagedMailboxRecovery.ExactOrPublish(files!, Path.Combine(files!.Root, epoch.Epoch, "managed-handoff.bin"), mapping);
        if (generations[epoch.Epoch].PreparationBinding is not null)
        { ManagedMailboxRecovery.ExactOrPublish(files!, Path.Combine(files!.Root, epoch.Epoch, "origin.bin"), epoch.Encode()); }
    }
    internal (string Disposition, byte[] Proof) Resolve(ManagedMailboxSelection transfer, NativeEpochRecord? epoch)
    {
        string disposition;
        byte[] evidence;
        if (epoch is null)
        {
            if (generations.Values.Any(g => g.Record.Request == transfer.Value.Request))
            { throw new IOException("Unmatched native reservation exists for the original request."); }
            disposition = "canceled-before-native-use";
            evidence = namespaceProof;
        }
        else
        {
            var generation = generations.GetValueOrDefault(epoch.Epoch) ?? throw new IOException("Bound native reservation is missing.");
            if (generation.Record.Request != transfer.Value.Request || generation.Record.Package != transfer.Value.Package
                || generation.Record.Installation != transfer.Value.Installation)
            { throw new IOException("Native transfer origin differs from its exact request."); }
            disposition = generation.Preparation ? "canceled-before-native-use" : "consumed-and-retired";
            evidence = NativeCanonical.Lines("OpenBlade.ManagedNativeGenerationProof.v2", Convert.ToBase64String(generation.Record.Encode()),
                Convert.ToBase64String(generation.Proof), NativeCanonical.Digest(generation.Reservation), Convert.ToBase64String(namespaceProof));
        }
        byte[] proof = NativeCanonical.Lines("OpenBlade.ManagedNativeResolution.v2", NativeCanonical.Digest(transfer.Encode()), disposition,
            epoch?.Epoch ?? "not-reserved", NativeCanonical.Digest(evidence), ownerCommitment, Convert.ToBase64String(evidence));
        return (disposition, proof);
    }
    public void Dispose()
    {
        for (int i = held.Count - 1; i >= 0; i--) { held[i].Dispose(); }
        held.Clear(); writer?.Dispose(); writer = null; files?.Dispose();
    }
}
