using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using OpenBlade.Deployment;

namespace OpenBlade.LocalDevDeploymentTool;

internal sealed record LocalDevDeploymentReceipt(
    int SchemaVersion,
    Guid InstallationId,
    string EnrolledAccountSid,
    string DeploymentPolicyId,
    string DevelopmentSigningKeyId,
    string DevelopmentSigningSubjectPublicKeyInfoBase64,
    string BrokerVersion,
    DateTimeOffset EnrolledAt)
{
    internal bool CpuVoltageValidationEnabled { get; init; }
}

internal interface ILocalDevDeploymentLayout
{
    string ReceiptPath { get; }

    string InboxDirectory { get; }

    string CurrentUserSid { get; }
}

internal sealed class WindowsLocalDevDeploymentLayout : ILocalDevDeploymentLayout
{
    private static string CommonData =>
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);

    public string ReceiptPath => Path.Combine(
        CommonData,
        "OpenBlade",
        "LocalDevDeploymentReceipt.json");

    public string InboxDirectory => Path.Combine(
        CommonData,
        "OpenBlade",
        "LocalDevDeploymentInbox");

    public string CurrentUserSid => WindowsIdentity.GetCurrent().User?.Value
        ?? throw new LocalDevDeploymentToolException("The current user SID is unavailable.");
}

internal static partial class LocalDevDeploymentReceiptCodec
{
    internal const int MaximumBytes = 4096;
    internal const string PolicyId = "openblade-local-dev-v1";

    private static readonly string[] PropertyNames =
    [
        "schemaVersion",
        "installationId",
        "enrolledAccountSid",
        "releasePolicyId",
        "developmentSigningKeyId",
        "developmentSigningSubjectPublicKeyInfoBase64",
        "brokerVersion",
        "enrolledAt",
    ];

    internal static LocalDevDeploymentReceipt Read(string path)
    {
        FileInfo info = new(Path.GetFullPath(path));
        if (!info.Exists || info.Length <= 0 || info.Length > MaximumBytes)
        {
            throw InvalidReceipt();
        }

        byte[] bytes = File.ReadAllBytes(info.FullName);
        return ParseCanonical(bytes);
    }

