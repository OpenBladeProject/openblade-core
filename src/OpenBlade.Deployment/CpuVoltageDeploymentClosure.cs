using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Security.Cryptography;
using System.Text.Json;

namespace OpenBlade.Deployment;

public sealed record CpuVoltageDeploymentFile(string Name, long Length, string Sha256);

// The existing Capture closure format, with its voltage-only publication bounds.
// Parsing grants no authority: the caller must pin and verify every actual file.
public static class CpuVoltageDeploymentClosure
{
    public const string DirectoryName = "cpu-voltage";
    public const int MaximumManifestBytes = 512 * 1024, MaximumFiles = 1536, MaximumEntries = 2048;
    public const long MaximumFileBytes = 128L * 1024 * 1024, MaximumTotalBytes = 1024L * 1024 * 1024;
    public static IReadOnlyList<string> RequiredFiles { get; } = new[] { "", "firmware-smoke/", "service/" }
        .SelectMany((prefix, index) => new[] { ".exe", ".dll", ".deps.json", ".runtimeconfig.json" }
            .Select(suffix => prefix + new[] { "OpenBlade.Capture", "OpenBlade.FirmwareSmoke", "OpenBlade.Service" }[index] + suffix)).ToArray();

    public static IReadOnlyList<CpuVoltageDeploymentFile> Parse(byte[] bytes, string source, string digest)
    {
        if (bytes.Length is <= 0 or > MaximumManifestBytes
            || Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant() != digest)
        { throw new InvalidDataException("CPU closure manifest digest or size differs."); }
        using var doc = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 4 });
        var root = doc.RootElement; Fields(root, "version", "source", "files");
        if (root.GetProperty("version").GetInt32() != 1 || root.GetProperty("source").GetString() != source)
        { throw new InvalidDataException("CPU closure source differs."); }
        var files = new List<CpuVoltageDeploymentFile>(); var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        long total = 0;
        foreach (var item in root.GetProperty("files").EnumerateArray())
        {
            Fields(item, "name", "length", "sha256");
            string name = item.GetProperty("name").GetString() ?? throw new InvalidDataException("Missing closure name.");
            string hash = item.GetProperty("sha256").GetString() ?? throw new InvalidDataException("Missing closure hash.");
            long length = item.GetProperty("length").GetInt64();
            if (files.Count >= MaximumFiles || !RelativeName(name) || !names.Add(name)
                || name.Equals("manifest.json", StringComparison.OrdinalIgnoreCase) || hash.Length != 64
                || hash.Any(c => !char.IsAsciiHexDigit(c) || char.IsAsciiLetterLower(c))
                || length is < 0 or > MaximumFileBytes || length > MaximumTotalBytes - total)
            { throw new InvalidDataException("CPU closure file contract invalid."); }
            total += length; files.Add(new(name, length, hash));
        }
        if (RequiredFiles.Any(file => !names.Contains(file))) { throw new InvalidDataException("CPU closure incomplete."); }
        foreach (var file in files)
        {
            string[] parts = file.Name.Split('/');
            for (int i = 1; i < parts.Length; i++)
            { if (names.Contains(string.Join('/', parts.Take(i)))) { throw new InvalidDataException("CPU closure path collision."); } }
        }
        return files;
    }

    public static bool RelativeName(string? value)
    {
        if (value is not { Length: > 0 and <= 200 }) { return false; }
        string[] parts = value.Split('/');
        if (parts.Length > 3) { return false; }
        foreach (string part in parts)
        {
            if (part.Length == 0 || part is "." or ".." || part.EndsWith('.')
                || part.Any(c => !(char.IsAsciiLetterOrDigit(c) || c is '.' or '_' or '-'))) { return false; }
            string stem = part.Split('.')[0].ToUpperInvariant();
            if (stem is "CON" or "PRN" or "AUX" or "NUL"
                || stem.Length == 4 && (stem.StartsWith("COM", StringComparison.Ordinal)
                    || stem.StartsWith("LPT", StringComparison.Ordinal)) && stem[3] is >= '0' and <= '9') { return false; }
        }
        return true;
    }

    public static void VerifyFile(Stream input, CpuVoltageDeploymentFile expected)
    {
        if (input.Length != expected.Length || Convert.ToHexString(SHA256.HashData(input)) != expected.Sha256)
        { throw new InvalidDataException("CPU closure file bytes differ: " + expected.Name); }
        input.Position = 0;
    }

    public static void VerifyAssembly(Stream input, string source, string serviceVersion, bool requireServiceMetadata = true)
    {
        using var pe = new PEReader(input, PEStreamOptions.LeaveOpen);
        MetadataReader reader = pe.GetMetadataReader();
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var handle in reader.GetAssemblyDefinition().GetCustomAttributes())
        {
            var attribute = reader.GetCustomAttribute(handle);
            if (attribute.Constructor.Kind != HandleKind.MemberReference) { continue; }
            var member = reader.GetMemberReference((MemberReferenceHandle)attribute.Constructor);
            if (member.Parent.Kind != HandleKind.TypeReference) { continue; }
            var type = reader.GetTypeReference((TypeReferenceHandle)member.Parent);
            if (reader.GetString(type.Namespace) != "System.Reflection") { continue; }
            string name = reader.GetString(type.Name);
            if (name is not ("AssemblyMetadataAttribute" or "AssemblyInformationalVersionAttribute")) { continue; }
            var blob = reader.GetBlobReader(attribute.Value);
            if (blob.ReadUInt16() != 1) { throw new InvalidDataException("Invalid CPU assembly attribute."); }
            string key = name == "AssemblyMetadataAttribute" ? blob.ReadSerializedString()! : "InformationalVersion";
            string value = blob.ReadSerializedString()!;
            if (!values.TryAdd(key, value)) { throw new InvalidDataException("Duplicate CPU assembly metadata."); }
        }
        if (requireServiceMetadata && (values.GetValueOrDefault("OpenBladeCpuVoltageValidation") != "true"
            || values.GetValueOrDefault("OpenBladeValidationSourceRevision") != source)
            || values.GetValueOrDefault("InformationalVersion") != serviceVersion)
        { throw new InvalidDataException("CPU validation profile/source/version differs."); }
        input.Position = 0;
    }

    private static void Fields(JsonElement value, params string[] names)
    {
        if (value.ValueKind != JsonValueKind.Object || !value.EnumerateObject().Select(p => p.Name).Order(StringComparer.Ordinal)
            .SequenceEqual(names.Order(StringComparer.Ordinal))) { throw new InvalidDataException("CPU closure fields differ."); }
    }
}
