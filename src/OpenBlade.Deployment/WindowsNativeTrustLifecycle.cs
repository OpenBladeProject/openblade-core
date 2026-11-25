using System.Collections.Immutable;

namespace OpenBlade.Deployment;

[System.Runtime.Versioning.SupportedOSPlatform("windows")]
public static class WindowsNativeTrustLifecycle
{
    // Keep the same exclusion as managed/native mailbox custody throughout
    // maintenance. A check-and-release permits a Service restart between file
    // replacements; enrollment is itself held by an authenticated managed run.
    public static IDisposable AcquireManagedEnrollmentChangeCustody()
    {
        var native = ManagedMailboxNativeCustody.Acquire();
        IDisposable? legacy = null, managed = null;
        try
        {
            legacy = WindowsManagedMailboxRun.AcquireLegacyExclusion(native);
            managed = AcquireManagedEnrollmentChangeCustody(WindowsNativeEpochFiles.CreateOrOpenManagedHandoff(), native);
            return new CombinedLease(new ManagedMailboxStartup.Admission(null, managed, legacy), native);
        }
        catch { try { managed?.Dispose(); } finally { legacy?.Dispose(); native.Dispose(); } throw; }
    }
    private static readonly AsyncLocal<ManagedMailboxNativeCustody?> CurrentMaintenance = new();
    private sealed class CombinedLease : IDisposable
    {
        private readonly IDisposable managed;
        private readonly ManagedMailboxNativeCustody native;
        private readonly ManagedMailboxNativeCustody? prior;
        internal CombinedLease(IDisposable managed, ManagedMailboxNativeCustody native)
        { this.managed = managed; this.native = native; prior = CurrentMaintenance.Value; CurrentMaintenance.Value = native; }
        public void Dispose()
        {
            try { managed.Dispose(); }
            finally { CurrentMaintenance.Value = prior; native.Dispose(); }
        }
    }

