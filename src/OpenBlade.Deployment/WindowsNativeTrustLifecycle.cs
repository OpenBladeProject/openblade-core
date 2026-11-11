namespace OpenBlade.Deployment;

[System.Runtime.Versioning.SupportedOSPlatform("windows")]
public static class WindowsNativeTrustLifecycle
{
    // Keep the same exclusion as managed/native mailbox custody throughout
    // maintenance. A check-and-release permits a Service restart between file
    // replacements; enrollment is itself held by an authenticated managed run.
    public static IDisposable AcquireManagedEnrollmentChangeCustody() =>
        AcquireManagedEnrollmentChangeCustody(WindowsNativeEpochFiles.CreateOrOpenManagedHandoff());

    internal static IDisposable AcquireManagedEnrollmentChangeCustody(WindowsNativeEpochFiles files)
    {
        FileStream? exclusion = null;
        try
        {
            exclusion = files.AcquireServiceCustody(Path.Combine(files.Root, "exclusion.lock"));
            foreach (string name in new[] { "request.bin", "receipt.bin", "mapping.bin" })
            { WindowsNativeEpochFiles.RequireAbsent(Path.Combine(files.Root, name)); }
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
        WindowsNativeEpochFiles.RequireLegacyStoreAbsent();
        if (!WindowsNativeEpochFiles.IsPresentDirectory(WindowsNativeEpochFiles.ProductRoot)) { return; }
        using var files = WindowsNativeEpochFiles.OpenProduct();
        using var writer = files.AcquireWriter();
        byte[]? selected = files.ReadCurrent();
        WindowsNativeEpochTransaction.RequireFrontier(files, selected);
        if (selected is not null && NativeEpochRecord.Parse(selected).Phase != NativeEpochPhase.Retired)
        { throw new IOException("Stop and retire the selected native generation before changing enrollment; its cleanup proof was retained."); }
        // Durable owner state is outside generations. Any Reserved/Active/Retiring
        // history is an unresolved drain fence, even if the process disappeared.
        using var owner = AcquireRetiredOwner(files);
        // Preserve every record/mapping/reservation. There is no recursive delete,
        // relocation, adoption or history reset in this revocation path.
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
            string commitment = RequireOwnerRetiredCore(files);
            return new OwnerLease(directory, writer, commitment);
        }
        catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException)
        { writer?.Dispose(); directory?.Dispose(); throw new NativeRecoveryException(NativeRecoveryReason.FirmwareOwnerUnresolved, "Native firmware ownership history is active, partial or unknown; no drain was inferred.", error); }
    }
    internal sealed class OwnerLease(IDisposable directory, IDisposable writer, string commitment) : IDisposable
    {
        internal string Commitment { get; } = commitment;
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
    private static string RequireOwnerRetiredCore(WindowsNativeEpochFiles files)
    {
        string owner = Path.Combine(files.Root, "owner");
        using var directory = files.RetainDirectory(owner);
        byte[]? previous = null;
        string[]? priorFields = null;
        int count = 0;
        foreach (string path in Directory.EnumerateFileSystemEntries(owner).Order(StringComparer.Ordinal))
        {
            if (Path.GetFileName(path) == "writer.lock") { continue; }
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
            previous = bytes; priorFields = f;
        }
        if (priorFields is not null && priorFields[0] != "Retired")
        { throw new IOException("Native ownership drain remains unresolved; enrollment was preserved."); }
        return NativeCanonical.Digest(NativeCanonical.Lines("OpenBlade.NativeOwnerHistory.v1",
            count.ToString(System.Globalization.CultureInfo.InvariantCulture), previous is null ? "known-empty" : NativeCanonical.Digest(previous)));

    }

}
