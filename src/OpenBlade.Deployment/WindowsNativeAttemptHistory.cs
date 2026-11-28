namespace OpenBlade.Deployment;

// Caller holds both the epoch writer and the service's SAME custody lock. This
// certifies metadata retirement only. Firmware ownership is a separate fence.
[System.Runtime.Versioning.SupportedOSPlatform("windows")]
internal static class WindowsNativeAttemptHistory
{
    internal const int MaximumAttempts = 4096;
    internal const int MaximumEntries = 1 + 2 * MaximumAttempts;

    internal static byte[] RequireDrained(WindowsNativeEpochFiles files, NativeEpochRecord record, string directory,
        NativeArtifact service, WindowsNativeTrustLifecycle.OwnerLease owner, uint? expectedPid = null, long? expectedCreated = null, byte[]? preparationOrigin = null)
    {
        if (!service.HasStartupCustody)
        { throw new NativeRecoveryException(NativeRecoveryReason.LegacyLaunchEvidenceUnavailable, "This native artifact predates durable launch custody; stopped-state drain is unproven."); }
        byte[] Read(string path, int max = 16384)
        { using var held = files.OpenFile(path); return WindowsNativeEpochFiles.ReadBounded(held, max); }
        byte[] origin = preparationOrigin is null ? Read(Path.Combine(directory, "origin.bin"))
            : ManagedMailboxRecovery.ReadOptional(files, Path.Combine(directory, "origin.bin")) ?? preparationOrigin;
        if (preparationOrigin is not null && !origin.AsSpan().SequenceEqual(preparationOrigin))
        { throw new IOException("Native preparation origin differs from its positive intent."); }
        byte[] policy = Read(Path.Combine(directory, "startup-policy.bin"));
        if (!policy.AsSpan().SequenceEqual(NativeCanonical.Lines("OpenBlade.NativeStartupPolicy.v1", record.Epoch,
            NativeCanonical.Digest(origin), record.ServiceId, service.StartupPolicy)))
        { throw new NativeRecoveryException(NativeRecoveryReason.UnknownAttemptHistory, "Native startup custody policy differs from the signed service artifact."); }
        string attempts = Path.Combine(directory, "attempts");
        string[] entries = Directory.EnumerateFileSystemEntries(attempts).Take(MaximumEntries + 1).Order(StringComparer.Ordinal).ToArray();
        if (entries.Length > MaximumEntries) { throw new NativeRecoveryException(NativeRecoveryReason.AttemptHistoryOverLimit, "Native attempt history exceeds the shared 4096-attempt bound; history was preserved."); }
        var launches = new Dictionary<string, byte[]>();
        var drains = new Dictionary<string, byte[]>();
        var exits = new Dictionary<string, byte[]>();
        foreach (string path in entries)
        {
            string name = Path.GetFileName(path);
            if (name == "custody.lock") { continue; }
            if (name.StartsWith("launch-", StringComparison.Ordinal) && name.EndsWith(".bin", StringComparison.Ordinal)) { launches.Add(name[7..], Read(path)); }
            else if (name.StartsWith("drain-", StringComparison.Ordinal) && name.EndsWith(".bin", StringComparison.Ordinal)) { drains.Add(name[6..], Read(path)); }
            else if (name.StartsWith("exited-", StringComparison.Ordinal) && name.EndsWith(".bin", StringComparison.Ordinal)) { exits.Add(name[7..], Read(path)); }
            else { throw new NativeRecoveryException(NativeRecoveryReason.UnknownAttemptHistory, "Unknown native attempt history was preserved."); }
        }
        // The caller publishes a pending exit only after this proof and every
        // other admission check succeed; until then it is checked as present.
        if (owner.PendingExit is { } pending && pending.Epoch == record.Epoch
            && !exits.TryAdd(Path.GetFileName(pending.Path)[7..], pending.Bytes))
        { throw new NativeRecoveryException(NativeRecoveryReason.MissingAttemptDrain, "A pending native exit record is already present."); }
        if (launches.Count > MaximumAttempts || drains.Count > MaximumAttempts)
        { throw new NativeRecoveryException(NativeRecoveryReason.AttemptHistoryOverLimit, "Native launch/drain history exceeds the shared attempt budget."); }
        string submission = Path.Combine(directory, "submission.bin");
        bool submitted;
        try { _ = File.GetAttributes(submission); submitted = true; }
        catch (FileNotFoundException) { submitted = false; }
        if (submitted)
        {
            if (!Read(submission).AsSpan().SequenceEqual(NativeCanonical.Lines("OpenBlade.NativeStartupSubmission.v1", record.Epoch,
                record.Request, NativeCanonical.Digest(origin), record.Package, record.ServiceId)))
            { throw new NativeRecoveryException(NativeRecoveryReason.UnknownAttemptHistory, "Native startup submission differs from its original attempt."); }
        }
        // Metadata recovery may have changed Committed to Unresolved. Preserve
        // the original readiness declaration instead of checking only current.
        bool committedHistory = record.Phase == NativeEpochPhase.Committed;
        string committedPath = Path.Combine(directory, "Committed.bin");
        bool hasCommitted;
        try { _ = File.GetAttributes(committedPath); hasCommitted = true; }
        catch (FileNotFoundException) { hasCommitted = false; }
        if (hasCommitted)
        {
            var committed = NativeEpochRecord.Parse(Read(committedPath));
            if (committed.Phase != NativeEpochPhase.Committed
                || committed with { Phase = record.Phase, Readiness = record.Readiness } != record)
            { throw new NativeRecoveryException(NativeRecoveryReason.UnknownAttemptHistory, "Preserved committed native history differs from the exact attempt."); }
            committedHistory = true;
        }
        if (committedHistory && (!submitted || launches.Count == 0))
        { throw new NativeRecoveryException(NativeRecoveryReason.UnknownAttemptHistory, "Committed native readiness contradicts missing startup/launch history; evidence was preserved."); }
        if (drains.Keys.Any(name => !launches.ContainsKey(name)) || exits.Keys.Any(name => !launches.ContainsKey(name) || drains.ContainsKey(name))
            || !submitted && launches.Count != 0)
        { throw new NativeRecoveryException(NativeRecoveryReason.MissingAttemptDrain, "Native launch/submission/drain history is incomplete; no startup was replayed."); }
        var commitments = new List<string>();
        bool matchedOriginal = expectedPid is null;
        bool abandoned = false;
        foreach (var (name, bytes) in launches.OrderBy(pair => pair.Key, StringComparer.Ordinal))
        {
            string[] f = NativeCanonical.ParseLines(bytes, "OpenBlade.NativeServiceLaunch.v1", 11);
            var identity = NativeStartupOwner.Parse(NativeCanonical.Lines("OpenBlade.NativeStartupOwner.v2", f[0], f[2], f[5], f[6], f[7], f[9]));
            if (f[0] != record.Epoch || f[1] != NativeCanonical.Digest(origin) || f[2] != record.Request || f[3] != record.Package
                || f[4] != record.ServiceId || f[7] != "0" || f[8] != "LocalSystem-primary-session0"
                || identity.Path != Path.Combine(directory, "app", "OpenBlade.Service.exe") || name != $"{identity.ProcessId}-{identity.Created}.bin")
            { throw new NativeRecoveryException(NativeRecoveryReason.UnknownAttemptHistory, "Native launch identity differs from the original package/process attempt."); }
            bool drained = drains.TryGetValue(name, out byte[]? drain);
            if (drained && !drain!.AsSpan().SequenceEqual(NativeCanonical.Lines(service.RequiresMailboxHandoff ? "OpenBlade.NativeTransportDrain.v1" : "OpenBlade.NativeStatusDrain.v2",
                record.Epoch, NativeCanonical.Digest(bytes), f[5], f[6])))
            { throw new NativeRecoveryException(NativeRecoveryReason.MissingAttemptDrain, "The exact native process drain is malformed or mismatched."); }
            // An exit marker is published only under this epoch's reacquired
            // custody for the terminal unretired owner process, after admission.
            bool exited = exits.TryGetValue(name, out byte[]? exit);
            if (exited && !exit!.AsSpan().SequenceEqual(Exited(record.Epoch, bytes, f[5], f[6])))
            { throw new NativeRecoveryException(NativeRecoveryReason.MissingAttemptDrain, "The exact native process exit record is malformed or mismatched."); }
            if (!drained && !exited && !service.MetadataOnly)
            { throw new NativeRecoveryException(NativeRecoveryReason.MissingAttemptDrain, "This historical artifact cannot authenticate metadata abandonment."); }
            abandoned |= !drained;
            matchedOriginal |= identity.ProcessId == expectedPid && identity.Created == expectedCreated;
            commitments.Add(NativeCanonical.Digest(bytes));
            commitments.Add(drained ? NativeCanonical.Digest(drain!) : exited ? NativeCanonical.Digest(exit!) : new string('0', 64));
            commitments.Add(drained ? "drained" : exited ? "exited-after-custody" : "abandoned-metadata");
        }
        if (!matchedOriginal) { throw new NativeRecoveryException(NativeRecoveryReason.MissingAttemptDrain, "The retained original process does not match durable native launch history."); }
        // No launch is accepted only because this signed entry recipe MUST hold
        // custody and durably record its attempt before dispatcher/factory. A
        // pending submission cannot cross admission after retirement under lock.
        string thermal = owner.Thermal.Generation(record, service, owner.References, launches);
        string[] fields = [record.Epoch, NativeCanonical.Digest(record.Encode()),
            NativeCanonical.Digest(origin), submitted ? "submission-retained" : "never-submitted",
            launches.Count == 0 ? "no-admitted-metadata-attempt" : abandoned ? "metadata-attempts-classified" : "all-metadata-attempts-drained",
            NativeCanonical.Digest(System.Text.Encoding.ASCII.GetBytes("OpenBlade.NativeAttemptSet.v2\n" + string.Join('\n', commitments) + "\n")), owner.Commitment];
        // v4 exists only when this generation hands unsettled obligations to
        // managed. Settled history keeps its exact v3 bytes.
        if (owner.Thermal.Handback is { IsEmpty: false } handback && handback.Epoch == record.Epoch)
        {
            if (!service.ThermalMutation) { throw new NativeRecoveryException(NativeRecoveryReason.UnknownAttemptHistory, "Native handback history has no thermal producer."); }
            return NativeCanonical.Lines("OpenBlade.NativeRetirementProof.v4", [.. fields, thermal, handback.Commitment]);
        }
        return NativeCanonical.Lines(service.ThermalMutation ? "OpenBlade.NativeRetirementProof.v3" : "OpenBlade.NativeRetirementProof.v2", service.ThermalMutation ? [.. fields, thermal] : fields);

    }
    internal static byte[] Exited(string epoch, byte[] launch, string pid, string created) =>
        NativeCanonical.Lines("OpenBlade.NativeAttemptExited.v1", epoch, NativeCanonical.Digest(launch), pid, created);
}
