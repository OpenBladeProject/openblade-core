using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace OpenBlade.DeploymentBroker;

public sealed record BrokerEnrollment(
    int SchemaVersion,
    Guid InstallationId,
    string EnrolledAccountSid,
    string DeploymentPolicyId,
    string BrokerVersion,
    string DevelopmentSigningKeyId,
    string DevelopmentSigningSubjectPublicKeyInfoBase64,
    DateTimeOffset EnrolledAt);

public sealed class BrokerPaths
{
    public const string EnrollmentFileName = "enrollment.json";

    internal BrokerPaths(string rootDirectory, bool applyProductionAcl, string? inboxDirectory = null)
    {
        RootDirectory = Path.GetFullPath(rootDirectory);
        ApplyProductionAcl = applyProductionAcl;
        EnrollmentPath = Path.Combine(RootDirectory, EnrollmentFileName);
        ReplayDirectory = Path.Combine(RootDirectory, "replay");
        StagingDirectory = Path.Combine(RootDirectory, "staging");
        JournalDirectory = Path.Combine(RootDirectory, "journal");
        ReportDirectory = Path.Combine(RootDirectory, "reports");
        InFlightPath = Path.Combine(RootDirectory, "unresolved-deployment.json");
        AcceptedSequencePath = Path.Combine(RootDirectory, "accepted-local-dev-sequence.txt");
        InboxDirectory = Path.GetFullPath(inboxDirectory ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "OpenBlade",
            "LocalDevDeploymentInbox"));
    }

    public string RootDirectory { get; }
    public string EnrollmentPath { get; }
    public string ReplayDirectory { get; }
    public string StagingDirectory { get; }
    public string JournalDirectory { get; }
    public string ReportDirectory { get; }
    public string InFlightPath { get; }
    public string AcceptedSequencePath { get; }
    public string InboxDirectory { get; }
    internal bool ApplyProductionAcl { get; }

    public static BrokerPaths CreateDefault() => new(
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "OpenBlade",
            "LocalDevDeploymentBroker"),
        applyProductionAcl: true);

    internal static BrokerPaths CreateForTests(string rootDirectory) => new(
        rootDirectory,
        applyProductionAcl: false,
        inboxDirectory: Path.Combine(rootDirectory, "inbox"));

    public string CreateRequestStagingDirectory(Guid requestId)
    {
        if (requestId == Guid.Empty)
        {
            throw new ArgumentException("A non-empty request ID is required.", nameof(requestId));
        }

        EnsureDirectory(StagingDirectory);
        string requestDirectory = Path.Combine(StagingDirectory, requestId.ToString("N"));
        Directory.CreateDirectory(requestDirectory);
        if (ApplyProductionAcl)
        {
            ProtectedBrokerStorage.ApplyDirectorySecurity(requestDirectory);
        }
        ProtectedBrokerStorage.RejectReparsePoint(requestDirectory);
        return requestDirectory;
    }

    public void DeleteRequestStagingDirectory(Guid requestId)
    {
        if (requestId == Guid.Empty)
        {
            throw new ArgumentException("A non-empty request ID is required.", nameof(requestId));
        }

        string expected = Path.Combine(StagingDirectory, requestId.ToString("N"));
        string fullPath = Path.GetFullPath(expected);
        if (!string.Equals(fullPath, expected, StringComparison.OrdinalIgnoreCase)
            || !Directory.Exists(fullPath))
        {
            return;
        }

        ProtectedBrokerStorage.RejectReparsePoint(StagingDirectory);
        ProtectedBrokerStorage.RejectReparsePoint(fullPath);
        VerifyDirectory(StagingDirectory);
        VerifyDirectory(fullPath);
        Stack<string> pending = new();
        pending.Push(fullPath);
        while (pending.Count > 0)
        {
            string directory = pending.Pop();
            foreach (string entry in Directory.EnumerateFileSystemEntries(directory))
            {
                string candidate = Path.GetFullPath(entry);
                string relative = Path.GetRelativePath(fullPath, candidate);
                if (relative == ".."
                    || relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal)
                    || Path.IsPathRooted(relative))
                {
                    throw new UnauthorizedAccessException(
                        "A broker staging entry escaped its exact request directory.");
                }

                ProtectedBrokerStorage.RejectReparsePoint(candidate);
                if (Directory.Exists(candidate))
                {
                    pending.Push(candidate);
                }
            }
        }

        Directory.Delete(fullPath, recursive: true);
    }

    internal void EnsureDirectory(string path)
    {
        Directory.CreateDirectory(path);
        if (ApplyProductionAcl)
        {
            ProtectedBrokerStorage.ApplyDirectorySecurity(path);
        }
        ProtectedBrokerStorage.RejectReparsePoint(path);
    }

    internal void ProtectFile(string path)
    {
        if (ApplyProductionAcl)
        {
            ProtectedBrokerStorage.ApplyFileSecurity(path);
        }
    }

    internal void VerifyDirectory(string path)
    {
        if (ApplyProductionAcl)
        {
            ProtectedBrokerStorage.VerifyProtectedDirectorySecurity(path);
        }
    }

    internal void VerifyFile(string path)
    {
        if (ApplyProductionAcl)
        {
            ProtectedBrokerStorage.VerifyProtectedFileSecurity(path);
        }
    }
}

