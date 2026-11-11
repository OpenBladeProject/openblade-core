using System.Globalization;
using System.Security.Cryptography;

namespace OpenBlade.Deployment;

internal enum NativeEpochPhase { Provisional, Committed, Unresolved, Retired }
internal enum NativeEpochBoundary { Reserved, PackageWritten, OriginWritten, PhaseWritten, FrontierWritten, SelectionPublished }

internal sealed record NativeEpochRecord(NativeEpochPhase Phase, string Epoch, string Predecessor,
    long Ordinal, string Installation, string Package, string Request, string ServiceId, string AgentId,
    string Readiness, string Owner)
{
    internal byte[] Encode() => NativeCanonical.Lines("OpenBlade.NativeEpoch.v1", Phase.ToString(), Epoch,
        Predecessor, Ordinal.ToString(CultureInfo.InvariantCulture), Installation, Package, Request,
        ServiceId, AgentId, Readiness, Owner);

    internal static NativeEpochRecord Parse(byte[] bytes)
    {
        string[] f = NativeCanonical.ParseLines(bytes, "OpenBlade.NativeEpoch.v1", 12);
        if (!Enum.TryParse<NativeEpochPhase>(f[0], out var phase) || !Enum.IsDefined(phase) || phase.ToString() != f[0]
            || !NativeCanonical.IsHex(f[1], 32) || f[1].All(c => c == '0') || !NativeCanonical.IsHex(f[2], 64)
            || !long.TryParse(f[3], NumberStyles.None, CultureInfo.InvariantCulture, out long ordinal) || ordinal < 1
            || ordinal.ToString(CultureInfo.InvariantCulture) != f[3]
            || !NativeCanonical.IsHex(f[4], 32) || !NativeCanonical.IsHex(f[5], 64) || !NativeCanonical.IsHex(f[6], 32)
            || !NativeCanonical.IsHex(f[9], 64) || !NativeCanonical.IsHex(f[10], 64)
            || f[7].Length != 72 || !f[7].EndsWith("-service", StringComparison.Ordinal) || !NativeCanonical.IsHex(f[7][..64], 64)
            || f[8].Length != 70 || !f[8].EndsWith("-agent", StringComparison.Ordinal) || !NativeCanonical.IsHex(f[8][..64], 64)
            || (phase == NativeEpochPhase.Committed) == (f[9] == new string('0', 64)))
        { throw new InvalidDataException("Native epoch record fields are invalid."); }
        return new(phase, f[1], f[2], ordinal, f[4], f[5], f[6], f[7], f[8], f[9], f[10]);
    }
}

// Exclusive writer ownership lasts through Setup's transaction. A lock is never
// interpreted as proof that an old native worker drained or that HID is available.
[System.Runtime.Versioning.SupportedOSPlatform("windows")]
internal sealed class WindowsNativeEpochTransaction : IDisposable
{
    private readonly WindowsNativeEpochFiles files;
    private readonly FileStream writer;
    private readonly WindowsNativeEpochFiles.DirectoryLease generation;
    private readonly List<IDisposable> evidence = [];
    private byte[] selected;
    private NativeEpochRecord current;
    private bool denied;
    internal string GenerationDirectory { get; }
    internal string ServicePath => Path.Combine(GenerationDirectory, "app", "OpenBlade.Service.exe");
    internal string AgentPath => Path.Combine(GenerationDirectory, "app", "OpenBlade.SessionAgent.exe");
    internal NativeEpochRecord Record => current;
    internal byte[] ProvisionalCommitment => SHA256.HashData(selected);

    private WindowsNativeEpochTransaction(WindowsNativeEpochFiles files, FileStream writer,
        WindowsNativeEpochFiles.DirectoryLease generation, string directory, NativeEpochRecord current)
    {
        this.files = files;
        this.writer = writer;
        this.generation = generation;
        GenerationDirectory = directory;
        this.current = current;
        selected = current.Encode();
    }

