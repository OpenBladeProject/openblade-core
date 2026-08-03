using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace OpenBlade.Protocol;

internal static class Rz090528LightingOracleResources
{
    private static readonly Target ExactTarget =
        new("RZ09-0528", "1532", "02C6", "2.02");

    internal static T ReadDocument<T>(string resource, string approvedHash)
    {
        using Stream stream = typeof(Rz090528LightingOracleResources).Assembly
            .GetManifestResourceStream(resource)
            ?? throw new InvalidOperationException("An RZ09-0528 oracle is missing.");
        using var memory = new MemoryStream();
        stream.CopyTo(memory);
        byte[] bytes = memory.ToArray();
        if (Convert.ToHexString(SHA256.HashData(bytes)) != approvedHash)
        {
            throw new InvalidOperationException(
                "An RZ09-0528 oracle hash is not approved.");
        }

        return JsonSerializer.Deserialize<T>(
            bytes,
            new JsonSerializerOptions(JsonSerializerDefaults.Web)
            {
                PropertyNameCaseInsensitive = true,
                UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
            }) ?? throw new InvalidOperationException(
                "An RZ09-0528 oracle is malformed.");
    }

    internal static byte[] Inflate(string data, int expectedBytes)
    {
        using var input = new MemoryStream(Convert.FromBase64String(data), false);
        using var deflate = new DeflateStream(input, CompressionMode.Decompress);
        byte[] raw = new byte[expectedBytes];
        deflate.ReadExactly(raw);
        if (deflate.ReadByte() != -1)
        {
            throw new InvalidOperationException(
                "An oracle template has trailing data.");
        }
        return raw;
    }

    internal static bool IsExactTarget(Target target) => target == ExactTarget;

    internal sealed record Target(
        string ModelNumber,
        string VendorIdHex,
        string ProductIdHex,
        string Bios);
}