public static partial class BrokerEnrollmentCodec
{
    public const int CurrentSchemaVersion = 1;
    public const int MaximumBytes = 2048;

    private static readonly string[] PropertyNames =
    [
        "schemaVersion",
        "installationId",
        "enrolledAccountSid",
        "deploymentPolicyId",
        "brokerVersion",
        "developmentSigningKeyId",
        "developmentSigningSubjectPublicKeyInfoBase64",
        "enrolledAt",
    ];

    public static byte[] SerializeCanonical(BrokerEnrollment enrollment)
    {
        Validate(enrollment);
        using MemoryStream stream = new();
        using (Utf8JsonWriter writer = new(stream))
        {
            writer.WriteStartObject();
            writer.WriteNumber("schemaVersion", enrollment.SchemaVersion);
            writer.WriteString("installationId", enrollment.InstallationId.ToString("D"));
            writer.WriteString("enrolledAccountSid", enrollment.EnrolledAccountSid);
            writer.WriteString("deploymentPolicyId", enrollment.DeploymentPolicyId);
            writer.WriteString("brokerVersion", enrollment.BrokerVersion);
            writer.WriteString("developmentSigningKeyId", enrollment.DevelopmentSigningKeyId);
            writer.WriteString(
                "developmentSigningSubjectPublicKeyInfoBase64",
                enrollment.DevelopmentSigningSubjectPublicKeyInfoBase64);
            writer.WriteString(
                "enrolledAt",
                enrollment.EnrolledAt.UtcDateTime.ToString(
                    "yyyy-MM-dd'T'HH:mm:ss.fffffff'Z'",
                    System.Globalization.CultureInfo.InvariantCulture));
            writer.WriteEndObject();
        }

        byte[] bytes = stream.ToArray();
        if (bytes.Length > MaximumBytes)
        {
            throw new InvalidDataException("The enrollment record is too large.");
        }

        return bytes;
    }