    internal static LocalDevDeploymentReceipt ParseCanonical(ReadOnlySpan<byte> bytes)
    {
        if (bytes.IsEmpty || bytes.Length > MaximumBytes)
        {
            throw InvalidReceipt();
        }

        try
        {
            using JsonDocument document = JsonDocument.Parse(
                bytes.ToArray(),
                new JsonDocumentOptions
                {
                    AllowTrailingCommas = false,
                    CommentHandling = JsonCommentHandling.Disallow,
                    MaxDepth = 2,
                });
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                throw InvalidReceipt();
            }

            Dictionary<string, JsonElement> properties = ReadExactProperties(
                document.RootElement);
            LocalDevDeploymentReceipt receipt = new(
                ReadInt32(properties, "schemaVersion"),
                ReadGuid(properties, "installationId"),
                ReadString(properties, "enrolledAccountSid"),
                ReadString(properties, "releasePolicyId"),
                ReadString(properties, "developmentSigningKeyId"),
                ReadString(properties, "developmentSigningSubjectPublicKeyInfoBase64"),
                ReadString(properties, "brokerVersion"),
                ReadTimestamp(properties, "enrolledAt"))
            { CpuVoltageValidationEnabled = properties.TryGetValue("cpuVoltageValidationEnabled", out var enabled) && enabled.GetBoolean() };
            Validate(receipt);
            if (!bytes.SequenceEqual(SerializeCanonical(receipt)))
            {
                throw InvalidReceipt();
            }

            return receipt;
        }
        catch (LocalDevDeploymentToolException)
        {
            throw;
        }
        catch (Exception exception) when (exception is
            JsonException or InvalidOperationException or FormatException
            or CryptographicException or ArgumentException)
        {
            throw InvalidReceipt(exception);
        }
    }

    internal static byte[] SerializeCanonical(LocalDevDeploymentReceipt receipt)
    {
        Validate(receipt);
        using MemoryStream output = new();
        using (Utf8JsonWriter writer = new(output))
        {
            writer.WriteStartObject();
            writer.WriteNumber("schemaVersion", receipt.SchemaVersion);
            writer.WriteString("installationId", receipt.InstallationId.ToString("D"));
            writer.WriteString("enrolledAccountSid", receipt.EnrolledAccountSid);
            writer.WriteString("releasePolicyId", receipt.DeploymentPolicyId);
            writer.WriteString("developmentSigningKeyId", receipt.DevelopmentSigningKeyId);
            writer.WriteString(
                "developmentSigningSubjectPublicKeyInfoBase64",
                receipt.DevelopmentSigningSubjectPublicKeyInfoBase64);
            writer.WriteString("brokerVersion", receipt.BrokerVersion);
            writer.WriteString(
                "enrolledAt",
                receipt.EnrolledAt.UtcDateTime.ToString(
                    "yyyy-MM-dd'T'HH:mm:ss.fffffff'Z'",
                    System.Globalization.CultureInfo.InvariantCulture));
            if (receipt.CpuVoltageValidationEnabled) { writer.WriteBoolean("cpuVoltageValidationEnabled", true); }
            writer.WriteEndObject();
        }

        return output.ToArray();
    }

    private static Dictionary<string, JsonElement> ReadExactProperties(JsonElement root)
    {
        HashSet<string> allowed = new(PropertyNames, StringComparer.Ordinal);
        allowed.Add("cpuVoltageValidationEnabled");
        HashSet<string> exact = new(StringComparer.Ordinal);
        HashSet<string> insensitive = new(StringComparer.OrdinalIgnoreCase);
        Dictionary<string, JsonElement> values = new(StringComparer.Ordinal);
        foreach (JsonProperty property in root.EnumerateObject())
        {
            if (!allowed.Contains(property.Name)
                || !exact.Add(property.Name)
                || !insensitive.Add(property.Name))
            {
                throw InvalidReceipt();
            }

            values.Add(property.Name, property.Value);
        }

        if (values.Count != PropertyNames.Length + (values.ContainsKey("cpuVoltageValidationEnabled") ? 1 : 0)
            || PropertyNames.Any(name => !values.ContainsKey(name)))
        {
            throw InvalidReceipt();
        }

        return values;
    }

    private static void Validate(LocalDevDeploymentReceipt receipt)
    {
        if (receipt.SchemaVersion != 1
            || receipt.InstallationId == Guid.Empty
            || !SidPattern().IsMatch(receipt.EnrolledAccountSid)
            || !string.Equals(receipt.DeploymentPolicyId, PolicyId, StringComparison.Ordinal)
            || !LowerSha256Pattern().IsMatch(receipt.DevelopmentSigningKeyId)
            || !StableVersionPattern().IsMatch(receipt.BrokerVersion)
            || receipt.EnrolledAt.Offset != TimeSpan.Zero)
        {
            throw InvalidReceipt();
        }

        byte[] subjectPublicKeyInfo = Convert.FromBase64String(
            receipt.DevelopmentSigningSubjectPublicKeyInfoBase64);
        if (subjectPublicKeyInfo.Length is <= 0
            or > OpenBladeSignedReleaseVerifier.MaximumSubjectPublicKeyInfoBytes
            || !string.Equals(
                Convert.ToBase64String(subjectPublicKeyInfo),
                receipt.DevelopmentSigningSubjectPublicKeyInfoBase64,
                StringComparison.Ordinal)
            || !string.Equals(
                OpenBladeSignedReleaseVerifier.ComputeSigningKeyId(subjectPublicKeyInfo),
                receipt.DevelopmentSigningKeyId,
                StringComparison.Ordinal))
        {
            throw InvalidReceipt();
        }

        using ECDsa key = ECDsa.Create();
        key.ImportSubjectPublicKeyInfo(subjectPublicKeyInfo, out int bytesRead);
        ECParameters parameters = key.ExportParameters(includePrivateParameters: false);
        if (bytesRead != subjectPublicKeyInfo.Length
            || key.KeySize != 256
            || !string.Equals(
                parameters.Curve.Oid.Value,
                "1.2.840.10045.3.1.7",
                StringComparison.Ordinal))
        {
            throw InvalidReceipt();
        }
    }

    private static string ReadString(
        IReadOnlyDictionary<string, JsonElement> values,
        string name)
    {
        return values[name].ValueKind == JsonValueKind.String
            ? values[name].GetString()!
            : throw InvalidReceipt();
    }

    private static int ReadInt32(
        IReadOnlyDictionary<string, JsonElement> values,
        string name)
    {
        return values[name].TryGetInt32(out int value) ? value : throw InvalidReceipt();
    }

    private static Guid ReadGuid(
        IReadOnlyDictionary<string, JsonElement> values,
        string name)
    {
        return Guid.TryParseExact(ReadString(values, name), "D", out Guid value)
            ? value
            : throw InvalidReceipt();
    }

    private static DateTimeOffset ReadTimestamp(
        IReadOnlyDictionary<string, JsonElement> values,
        string name)
    {
        return DateTimeOffset.TryParseExact(
            ReadString(values, name),
            "yyyy-MM-dd'T'HH:mm:ss.fffffff'Z'",
            System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.AssumeUniversal
                | System.Globalization.DateTimeStyles.AdjustToUniversal,
            out DateTimeOffset value)
            ? value
            : throw InvalidReceipt();
    }

    private static LocalDevDeploymentToolException InvalidReceipt(
        Exception? innerException = null) => new(
            "The installed local-development enrollment receipt is invalid.",
            innerException);

    [GeneratedRegex("^S-1-(?:[0-9]+-){1,14}[0-9]+$")]
    private static partial Regex SidPattern();

    [GeneratedRegex("^[0-9a-f]{64}$")]
    private static partial Regex LowerSha256Pattern();

    [GeneratedRegex("^(?:0|[1-9][0-9]*)\\.(?:0|[1-9][0-9]*)\\.(?:0|[1-9][0-9]*)$")]
    private static partial Regex StableVersionPattern();
}