    public static IDisposable AcquireFirstEnrollmentCustody()
    {
        // Program Files is a trusted creation anchor. This mutex serializes only
        // positively unenrolled creation; it does not claim the running default
        // managed Service's transport or replace any retained signed trust.
        var native = ManagedMailboxNativeCustody.Acquire();
        WindowsNativeEpochFiles? files = null;
        try
        {
            native.RequireSafe();
            files = WindowsNativeEpochFiles.OpenProtected(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles));
            string path = Path.Combine(files.Root, "OpenBlade.LocalDevEnrollment.lock");
            if (ManagedMailboxRecovery.ReadOptional(files, path) is null)
            { files.PublishCustody(path, NativeCanonical.Lines("OpenBlade.FirstEnrollmentMutex.v2")); }
            return new CombinedLease(new EnrollmentLease(files, files.AcquireServiceCustody(path)), native);
        }
        catch { files?.Dispose(); native.Dispose(); throw; }
    }

    internal static IDisposable AcquireManagedEnrollmentChangeCustody(WindowsNativeEpochFiles files, ManagedMailboxNativeCustody? native = null)
    {
        FileStream? exclusion = null;
        try
        {
            exclusion = files.AcquireServiceCustody(Path.Combine(files.Root, "exclusion.lock"));
            if (native is not null) { ManagedMailboxRecovery.Resolve(files, native); }
            else
            {
                var selected = ManagedMailboxRecovery.Selected(files);
                if (selected is not null) { ManagedMailboxRecovery.ValidateTerminal(files, selected); }
            }
            return new EnrollmentLease(files, exclusion);
        }
        catch (Exception error)
        {
            exclusion?.Dispose(); files.Dispose();
            throw new IOException("Stop the managed Service and resolve mailbox transfer custody before enrollment maintenance; no maintenance changes were admitted.", error);
        }
    }
    private sealed class EnrollmentLease(IDisposable files, IDisposable exclusion) : IDisposable
    { public void Dispose() { try { exclusion.Dispose(); } finally { files.Dispose(); } } }

    // Called before any enrollment replacement/removal. This deliberately does
    // not invent a drain certificate from metadata retirement or SCM STOPPED.
    // An active native selection must first complete its coordinated stop path.
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    public static void RequireReadyForEnrollmentChange()
    {
        if (CurrentMaintenance.Value is { } retained)
        {
            // The same original locks remain held through both maintenance
            // checks and all byte mutations; do not reacquire our own mutexes.
            retained.RequireSafe();
            return;
        }
        using var native = ManagedMailboxNativeCustody.Acquire();
        native.RequireSafe();
    }

    internal static void RequireOwnerRetired(WindowsNativeEpochFiles files)
    {
        using var lease = AcquireRetiredOwner(files);
    }
    // Global order: epoch writer -> original service custody -> owner writer.
    // Native Origin retains custody before Store::open and never takes epoch writer.
    // exitedUnderCustody names the epoch whose exact service custody the caller
    // holds. Only that caller may receive the terminal owner process's exit,
    // and it publishes the exit only after its own admission has completed.
    internal static OwnerLease AcquireRetiredOwner(WindowsNativeEpochFiles files, string? exitedUnderCustody = null)
    {
        WindowsNativeEpochFiles.DirectoryLease? directory = null;
        FileStream? writer = null;
        try
        {
            directory = files.RetainDirectory(Path.Combine(files.Root, "owner"));
            writer = files.AcquireOwnerWriter();
            var references = new List<OwnerReference>();
            (string ownerCommitment, OwnerReference? exited, PendingExit? pending) = RequireOwnerRetiredCore(files, references, exitedUnderCustody);
            // Before thermal: a Driver-mode doubt refuses even an exited owner.
            var driverMode = WindowsNativeDriverModeHistory.Read(files, references);
            driverMode.RequireSettled();
            var thermal = WindowsNativeThermalHistory.Read(files, references, exited);
            string commitment = thermal.Present ? NativeCanonical.Digest(NativeCanonical.Lines("OpenBlade.NativeOwnerHistory.v2", ownerCommitment, thermal.Commitment)) : ownerCommitment;
            return new OwnerLease(directory, writer, driverMode.Commit(commitment), references.ToImmutableArray(), thermal, driverMode, pending);
        }
        catch (NativeRecoveryException) { writer?.Dispose(); directory?.Dispose(); throw; }
        catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException or System.Text.Json.JsonException or InvalidOperationException or KeyNotFoundException or FormatException or OverflowException)
        { writer?.Dispose(); directory?.Dispose(); throw new NativeRecoveryException(NativeRecoveryReason.FirmwareOwnerUnresolved, "Native firmware ownership history is active, partial or unknown; no drain was inferred.", error); }
    }
    internal sealed record OwnerReference(string Installation, string Epoch, string Service, string Process, string Nonce, string Active);
    // An exit proven under custody but not yet durable. Admission treats it as
    // present; it is published only after every refusal check has passed.
    internal sealed record PendingExit(string Epoch, string Path, byte[] Bytes);
    internal sealed class OwnerLease(IDisposable directory, IDisposable writer, string commitment, ImmutableArray<OwnerReference> references, WindowsNativeThermalHistory thermal, WindowsNativeDriverModeHistory driverMode, PendingExit? pendingExit = null) : IDisposable
    {
        internal string Commitment { get; } = commitment;
        internal WindowsNativeThermalHistory Thermal { get; } = thermal;
        // Absent, Clear or Owned: an admitted lease never holds a Driver-mode doubt.
        internal WindowsNativeDriverModeHistory DriverMode { get; } = driverMode;
        internal ImmutableArray<OwnerReference> References { get; } = references;
        internal PendingExit? PendingExit { get; } = pendingExit;
        internal bool IsKnownEmpty => References.IsEmpty;
        internal void PublishExit(WindowsNativeEpochFiles files)
        {
            if (PendingExit is not { } exit) { return; }
            if (ManagedMailboxRecovery.ReadOptional(files, exit.Path) is null) { files.Publish(exit.Path, exit.Bytes); }
            using var held = files.OpenFile(exit.Path);
            WindowsNativeEpochFiles.RequireExact(held, exit.Bytes);
        }
        public void Dispose() { try { writer.Dispose(); } finally { directory.Dispose(); } }
    }
    private static bool ValidArtifact(string value) => value.Length == 72 && value.EndsWith("-service", StringComparison.Ordinal) && NativeCanonical.IsHex(value[..64], 64);
    private static bool ValidProcess(string value)
    {
        var parts = value.Split(':');
        return parts.Length == 2 && uint.TryParse(parts[0], out uint pid) && pid > 0
            && ulong.TryParse(parts[1], out ulong created) && created > 0
            && value == pid.ToString(System.Globalization.CultureInfo.InvariantCulture) + ":" + created.ToString(System.Globalization.CultureInfo.InvariantCulture);
    }
    private static (string Commitment, OwnerReference? Exited, PendingExit? Pending) RequireOwnerRetiredCore(WindowsNativeEpochFiles files, List<OwnerReference> references, string? exitedUnderCustody)
    {
        string owner = Path.Combine(files.Root, "owner");
        using var directory = files.RetainDirectory(owner);
        byte[]? previous = null;
        string[]? priorFields = null;
        int count = 0;
        foreach (string path in Directory.EnumerateFileSystemEntries(owner).Order(StringComparer.Ordinal))
        {
            if (Path.GetFileName(path) == "writer.lock") { continue; }
            if (WindowsNativeThermalHistory.IsNamespace(Path.GetFileName(path))) { continue; }
            if (WindowsNativeDriverModeHistory.IsNamespace(Path.GetFileName(path))) { continue; }
            if (++count > 4096) { throw new IOException("Native owner history exceeds its bound."); }
            using var held = files.OpenFile(path);
            byte[] bytes = WindowsNativeEpochFiles.ReadBounded(held, 4096);
            string[] f = NativeCanonical.ParseLines(bytes, "OpenBlade.NativeOwner.v1", 9);
            if (!long.TryParse(f[1], System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out long sequence)
                || sequence != count || f[1] != sequence.ToString(System.Globalization.CultureInfo.InvariantCulture) || Path.GetFileName(path) != $"owner-{sequence:D20}.bin"
                || f[2] != (previous is null ? new string('0', 64) : NativeCanonical.Digest(previous))
                || !ValidArtifact(f[5]) || !ValidProcess(f[6])
                || !NativeCanonical.IsHex(f[3], 32) || !NativeCanonical.IsHex(f[4], 32) || !NativeCanonical.IsHex(f[7], 64))
            { throw new IOException("Native owner history is incomplete or changed."); }
            if (priorFields is null)
            { if (f[0] != "Reserved") { throw new IOException("Native owner history lacks its reservation."); } }
            else
            {
                bool same = priorFields[3..].SequenceEqual(f[3..]);
                bool transition = (priorFields[0], f[0]) is ("Reserved", "Active") or ("Active", "Retiring") or ("Retiring", "Retired");
                bool replacement = priorFields[0] == "Retired" && f[0] == "Reserved" && priorFields[3] == f[3] && priorFields[4] != f[4];
                if (!(same && transition || replacement)) { throw new IOException("Native owner transition differs."); }
            }
            if (f[0] == "Active")
            {
                if (references.Any(r => r.Nonce == f[7])) { throw new IOException("Native owner nonce was reused."); }
                references.Add(new(f[3], f[4], f[5], f[6], f[7], NativeCanonical.Digest(bytes)));
            }
            previous = bytes; priorFields = f;
        }
        // A crashed native process cannot retire its own owner. Its later-acquired
        // exact custody proves exit; managed then inherits the unsettled history.
        OwnerReference? exited = null;
        PendingExit? pending = null;
        if (priorFields is not null && priorFields[0] != "Retired")
        {
            exited = references.LastOrDefault(r => r.Nonce == priorFields[7]);
            if (exited is null || !RequireExited(files, exited, underCustody: exited.Epoch == exitedUnderCustody, out pending))
            { throw new IOException("Native ownership drain remains unresolved; enrollment was preserved."); }
        }
        return (NativeCanonical.Digest(NativeCanonical.Lines("OpenBlade.NativeOwnerHistory.v1",
            count.ToString(System.Globalization.CultureInfo.InvariantCulture), previous is null ? "known-empty" : NativeCanonical.Digest(previous))), exited, pending);

    }
    // Writes nothing. Without a durable marker, only the caller holding this
    // epoch's exact custody receives the exit, as a pending publication.
    private static bool RequireExited(WindowsNativeEpochFiles files, OwnerReference owner, bool underCustody, out PendingExit? pending)
    {
        pending = null;
        string attempts = Path.Combine(files.Root, owner.Epoch, "attempts");
        string key = owner.Process.Replace(':', '-') + ".bin";
        byte[]? launch = ManagedMailboxRecovery.ReadOptional(files, Path.Combine(attempts, "launch-" + key));
        // A drained process retires its own owner; drain plus Active is unknown.
        if (launch is null || ManagedMailboxRecovery.ReadOptional(files, Path.Combine(attempts, "drain-" + key)) is not null) { return false; }
        string[] process = owner.Process.Split(':');
        byte[] expected = WindowsNativeAttemptHistory.Exited(owner.Epoch, launch, process[0], process[1]);
        string marker = Path.Combine(attempts, "exited-" + key);
        byte[]? existing = ManagedMailboxRecovery.ReadOptional(files, marker);
        if (existing is null)
        {
            if (!underCustody) { return false; }
            pending = new(owner.Epoch, marker, expected);
            return true;
        }
        return existing.AsSpan().SequenceEqual(expected);
    }

}