    public static BrokerEnrollment ParseCanonical(ReadOnlySpan<byte> bytes)
    {
        if (bytes.IsEmpty || bytes.Length > MaximumBytes)
        {
            throw new InvalidDataException("The enrollment record has an invalid size.");
        }

        if (bytes.Length >= 3 && bytes[..3].SequenceEqual(new byte[] { 0xEF, 0xBB, 0xBF }))
        {
            throw new InvalidDataException("The enrollment record must not contain a BOM.");
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
                throw new InvalidDataException("The enrollment record must be an object.");
            }

            Dictionary<string, JsonElement> properties = ReadProperties(document.RootElement);
            BrokerEnrollment enrollment = new(
                ReadInt32(properties, "schemaVersion"),
                ReadGuid(properties, "installationId"),
                ReadString(properties, "enrolledAccountSid"),
                ReadString(properties, "deploymentPolicyId"),
                ReadString(properties, "brokerVersion"),
                ReadString(properties, "developmentSigningKeyId"),
                ReadString(properties, "developmentSigningSubjectPublicKeyInfoBase64"),
                ReadTimestamp(properties, "enrolledAt"));
            Validate(enrollment);
            if (!bytes.SequenceEqual(SerializeCanonical(enrollment)))
            {
                throw new InvalidDataException("The enrollment record is not canonical JSON.");
            }

            return enrollment;
        }
        catch (InvalidDataException)
        {
            throw;
        }
        catch (Exception exception) when (exception is JsonException or FormatException or InvalidOperationException)
        {
            throw new InvalidDataException("The enrollment record is malformed.", exception);
        }
    }

    public static SecurityIdentifier ParseAccountSid(string value)
    {
        try
        {
            SecurityIdentifier sid = new(value);
            if (!sid.IsAccountSid()
                || sid.AccountDomainSid is null
                || !string.Equals(sid.Value, value, StringComparison.Ordinal))
            {
                throw new InvalidDataException("The enrolled SID must be a canonical Windows account SID.");
            }

            return sid;
        }
        catch (ArgumentException exception)
        {
            throw new InvalidDataException("The enrolled SID is invalid.", exception);
        }
    }

    private static Dictionary<string, JsonElement> ReadProperties(JsonElement root)
    {
        HashSet<string> allowed = new(PropertyNames, StringComparer.Ordinal);
        HashSet<string> seen = new(StringComparer.Ordinal);
        HashSet<string> seenIgnoreCase = new(StringComparer.OrdinalIgnoreCase);
        Dictionary<string, JsonElement> values = new(StringComparer.Ordinal);
        foreach (JsonProperty property in root.EnumerateObject())
        {
            if (!allowed.Contains(property.Name)
                || !seen.Add(property.Name)
                || !seenIgnoreCase.Add(property.Name))
            {
                throw new InvalidDataException("The enrollment record has an unknown or duplicate property.");
            }

            values.Add(property.Name, property.Value);
        }

        if (values.Count != PropertyNames.Length
            || PropertyNames.Any(name => !values.ContainsKey(name)))
        {
            throw new InvalidDataException("The enrollment record is missing a required property.");
        }

        return values;
    }

    private static string ReadString(IReadOnlyDictionary<string, JsonElement> values, string name) =>
        values[name].ValueKind == JsonValueKind.String
            ? values[name].GetString()!
            : throw new InvalidDataException($"Enrollment property '{name}' must be a string.");

    private static int ReadInt32(IReadOnlyDictionary<string, JsonElement> values, string name) =>
        values[name].TryGetInt32(out int result)
            ? result
            : throw new InvalidDataException($"Enrollment property '{name}' must be an integer.");

    private static Guid ReadGuid(IReadOnlyDictionary<string, JsonElement> values, string name)
    {
        string text = ReadString(values, name);
        return Guid.TryParseExact(text, "D", out Guid result)
            ? result
            : throw new InvalidDataException($"Enrollment property '{name}' must be a canonical GUID.");
    }

    private static DateTimeOffset ReadTimestamp(
        IReadOnlyDictionary<string, JsonElement> values,
        string name)
    {
        string text = ReadString(values, name);
        const string format = "yyyy-MM-dd'T'HH:mm:ss.fffffff'Z'";
        return DateTimeOffset.TryParseExact(
            text,
            format,
            System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.AssumeUniversal
                | System.Globalization.DateTimeStyles.AdjustToUniversal,
            out DateTimeOffset result)
            ? result
            : throw new InvalidDataException($"Enrollment property '{name}' must be a canonical UTC timestamp.");
    }

    private static void Validate(BrokerEnrollment enrollment)
    {
        ArgumentNullException.ThrowIfNull(enrollment);
        if (enrollment.SchemaVersion != CurrentSchemaVersion)
        {
            throw new InvalidDataException($"Enrollment schema must be {CurrentSchemaVersion}.");
        }

        if (enrollment.InstallationId == Guid.Empty)
        {
            throw new InvalidDataException("The installation ID must not be empty.");
        }

        _ = ParseAccountSid(enrollment.EnrolledAccountSid);
        if (!string.Equals(
                enrollment.DeploymentPolicyId,
                "openblade-local-dev-v1",
                StringComparison.Ordinal))
        {
            throw new InvalidDataException("The local-development deployment policy is invalid.");
        }

        if (!StableVersionPattern().IsMatch(enrollment.BrokerVersion))
        {
            throw new InvalidDataException("The broker version must be a canonical stable version.");
        }

        if (!LowerSha256Pattern().IsMatch(enrollment.DevelopmentSigningKeyId))
        {
            throw new InvalidDataException("The development signing key ID is invalid.");
        }

        byte[] signingKey;
        try
        {
            signingKey = Convert.FromBase64String(
                enrollment.DevelopmentSigningSubjectPublicKeyInfoBase64);
        }
        catch (FormatException exception)
        {
            throw new InvalidDataException("The development signing public key is invalid.", exception);
        }

        if (signingKey.Length is < 1 or > OpenBlade.Deployment.OpenBladeSignedReleaseVerifier.MaximumSubjectPublicKeyInfoBytes
            || !string.Equals(
                OpenBlade.Deployment.OpenBladeSignedReleaseVerifier.ComputeSigningKeyId(signingKey),
                enrollment.DevelopmentSigningKeyId,
                StringComparison.Ordinal))
        {
            throw new InvalidDataException("The development signing public key does not match its key ID.");
        }

        if (enrollment.EnrolledAt.Offset != TimeSpan.Zero)
        {
            throw new InvalidDataException("The enrollment timestamp must be UTC.");
        }
    }

    [GeneratedRegex("^(?:0|[1-9][0-9]*)\\.(?:0|[1-9][0-9]*)\\.(?:0|[1-9][0-9]*)$")]
    private static partial Regex StableVersionPattern();

    [GeneratedRegex("^[0-9a-f]{64}$")]
    private static partial Regex LowerSha256Pattern();
}

