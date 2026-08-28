using System.Text.Json;
using System.Text.RegularExpressions;

namespace OpenBlade.Deployment;

public sealed record OpenBladeReleaseManifest
{
    public int SchemaVersion { get; init; }
    public required string SignatureAlgorithm { get; init; }
    public required string SigningKeyCurve { get; init; }
    public required string SigningKeyId { get; init; }
    public Guid InstallationId { get; init; }
    public long Sequence { get; init; }
    public required string Channel { get; init; }
    public required string Product { get; init; }
    public required string Version { get; init; }
    public required string SourceRevision { get; init; }
    public required string Architecture { get; init; }
    public int MinimumWindowsBuild { get; init; }
    public required string InstallerFileName { get; init; }
    public required string BundleId { get; init; }
    public required string ProviderKey { get; init; }
    public required string UpgradeCode { get; init; }
    public long InstallerSize { get; init; }
    public required string InstallerSha256 { get; init; }
}

public static partial class OpenBladeReleaseManifestCodec
{
    public const int CurrentSchemaVersion = 1;
    public const int MaximumManifestBytes = 4096;
    public const long MaximumInstallerBytes = 1024L * 1024L * 1024L;
    public const string SignatureAlgorithm = "ECDSA-P256-SHA256-P1363";
    public const string SigningKeyCurve = "P-256";
    public const string LocalDevChannel = "LocalDev";
    public const string Product = "OpenBlade";
    public const string Architecture = "x64";
    public const int MinimumWindowsBuild = 26100;
    public const string InstallerFileName = "OpenBlade-Setup.exe";
    public const string BundleId = "OSSBlade.OpenBlade";
    public const string ProviderKey = "OSSBlade.OpenBlade";
    public const string UpgradeCode = "FF859CA9-D69F-4995-A2AD-69E540E45724";

    private static readonly string[] RequiredProperties =
    [
        "schemaVersion",
        "signatureAlgorithm",
        "signingKeyCurve",
        "signingKeyId",
        "installationId",
        "sequence",
        "channel",
        "product",
        "version",
        "sourceRevision",
        "architecture",
        "minimumWindowsBuild",
        "installerFileName",
        "bundleId",
        "providerKey",
        "upgradeCode",
        "installerSize",
        "installerSha256",
    ];

    public static byte[] SerializeCanonical(OpenBladeReleaseManifest manifest)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        Validate(manifest);

        using MemoryStream stream = new();
        using (Utf8JsonWriter writer = new(stream, new JsonWriterOptions { Indented = false }))
        {
            writer.WriteStartObject();
            writer.WriteNumber("schemaVersion", manifest.SchemaVersion);
            writer.WriteString("signatureAlgorithm", manifest.SignatureAlgorithm);
            writer.WriteString("signingKeyCurve", manifest.SigningKeyCurve);
            writer.WriteString("signingKeyId", manifest.SigningKeyId);
            writer.WriteString("installationId", manifest.InstallationId.ToString("D"));
            writer.WriteNumber("sequence", manifest.Sequence);
            writer.WriteString("channel", manifest.Channel);
            writer.WriteString("product", manifest.Product);
            writer.WriteString("version", manifest.Version);
            writer.WriteString("sourceRevision", manifest.SourceRevision);
            writer.WriteString("architecture", manifest.Architecture);
            writer.WriteNumber("minimumWindowsBuild", manifest.MinimumWindowsBuild);
            writer.WriteString("installerFileName", manifest.InstallerFileName);
            writer.WriteString("bundleId", manifest.BundleId);
            writer.WriteString("providerKey", manifest.ProviderKey);
            writer.WriteString("upgradeCode", manifest.UpgradeCode);
            writer.WriteNumber("installerSize", manifest.InstallerSize);
            writer.WriteString("installerSha256", manifest.InstallerSha256);
            writer.WriteEndObject();
        }

