// Actual pinned 63401e4c full reader, SHA256(LF) fbd33b0a04810ae66be19306a4e2aac57cf612b03ca0d41c0a2ca92a1fbe38b8.
// Only namespace/class name changed; selected tests call private-root owner admission.
using System.Collections.Immutable;
using OpenBlade.Deployment;

namespace OpenBlade.Deployment.Tests;

[System.Runtime.Versioning.SupportedOSPlatform("windows")]
internal static class PinnedPredecessorThermalTrustLifecycle
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
    internal static OwnerLease AcquireRetiredOwner(WindowsNativeEpochFiles files)
    {
        WindowsNativeEpochFiles.DirectoryLease? directory = null;
        FileStream? writer = null;
        try
        {
            directory = files.RetainDirectory(Path.Combine(files.Root, "owner"));
            writer = files.AcquireOwnerWriter();
            var references = new List<OwnerReference>();
            string commitment = RequireOwnerRetiredCore(files, references);
            return new OwnerLease(directory, writer, commitment, references.ToImmutableArray());
        }
        catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException)
        { writer?.Dispose(); directory?.Dispose(); throw new NativeRecoveryException(NativeRecoveryReason.FirmwareOwnerUnresolved, "Native firmware ownership history is active, partial or unknown; no drain was inferred.", error); }
    }
    internal sealed record OwnerReference(string Installation, string Epoch, string Service);
    internal sealed class OwnerLease(IDisposable directory, IDisposable writer, string commitment, ImmutableArray<OwnerReference> references) : IDisposable
    {
        internal string Commitment { get; } = commitment;
        internal ImmutableArray<OwnerReference> References { get; } = references;
        internal bool IsKnownEmpty => References.IsEmpty;
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
    internal static void RequireNoThermalJournal(string name)
    {
        if (name is "thermal-mutation.journal" or "thermal-mutation.journal.pending")
        { throw new IOException("Native thermal submission history is preserved; transport retirement alone cannot authorize managed recovery or cleanup."); }
    }
    private static string RequireOwnerRetiredCore(WindowsNativeEpochFiles files, List<OwnerReference> references)
    {
        string owner = Path.Combine(files.Root, "owner");
        using var directory = files.RetainDirectory(owner);
        byte[]? previous = null;
        string[]? priorFields = null;
        int count = 0;
        foreach (string path in Directory.EnumerateFileSystemEntries(owner).Order(StringComparer.Ordinal))
        {
            if (Path.GetFileName(path) == "writer.lock") { continue; }
            RequireNoThermalJournal(Path.GetFileName(path));
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
            if (f[0] == "Reserved") { references.Add(new(f[3], f[4], f[5])); }
            previous = bytes; priorFields = f;
        }
        if (priorFields is not null && priorFields[0] != "Retired")
        { throw new IOException("Native ownership drain remains unresolved; enrollment was preserved."); }
        return NativeCanonical.Digest(NativeCanonical.Lines("OpenBlade.NativeOwnerHistory.v1",
            count.ToString(System.Globalization.CultureInfo.InvariantCulture), previous is null ? "known-empty" : NativeCanonical.Digest(previous)));

    }

}