public sealed class BrokerEnrollmentStore(BrokerPaths paths)
{
    public async Task CreateOnceAsync(
        BrokerEnrollment enrollment,
        CancellationToken cancellationToken = default)
    {
        byte[] bytes = BrokerEnrollmentCodec.SerializeCanonical(enrollment);
        paths.EnsureDirectory(paths.RootDirectory);
        await using FileStream stream = new(
            paths.EnrollmentPath,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            bufferSize: 4096,
            FileOptions.Asynchronous | FileOptions.WriteThrough);
        await stream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
        stream.Flush(flushToDisk: true);
        paths.ProtectFile(paths.EnrollmentPath);
    }

    public async Task<BrokerEnrollment> LoadAsync(CancellationToken cancellationToken = default)
    {
        ProtectedBrokerStorage.RejectReparsePoint(paths.RootDirectory);
        ProtectedBrokerStorage.RejectReparsePoint(paths.EnrollmentPath);
        paths.VerifyDirectory(paths.RootDirectory);
        paths.VerifyFile(paths.EnrollmentPath);
        byte[] bytes = await File.ReadAllBytesAsync(paths.EnrollmentPath, cancellationToken)
            .ConfigureAwait(false);
        return BrokerEnrollmentCodec.ParseCanonical(bytes);
    }
}