        return stream.ToArray();
    }

    internal static OpenBladeReleaseManifest ParseCanonical(ReadOnlySpan<byte> manifestBytes)
    {
        if (manifestBytes.IsEmpty || manifestBytes.Length > MaximumManifestBytes)
        {
            throw new OpenBladeReleaseVerificationException(
                OpenBladeReleaseVerificationFailure.ManifestSizeInvalid,
                $"The release manifest must contain between 1 and {MaximumManifestBytes} bytes.");
        }

        if (manifestBytes.Length >= 3
            && manifestBytes[0] == 0xEF
            && manifestBytes[1] == 0xBB
            && manifestBytes[2] == 0xBF)
        {
            throw InvalidManifest("The release manifest must not contain a UTF-8 byte-order mark.");
        }

        try
        {
            using JsonDocument document = JsonDocument.Parse(
                manifestBytes.ToArray(),
                new JsonDocumentOptions
                {
                    AllowTrailingCommas = false,
                    CommentHandling = JsonCommentHandling.Disallow,
                    MaxDepth = 4,
                });

            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                throw InvalidManifest("The release manifest root must be a JSON object.");
            }

            Dictionary<string, JsonElement> properties = ReadProperties(document.RootElement);
            OpenBladeReleaseManifest manifest = new()
            {
                SchemaVersion = ReadInt32(properties, "schemaVersion"),
                SignatureAlgorithm = ReadString(properties, "signatureAlgorithm"),
                SigningKeyCurve = ReadString(properties, "signingKeyCurve"),
                SigningKeyId = ReadString(properties, "signingKeyId"),
                InstallationId = ReadGuid(properties, "installationId"),
                Sequence = ReadInt64(properties, "sequence"),
                Channel = ReadString(properties, "channel"),
                Product = ReadString(properties, "product"),
                Version = ReadString(properties, "version"),
                SourceRevision = ReadString(properties, "sourceRevision"),
                Architecture = ReadString(properties, "architecture"),
                MinimumWindowsBuild = ReadInt32(properties, "minimumWindowsBuild"),
                InstallerFileName = ReadString(properties, "installerFileName"),
                BundleId = ReadString(properties, "bundleId"),
                ProviderKey = ReadString(properties, "providerKey"),
                UpgradeCode = ReadString(properties, "upgradeCode"),
                InstallerSize = ReadInt64(properties, "installerSize"),
                InstallerSha256 = ReadString(properties, "installerSha256"),
            };

            Validate(manifest);
            byte[] canonicalBytes = SerializeCanonical(manifest);
            if (!manifestBytes.SequenceEqual(canonicalBytes))
            {
                throw new OpenBladeReleaseVerificationException(
                    OpenBladeReleaseVerificationFailure.ManifestNotCanonical,
                    "The release manifest bytes do not match the canonical encoding.");
            }

            return manifest;
        }
        catch (OpenBladeReleaseVerificationException)
        {
            throw;
        }
        catch (JsonException exception)
        {
            throw InvalidManifest("The release manifest is not valid strict UTF-8 JSON.", exception);
        }
        catch (InvalidOperationException exception)
        {
            throw InvalidManifest("A release manifest property has the wrong JSON type.", exception);
        }
    }

    private static Dictionary<string, JsonElement> ReadProperties(JsonElement root)
    {
        HashSet<string> exactNames = new(StringComparer.Ordinal);
        HashSet<string> caseInsensitiveNames = new(StringComparer.OrdinalIgnoreCase);
        HashSet<string> allowedNames = new(RequiredProperties, StringComparer.Ordinal);
        Dictionary<string, JsonElement> properties = new(StringComparer.Ordinal);

        foreach (JsonProperty property in root.EnumerateObject())
        {
            if (!exactNames.Add(property.Name))
            {
                throw InvalidManifest($"The release manifest property '{property.Name}' is duplicated.");
            }

            if (!caseInsensitiveNames.Add(property.Name))
            {
                throw InvalidManifest("Release manifest property names must not collide by case.");
            }

            if (!allowedNames.Contains(property.Name))
            {
                throw InvalidManifest($"The release manifest property '{property.Name}' is not recognized.");
            }

            properties.Add(property.Name, property.Value);
        }

        foreach (string requiredProperty in RequiredProperties)
        {
            if (!properties.ContainsKey(requiredProperty))
            {
                throw InvalidManifest($"The release manifest property '{requiredProperty}' is required.");
            }
        }

        return properties;
    }

    private static string ReadString(IReadOnlyDictionary<string, JsonElement> properties, string name)
    {
        JsonElement value = properties[name];
        if (value.ValueKind != JsonValueKind.String)
        {
            throw InvalidManifest($"The release manifest property '{name}' must be a string.");
        }

        return value.GetString()!;
    }

    private static int ReadInt32(IReadOnlyDictionary<string, JsonElement> properties, string name)
    {
        if (!properties[name].TryGetInt32(out int value))
        {
            throw InvalidManifest($"The release manifest property '{name}' must be a 32-bit integer.");
        }

        return value;
    }

    private static long ReadInt64(IReadOnlyDictionary<string, JsonElement> properties, string name)
    {
        if (!properties[name].TryGetInt64(out long value))
        {
            throw InvalidManifest($"The release manifest property '{name}' must be a 64-bit integer.");
        }

        return value;
    }

    private static Guid ReadGuid(IReadOnlyDictionary<string, JsonElement> properties, string name)
    {
        string value = ReadString(properties, name);
        return Guid.TryParseExact(value, "D", out Guid result)
            ? result
            : throw InvalidManifest($"The release manifest property '{name}' must be a canonical GUID.");
    }

    private static IReadOnlyList<string> ReadStringArray(
        IReadOnlyDictionary<string, JsonElement> properties,
        string name)
    {
        JsonElement value = properties[name];
        if (value.ValueKind != JsonValueKind.Array)
        {
            throw InvalidManifest($"The release manifest property '{name}' must be an array.");
        }

        List<string> items = [];
        foreach (JsonElement item in value.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.String)
            {
                throw InvalidManifest($"Every '{name}' item must be a string.");
            }

            items.Add(item.GetString()!);
        }

        return items;
    }

    private static void Validate(OpenBladeReleaseManifest manifest)
    {
        if (manifest.SchemaVersion != CurrentSchemaVersion)
        {
            throw InvalidManifest($"schemaVersion must be {CurrentSchemaVersion}.");
        }

        RequireFixed(manifest.SignatureAlgorithm, SignatureAlgorithm, nameof(manifest.SignatureAlgorithm));
        RequireFixed(manifest.SigningKeyCurve, SigningKeyCurve, nameof(manifest.SigningKeyCurve));
        RequireFixed(manifest.Channel, LocalDevChannel, nameof(manifest.Channel));
        RequireFixed(manifest.Product, Product, nameof(manifest.Product));
        RequireFixed(manifest.Architecture, Architecture, nameof(manifest.Architecture));
        RequireFixed(manifest.InstallerFileName, InstallerFileName, nameof(manifest.InstallerFileName));
        RequireFixed(manifest.BundleId, BundleId, nameof(manifest.BundleId));
        RequireFixed(manifest.ProviderKey, ProviderKey, nameof(manifest.ProviderKey));
        RequireFixed(manifest.UpgradeCode, UpgradeCode, nameof(manifest.UpgradeCode));

        if (manifest.MinimumWindowsBuild != MinimumWindowsBuild)
        {
            throw InvalidManifest($"minimumWindowsBuild must be {MinimumWindowsBuild}.");
        }

        if (!LowerSha256Pattern().IsMatch(manifest.SigningKeyId))
        {
            throw InvalidManifest("signingKeyId must be the lowercase SHA-256 of the signing SPKI DER bytes.");
        }

        if (manifest.InstallationId == Guid.Empty)
        {
            throw InvalidManifest("installationId must be a non-empty canonical GUID.");
        }

        if (manifest.Sequence <= 0)
        {
            throw InvalidManifest("sequence must be a positive 64-bit integer.");
        }

        if (!IsCanonicalLocalDevVersion(manifest.Version))
        {
            throw InvalidManifest(
                "version must be a canonical reserved 65535.x.y.z local-development version.");
        }

        if (!LowerHexRevisionPattern().IsMatch(manifest.SourceRevision))
        {
            throw InvalidManifest("sourceRevision must contain exactly 40 lowercase hexadecimal characters.");
        }

        if (manifest.InstallerSize <= 0 || manifest.InstallerSize > MaximumInstallerBytes)
        {
            throw InvalidManifest($"installerSize must be between 1 and {MaximumInstallerBytes} bytes.");
        }

        if (!LowerSha256Pattern().IsMatch(manifest.InstallerSha256))
        {
            throw InvalidManifest("installerSha256 must contain exactly 64 lowercase hexadecimal characters.");
        }

    }

    private static void RequireFixed(string? actual, string expected, string propertyName)
    {
        if (!string.Equals(actual, expected, StringComparison.Ordinal))
        {
            throw InvalidManifest($"{propertyName} must be '{expected}'.");
        }
    }

    private static OpenBladeReleaseVerificationException InvalidManifest(
        string message,
        Exception? innerException = null)
    {
        return new OpenBladeReleaseVerificationException(
            OpenBladeReleaseVerificationFailure.ManifestInvalid,
            message,
            innerException);
    }

    public static bool IsCanonicalLocalDevVersion(string? value)
    {
        if (value is null
            || !FourPartVersionPattern().IsMatch(value)
            || !Version.TryParse(value, out Version? parsed))
        {
            return false;
        }

        return parsed.Major == 65535
            && parsed.Minor is >= 0 and < 65535
            && parsed.Build is >= 0 and < 65535
            && parsed.Revision is >= 0 and < 65535
            && string.Equals(parsed.ToString(4), value, StringComparison.Ordinal);
    }

    [GeneratedRegex("^(?:0|[1-9][0-9]*)\\.(?:0|[1-9][0-9]*)\\.(?:0|[1-9][0-9]*)\\.(?:0|[1-9][0-9]*)$")]
    private static partial Regex FourPartVersionPattern();

    [GeneratedRegex("^[0-9a-f]{40}$")]
    private static partial Regex LowerHexRevisionPattern();

    [GeneratedRegex("^[0-9a-f]{64}$")]
    private static partial Regex LowerSha256Pattern();
}
