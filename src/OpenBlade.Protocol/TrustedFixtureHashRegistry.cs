namespace OpenBlade.Protocol;

internal sealed class TrustedFixtureHashRegistry
{
    private static readonly string[] ProductionApprovedSha256Hashes =
    [
        "12D41086349900514F2D8CB87073A90FD751CB1DF964A1D8D0B8C320B7F0FABC",
    ];
    private static readonly string[] HyperBoostProductionApprovedSha256Hashes =
    [
        "3DBE55C2E2972EE2C97FE5DC5D7A53D7F175CF37EB954E95C16E67589401010D",
    ];
    private readonly HashSet<string> trustedSha256Hashes;

    private TrustedFixtureHashRegistry(IEnumerable<string> sha256Hashes)
    {
        trustedSha256Hashes = new HashSet<string>(sha256Hashes, StringComparer.OrdinalIgnoreCase);
    }

    public static TrustedFixtureHashRegistry Production { get; } =
        FromSha256Hex(ProductionApprovedSha256Hashes);

    public static TrustedFixtureHashRegistry HyperBoostProduction { get; } =
        FromSha256Hex(HyperBoostProductionApprovedSha256Hashes);

    internal static TrustedFixtureHashRegistry Rz090528HyperBoostProduction { get; } =
        FromSha256Hex(["96B8E86248A1CEEBE7D76A3C49CFCE536CBE084F5D8CE3AC85CE1D9075F91C81"]);

    public static TrustedFixtureHashRegistry FromSha256Hex(IEnumerable<string> sha256Hashes)
    {
        ArgumentNullException.ThrowIfNull(sha256Hashes);
        string[] hashes = sha256Hashes.ToArray();
        if (hashes.Any(hash => hash.Length != 64 || !hash.All(Uri.IsHexDigit)))
        {
            throw new ArgumentException(
                "Trusted fixture hashes must be 64-character SHA-256 hexadecimal strings.",
                nameof(sha256Hashes));
        }

        return new TrustedFixtureHashRegistry(hashes);
    }

    public bool Contains(ReadOnlySpan<byte> sha256) =>
        trustedSha256Hashes.Contains(Convert.ToHexString(sha256));
}