    internal static WindowsNativeEpochTransaction Begin(WindowsNativeEpochFiles files, WindowsNativePackage package,
        Guid request, string? explicitlyRecoverCurrent = null,
        Action<NativeEpochBoundary>? testCrashBoundary = null)
    {
        if (request == Guid.Empty) { throw new InvalidDataException("Native epoch requires a fresh Setup request."); }
        var writer = files.AcquireWriter();
        WindowsNativeEpochFiles.DirectoryLease? generation = null;
        try
        {
            byte[]? previous = files.ReadCurrent();
            RequireFrontier(files, previous);
            var predecessor = previous is null ? null : NativeEpochRecord.Parse(previous);
            if (predecessor is { Phase: NativeEpochPhase.Provisional or NativeEpochPhase.Unresolved }
                && (explicitlyRecoverCurrent is null || NativeCanonical.Digest(previous!) != explicitlyRecoverCurrent))
            { throw new InvalidDataException("Unresolved native publication requires explicit recovery; no fallback or replay."); }
            if (explicitlyRecoverCurrent is not null && (previous is null || NativeCanonical.Digest(previous) != explicitlyRecoverCurrent))
            { throw new InvalidDataException("Recovery selection changed."); }
            if (predecessor is not null && predecessor.Installation != package.Release.Manifest.InstallationId.ToString("N"))
            { throw new InvalidDataException("Native epoch installation substitution."); }
            string epoch = Guid.NewGuid().ToString("N");
            string directory = Path.Combine(files.Root, epoch);
            files.CreateDirectoryOnce(directory);
            generation = files.RetainDirectory(directory);
            testCrashBoundary?.Invoke(NativeEpochBoundary.Reserved);
            string app = Path.Combine(directory, "app");
            files.CreateDirectoryOnce(app);
            void Write(string name, byte[] bytes) { using var stream = new MemoryStream(bytes, false); files.WriteEvidence(Path.Combine(directory, name), stream); }
            Write("release.json", package.Manifest);
            Write("release.sig", package.Signature);
            files.Publish(Path.Combine(directory, "package.bin"), package.Record);
            Write("roles.bin", package.Inventory);
            using (var service = new MemoryStream(package.ServiceImage.ToArray(), false)) { files.WriteEvidence(Path.Combine(app, "OpenBlade.Service.exe"), service); }
            using (var agent = new MemoryStream(package.AgentImage.ToArray(), false)) { files.WriteEvidence(Path.Combine(app, "OpenBlade.SessionAgent.exe"), agent); }
            using (var output = new FileStream(Path.Combine(directory, "package.exe"), FileMode.CreateNew, FileAccess.Write,
                FileShare.None, 81920, FileOptions.WriteThrough)) { package.CopyPackageTo(output); output.Flush(true); }
            // Refuse conflicting mappings across every retained epoch, including
            // abandoned preparations. An artifact ID never acquires a new digest.
            foreach (string other in Directory.EnumerateDirectories(files.Root))
            {
                string record = Path.Combine(other, "package.bin");
                if (!File.Exists(record)) { continue; }
                using var priorDirectory = files.RetainDirectory(other);
                using var held = files.OpenFile(record);
                string[] prior = NativeCanonical.ParseLines(WindowsNativeEpochFiles.ReadBounded(held, 16384), "OpenBlade.NativePackage.v1", 12);
                foreach (string encoded in prior[8..10])
                {
                    byte[] priorArtifact = Convert.FromBase64String(encoded);
                    string[] artifact = NativeCanonical.ParseLines(priorArtifact, "OpenBlade.NativeArtifactRecord.v1", 11);
                    foreach (var candidate in new[] { package.Service, package.Agent })
                    {
                        if (artifact[0] == candidate.Id && !priorArtifact.AsSpan().SequenceEqual(candidate.Encode()))
                        { throw new InvalidDataException("Native artifact ID already maps to different bytes or provenance."); }
                    }
                }
            }
            testCrashBoundary?.Invoke(NativeEpochBoundary.PackageWritten);
            var value = new NativeEpochRecord(NativeEpochPhase.Provisional, epoch,
                previous is null ? new string('0', 64) : NativeCanonical.Digest(previous),
                checked((predecessor?.Ordinal ?? 0) + 1), package.Release.Manifest.InstallationId.ToString("N"),
                NativeCanonical.Digest(package.Record), request.ToString("N"), package.Service.Id, package.Agent.Id,
                new string('0', 64), Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32)));
            byte[] selection = value.Encode();
            files.Publish(Path.Combine(directory, "origin.bin"), selection);
            testCrashBoundary?.Invoke(NativeEpochBoundary.OriginWritten);
            files.Publish(Path.Combine(directory, "Provisional.bin"), selection);
            testCrashBoundary?.Invoke(NativeEpochBoundary.PhaseWritten);
            if (!(files.ReadCurrent() ?? []).AsSpan().SequenceEqual(previous ?? []))
            { throw new InvalidDataException("Native predecessor selection changed while preparing publication."); }
            PublishFrontier(files, value, selection);
            testCrashBoundary?.Invoke(NativeEpochBoundary.FrontierWritten);
            files.PublishCurrent(selection);
            testCrashBoundary?.Invoke(NativeEpochBoundary.SelectionPublished);
            if (!(files.ReadCurrent() ?? []).AsSpan().SequenceEqual(selection))
            { throw new IOException("Native provisional publication readback is unresolved."); }
            var transaction = new WindowsNativeEpochTransaction(files, writer, generation, directory, value);
            generation = null;
            try { transaction.RetainEvidence(); return transaction; }
            catch { transaction.Dispose(); throw; }
        }
        catch { generation?.Dispose(); writer.Dispose(); throw; }
    }

    private void RetainEvidence()
    {
        evidence.Add(files.RetainDirectory(Path.Combine(GenerationDirectory, "app")));
        foreach (string name in new[] { "release.json", "release.sig", "package.bin", "roles.bin", "package.exe", "origin.bin",
            @"app\OpenBlade.Service.exe", @"app\OpenBlade.SessionAgent.exe" })
        { evidence.Add(files.OpenFile(Path.Combine(GenerationDirectory, name))); }
    }

    internal void RequireProvisional()
    {
        RequireCurrent();
        if (current.Phase != NativeEpochPhase.Provisional) { throw new InvalidOperationException("Native startup is not provisional."); }
    }

    internal void CommitAfterBoundReadiness(string readinessDigest, Action<NativeEpochBoundary>? testCrashBoundary = null)
    {
        RequireProvisional();
        if (!NativeCanonical.IsHex(readinessDigest, 64) || readinessDigest.All(c => c == '0'))
        { throw new InvalidDataException("Native commit lacks correctly bound readiness."); }
        Transition(current with { Phase = NativeEpochPhase.Committed, Readiness = readinessDigest }, testCrashBoundary);
    }

    internal void MarkUnresolved()
    {
        RequireCurrent();
        if (current.Phase != NativeEpochPhase.Provisional) { throw new InvalidOperationException("Only a provisional epoch may become unresolved."); }
        Transition(current with { Phase = NativeEpochPhase.Unresolved });
    }

    // Retires package selection only, never an ownership/drain certificate.
    internal void RetirePackageSelection()
    {
        RequireCurrent();
        if (current.Phase != NativeEpochPhase.Committed) { throw new InvalidOperationException("Package selection is not committed."); }
        Transition(current with { Phase = NativeEpochPhase.Retired, Readiness = new string('0', 64) });
    }

    private void Transition(NativeEpochRecord next, Action<NativeEpochBoundary>? testCrashBoundary = null)
    {
        try
        {
            byte[] bytes = next.Encode();
            files.Publish(Path.Combine(GenerationDirectory, next.Phase + ".bin"), bytes);
            testCrashBoundary?.Invoke(NativeEpochBoundary.PhaseWritten);
            RequireCurrent();
            PublishFrontier(files, next, bytes);
            testCrashBoundary?.Invoke(NativeEpochBoundary.FrontierWritten);
            files.PublishCurrent(bytes);
            testCrashBoundary?.Invoke(NativeEpochBoundary.SelectionPublished);
            if (!(files.ReadCurrent() ?? []).AsSpan().SequenceEqual(bytes)) { throw new IOException("Native phase publication readback differs."); }
            selected = bytes;
            current = next;
        }
        catch { denied = true; throw; }
    }

    private static string FrontierName(NativeEpochRecord record) => $"selection-{record.Ordinal:D20}-{(int)record.Phase}.bin";

    private static void PublishFrontier(WindowsNativeEpochFiles files, NativeEpochRecord record, byte[] bytes)
        => files.Publish(Path.Combine(files.Root, FrontierName(record)), bytes);

    private static byte[]? ReadFrontier(WindowsNativeEpochFiles files)
    {
        string? latest = Directory.EnumerateFiles(files.Root, "selection-*.bin").Order(StringComparer.Ordinal).LastOrDefault();
        if (latest is null) { return null; }
        using var held = files.OpenFile(latest);
        byte[] bytes = WindowsNativeEpochFiles.ReadBounded(held, 16384);
        var record = NativeEpochRecord.Parse(bytes);
        if (Path.GetFileName(latest) != FrontierName(record)) { throw new InvalidDataException("Native publication frontier name differs."); }
        return bytes;
    }

    private static void RequireFrontier(WindowsNativeEpochFiles files, byte[]? expected)
    {
        if (!(ReadFrontier(files) ?? []).AsSpan().SequenceEqual(expected ?? []))
        { throw new IOException("Native publication is interrupted or selection was replayed; explicit recovery is required."); }
    }

    internal static void RecoverUnresolvedPublication(WindowsNativeEpochFiles files, string expectedFrontier,
        Action<NativeEpochBoundary>? testCrashBoundary = null)
    {
        using var owner = files.AcquireWriter();
        byte[] prior = ReadFrontier(files) ?? throw new InvalidDataException("No native publication to recover.");
        if (NativeCanonical.Digest(prior) != expectedFrontier) { throw new InvalidDataException("Native recovery frontier changed."); }
        var record = NativeEpochRecord.Parse(prior);
        if (record.Phase == NativeEpochPhase.Retired)
        { throw new InvalidDataException("Retired native package selection cannot be revived."); }
        var unresolved = record with { Phase = NativeEpochPhase.Unresolved, Readiness = new string('0', 64) };
        byte[] bytes = unresolved.Encode();
        string directory = Path.Combine(files.Root, record.Epoch);
        using var pin = files.RetainDirectory(directory);
        using (var originFile = files.OpenFile(Path.Combine(directory, "origin.bin")))
        {
            var origin = NativeEpochRecord.Parse(WindowsNativeEpochFiles.ReadBounded(originFile, 16384));
            if (origin with { Phase = NativeEpochPhase.Unresolved, Readiness = new string('0', 64) } != unresolved)
            { throw new InvalidDataException("Native recovery origin differs from its publication frontier."); }
        }
        // Explicit recovery reconciles an already-created exact phase/frontier;
        // it never repeats native startup, readiness, retirement or a hardware write.
        EnsureExactOrPublish(files, Path.Combine(directory, "Unresolved.bin"), bytes);
        testCrashBoundary?.Invoke(NativeEpochBoundary.PhaseWritten);
        EnsureExactOrPublish(files, Path.Combine(files.Root, FrontierName(unresolved)), bytes);
        testCrashBoundary?.Invoke(NativeEpochBoundary.FrontierWritten);
        if (!(files.ReadCurrent() ?? []).AsSpan().SequenceEqual(bytes)) { files.PublishCurrent(bytes); }
        testCrashBoundary?.Invoke(NativeEpochBoundary.SelectionPublished);
        RequireFrontier(files, files.ReadCurrent());
    }

    private static void EnsureExactOrPublish(WindowsNativeEpochFiles files, string path, byte[] bytes)
    {
        if (File.Exists(path))
        {
            using var existing = files.OpenFile(path);
            WindowsNativeEpochFiles.RequireExact(existing, bytes);
        }
        else { files.Publish(path, bytes); }
    }

    private void RequireCurrent()
    {
        if (denied) { throw new IOException("Native transaction lost evidence; denial is sticky."); }
        try
        {
            RequireFrontier(files, selected);
            if (!(files.ReadCurrent() ?? []).AsSpan().SequenceEqual(selected)) { throw new IOException("Native selection is no longer this epoch."); }
        }
        catch { denied = true; throw; }
    }

    public void Dispose()
    {
        denied = true;
        for (int i = evidence.Count - 1; i >= 0; i--) { evidence[i].Dispose(); }
        evidence.Clear();
        generation.Dispose();
        writer.Dispose();
        // A provisional owner disappearing leaves the durable phase provisional.
        // Reboot readers refuse it; disposal does not fabricate a successful rollback.
    }
}
