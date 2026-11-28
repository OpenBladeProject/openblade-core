using System.Globalization;

namespace OpenBlade.Deployment;

// v1 is read-only compatibility: its last field is a literal printable ASCII
// path. v2 writes canonical base64 of the exact UTF-16LE code units; never
// normalize or replace Windows path characters (including surrogate units).
internal sealed record NativeStartupOwner(string Epoch, string Request, uint ProcessId, long Created, uint Session, string Path)
{
    private const int MaximumPathUnits = 4096;
    internal byte[] Encode() => NativeCanonical.Lines("OpenBlade.NativeStartupOwner.v2", Epoch, Request,
        ProcessId.ToString(CultureInfo.InvariantCulture), Created.ToString(CultureInfo.InvariantCulture),
        Session.ToString(CultureInfo.InvariantCulture), EncodePath(Path));

    internal static string EncodePath(string path)
    {
        if (path.Length is 0 or > MaximumPathUnits || path.Contains('\0'))
        { throw new InvalidDataException("Native startup owner path is invalid."); }
        byte[] bytes = new byte[path.Length * 2];
        for (int i = 0; i < path.Length; i++)
        { bytes[2 * i] = (byte)path[i]; bytes[2 * i + 1] = (byte)(path[i] >> 8); }
        return Convert.ToBase64String(bytes);
    }

    internal static NativeStartupOwner Parse(byte[] bytes)
    {
        bool legacy = bytes.AsSpan().StartsWith("OpenBlade.NativeStartupOwner.v1\n"u8);
        string[] f = NativeCanonical.ParseLines(bytes, legacy ? "OpenBlade.NativeStartupOwner.v1" : "OpenBlade.NativeStartupOwner.v2", 7);
        string path = f[5];
        if (!legacy)
        {
            byte[] encoded;
            try { encoded = Convert.FromBase64String(path); }
            catch (FormatException e) { throw new InvalidDataException("Native startup owner path encoding is invalid.", e); }
            if (encoded.Length is 0 or > MaximumPathUnits * 2 || encoded.Length % 2 != 0 || Convert.ToBase64String(encoded) != path)
            { throw new InvalidDataException("Native startup owner path encoding is noncanonical."); }
            char[] units = new char[encoded.Length / 2];
            for (int i = 0; i < units.Length; i++) { units[i] = (char)(encoded[2 * i] | encoded[2 * i + 1] << 8); }
            path = new string(units);
        }
        _ = EncodePath(path);
        if (!NativeCanonical.IsHex(f[0], 32) || !NativeCanonical.IsHex(f[1], 32)
            || !uint.TryParse(f[2], NumberStyles.None, CultureInfo.InvariantCulture, out uint pid) || pid == 0 || pid.ToString(CultureInfo.InvariantCulture) != f[2]
            || !long.TryParse(f[3], NumberStyles.None, CultureInfo.InvariantCulture, out long created) || created <= 0 || created.ToString(CultureInfo.InvariantCulture) != f[3]
            || !uint.TryParse(f[4], NumberStyles.None, CultureInfo.InvariantCulture, out uint session) || session.ToString(CultureInfo.InvariantCulture) != f[4])
        { throw new InvalidDataException("Native startup owner identity is invalid."); }
        return new(f[0], f[1], pid, created, session, path);
    }
}