internal interface ILocalDevSequenceSource
{
    long Next();
}

internal sealed class UserLocalDevSequenceSource : ILocalDevSequenceSource
{
    public long Next()
    {
        string directory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "OpenBlade");
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, "LocalDevDeployment.sequence");
        using FileStream stream = new(
            path,
            FileMode.OpenOrCreate,
            FileAccess.ReadWrite,
            FileShare.None,
            bufferSize: 64,
            FileOptions.WriteThrough);
        using StreamReader reader = new(stream, Encoding.ASCII, leaveOpen: true);
        string existing = reader.ReadToEnd();
        bool parsedExisting = long.TryParse(
            existing,
            System.Globalization.NumberStyles.None,
            System.Globalization.CultureInfo.InvariantCulture,
            out long previous);
        if ((existing.Length != 0 && !parsedExisting)
            || previous < 0
            || previous == long.MaxValue)
        {
            throw new LocalDevDeploymentToolException(
                "The per-user local-development sequence state is invalid.");
        }

        long now = DateTime.UtcNow.Ticks;
        long next = Math.Max(previous + 1, now);
        stream.Position = 0;
        stream.SetLength(0);
        byte[] bytes = Encoding.ASCII.GetBytes(
            next.ToString(System.Globalization.CultureInfo.InvariantCulture));
        stream.Write(bytes);
        stream.Flush(flushToDisk: true);
        return next;
    }
}