internal static class ProtectedBrokerStorage
{
    private static readonly SecurityIdentifier LocalSystem = new(
        WellKnownSidType.LocalSystemSid,
        null);
    private static readonly SecurityIdentifier Administrators = new(
        WellKnownSidType.BuiltinAdministratorsSid,
        null);

    public static void EnsureDirectory(string path)
    {
        Directory.CreateDirectory(path);
        ApplyDirectorySecurity(path);
        RejectReparsePoint(path);
    }

    public static void ApplyDirectorySecurity(string path)
    {
        new DirectoryInfo(path).SetAccessControl(CreateProtectedDirectorySecurity());
    }

    internal static DirectorySecurity CreateProtectedDirectorySecurity()
    {
        DirectorySecurity security = new();
        security.SetOwner(Administrators);
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        const InheritanceFlags inheritance =
            InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit;
        security.AddAccessRule(new FileSystemAccessRule(
            LocalSystem,
            FileSystemRights.FullControl,
            inheritance,
            PropagationFlags.None,
            AccessControlType.Allow));
        security.AddAccessRule(new FileSystemAccessRule(
            Administrators,
            FileSystemRights.FullControl,
            inheritance,
            PropagationFlags.None,
            AccessControlType.Allow));
        return security;
    }

    public static void ApplyFileSecurity(string path)
    {
        new FileInfo(path).SetAccessControl(CreateProtectedFileSecurity());
    }

    internal static FileSecurity CreateProtectedFileSecurity()
    {
        FileSecurity security = new();
        security.SetOwner(Administrators);
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        security.AddAccessRule(new FileSystemAccessRule(
            LocalSystem,
            FileSystemRights.FullControl,
            AccessControlType.Allow));
        security.AddAccessRule(new FileSystemAccessRule(
            Administrators,
            FileSystemRights.FullControl,
            AccessControlType.Allow));
        return security;
    }

    internal static void VerifyProtectedDirectorySecurity(string path) =>
        VerifyProtectedSecurity(
            new DirectoryInfo(path).GetAccessControl(),
            directory: true);

    internal static void VerifyProtectedFileSecurity(string path) =>
        VerifyProtectedSecurity(
            new FileInfo(path).GetAccessControl(),
            directory: false);

    internal static void VerifyProtectedSecurity(
        FileSystemSecurity security,
        bool directory)
    {
        if (!security.AreAccessRulesProtected
            || security.GetOwner(typeof(SecurityIdentifier)) != Administrators)
        {
            throw new UnauthorizedAccessException(
                "Broker state must be administrator-owned with protected access rules.");
        }

        FileSystemAccessRule[] rules = security.GetAccessRules(
                includeExplicit: true,
                includeInherited: false,
                typeof(SecurityIdentifier))
            .Cast<FileSystemAccessRule>()
            .ToArray();
        InheritanceFlags expectedInheritance = directory
            ? InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit
            : InheritanceFlags.None;
        if (rules.Length != 2
            || !HasExactRule(rules, LocalSystem, expectedInheritance)
            || !HasExactRule(rules, Administrators, expectedInheritance))
        {
            throw new UnauthorizedAccessException(
                "Broker state access must be limited to SYSTEM and Administrators full control.");
        }
    }

    private static bool HasExactRule(
        IEnumerable<FileSystemAccessRule> rules,
        SecurityIdentifier identity,
        InheritanceFlags inheritance) => rules.Any(rule =>
            rule.IdentityReference == identity
            && rule.AccessControlType == AccessControlType.Allow
            && rule.FileSystemRights == FileSystemRights.FullControl
            && rule.InheritanceFlags == inheritance
            && rule.PropagationFlags == PropagationFlags.None);

    public static void RejectReparsePoint(string path)
    {
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
        {
            throw new UnauthorizedAccessException("Broker storage must not contain reparse points.");
        }
    }
}
