namespace OpenBlade.Protocol;

internal sealed class TrustedFixtureHashRegistry
{
    private static readonly string[] ProductionApprovedSha256Hashes =
    [
        "BFD56604D3FFCC52CF471288E0FF1A7290B86CCDA37B3F45C1BF4D8A7ED708CD",
    ];
    private readonly HashSet<string> _sha256Hashes;

    private TrustedFixtureHashRegistry(IEnumerable<string> sha256Hashes)
    {
        _sha256Hashes = new HashSet<string>(sha256Hashes, StringComparer.OrdinalIgnoreCase);
    }

    public static TrustedFixtureHashRegistry Production { get; } =
        FromSha256Hex(ProductionApprovedSha256Hashes);

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
        _sha256Hashes.Contains(Convert.ToHexString(sha256));
}
