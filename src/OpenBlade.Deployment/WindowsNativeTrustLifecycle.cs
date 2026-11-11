namespace OpenBlade.Deployment;

[System.Runtime.Versioning.SupportedOSPlatform("windows")]
public static class WindowsNativeTrustLifecycle
{
    // Called before any enrollment replacement/removal. This deliberately does
    // not invent a drain certificate from metadata retirement or SCM STOPPED.
    // An active native selection must first complete its coordinated stop path.
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    public static void RequireReadyForEnrollmentChange()
    {
        if (!Directory.Exists(WindowsNativeEpochFiles.ProductRoot)) { return; }
        using var files = WindowsNativeEpochFiles.OpenProduct();
        using var writer = files.AcquireWriter();
        byte[]? selected = files.ReadCurrent();
        WindowsNativeEpochTransaction.RequireFrontier(files, selected);
        if (selected is not null && NativeEpochRecord.Parse(selected).Phase != NativeEpochPhase.Retired)
        { throw new IOException("Stop and retire the selected native generation before changing enrollment; its cleanup proof was retained."); }
        // Durable owner state is outside generations. Any Reserved/Active/Retiring
        // history is an unresolved drain fence, even if the process disappeared.
        RequireOwnerRetired(files);
        // Preserve every record/mapping/reservation. There is no recursive delete,
        // relocation, adoption or history reset in this revocation path.
    }
    internal static void RequireOwnerRetired(WindowsNativeEpochFiles files)
    {
        string owner = Path.Combine(files.Root, "owner");
        using var directory = files.RetainDirectory(owner);
        byte[]? previous = null;
        string[]? priorFields = null;
        int count = 0;
        foreach (string path in Directory.EnumerateFiles(owner, "owner-*.bin").Order(StringComparer.Ordinal))
        {
            if (++count > 4096) { throw new IOException("Native owner history exceeds its bound."); }
            using var held = files.OpenFile(path);
            byte[] bytes = WindowsNativeEpochFiles.ReadBounded(held, 4096);
            string[] f = NativeCanonical.ParseLines(bytes, "OpenBlade.NativeOwner.v1", 9);
            if (!long.TryParse(f[1], System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out long sequence)
                || sequence != count || Path.GetFileName(path) != $"owner-{sequence:D20}.bin"
                || f[2] != (previous is null ? new string('0', 64) : NativeCanonical.Digest(previous))
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

    }

}
