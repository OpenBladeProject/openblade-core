using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json;

namespace OpenBlade.Setup;

internal static class LocalDevEnrollmentUser
{
    private const int MaximumEnrollmentBytes = 2048;
    private const string PolicyId = "openblade-local-dev-v1";
    private static readonly string[] RequiredProperties =
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

    internal static string ReadEnrolledAccountSid() => ReadEnrolledAccountSid(
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "OpenBlade",
            "LocalDevDeploymentBroker",
            "enrollment.json"),
        verifyAcl: true);

    internal static string ReadEnrolledAccountSid(string enrollmentPath, bool verifyAcl)
    {
        string fullPath = Path.GetFullPath(enrollmentPath);
        string? stateDirectory = Path.GetDirectoryName(fullPath);
        if (stateDirectory is null
            || !File.Exists(fullPath)
            || (File.GetAttributes(fullPath) & FileAttributes.ReparsePoint) != 0
            || !Directory.Exists(stateDirectory)
            || (File.GetAttributes(stateDirectory) & FileAttributes.ReparsePoint) != 0)
        {
            throw new InvalidDataException(
                "The protected local-development enrollment is unavailable.");
        }

        if (verifyAcl)
        {
            VerifyProtectedAcl(new DirectoryInfo(stateDirectory).GetAccessControl(), directory: true);
            VerifyProtectedAcl(new FileInfo(fullPath).GetAccessControl(), directory: false);
        }

        byte[] bytes;
        using (FileStream stream = new(
            fullPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 4096,
            FileOptions.SequentialScan))
        {
            if (stream.Length is < 1 or > MaximumEnrollmentBytes)
            {
                throw new InvalidDataException(
                    "The protected local-development enrollment has an invalid size.");
            }

            bytes = new byte[checked((int)stream.Length)];
            stream.ReadExactly(bytes);
        }

        if (bytes.Length >= 3
            && bytes[0] == 0xEF
            && bytes[1] == 0xBB
            && bytes[2] == 0xBF)
        {
            throw new InvalidDataException(
                "The protected local-development enrollment must not contain a BOM.");
        }

        try
        {
            using JsonDocument document = JsonDocument.Parse(
                bytes,
                new JsonDocumentOptions
                {
                    AllowTrailingCommas = false,
                    CommentHandling = JsonCommentHandling.Disallow,
                    MaxDepth = 2,
                });
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                throw new InvalidDataException(
                    "The protected local-development enrollment must be an object.");
            }

            Dictionary<string, JsonElement> properties = ReadExactProperties(document.RootElement);
            if (!properties["schemaVersion"].TryGetInt32(out int schemaVersion)
                || schemaVersion != 1
                || properties["installationId"].ValueKind != JsonValueKind.String
                || !Guid.TryParseExact(
                    properties["installationId"].GetString(),
                    "D",
                    out Guid installationId)
                || installationId == Guid.Empty
                || properties["deploymentPolicyId"].ValueKind != JsonValueKind.String
                || !string.Equals(
                    properties["deploymentPolicyId"].GetString(),
                    PolicyId,
                    StringComparison.Ordinal))
            {
                throw new InvalidDataException(
                    "The protected local-development enrollment identity is invalid.");
            }

            string enrolledSid = properties["enrolledAccountSid"].ValueKind == JsonValueKind.String
                ? properties["enrolledAccountSid"].GetString()!
                : throw new InvalidDataException(
                    "The protected local-development enrollment SID is invalid.");
            SecurityIdentifier identity = new(enrolledSid);
            if (!identity.IsAccountSid()
                || identity.AccountDomainSid is null
                || !string.Equals(identity.Value, enrolledSid, StringComparison.Ordinal))
            {
                throw new InvalidDataException(
                    "The protected local-development enrollment SID is not a canonical account SID.");
            }

            return identity.Value;
        }
        catch (InvalidDataException)
        {
            throw;
        }
        catch (Exception exception) when (exception is
            JsonException or InvalidOperationException or ArgumentException)
        {
            throw new InvalidDataException(
                "The protected local-development enrollment is malformed.",
                exception);
        }
    }

    private static Dictionary<string, JsonElement> ReadExactProperties(JsonElement root)
    {
        HashSet<string> allowed = new(RequiredProperties, StringComparer.Ordinal);
        allowed.Add("cpuVoltageValidationEnabled");
        HashSet<string> seenIgnoreCase = new(StringComparer.OrdinalIgnoreCase);
        Dictionary<string, JsonElement> properties = new(StringComparer.Ordinal);
        foreach (JsonProperty property in root.EnumerateObject())
        {
            if (!allowed.Contains(property.Name)
                || !properties.TryAdd(property.Name, property.Value)
                || !seenIgnoreCase.Add(property.Name))
            {
                throw new InvalidDataException(
                    "The protected local-development enrollment has unknown or duplicate properties.");
            }
        }

        if (properties.Count != RequiredProperties.Length + (properties.ContainsKey("cpuVoltageValidationEnabled") ? 1 : 0)
            || RequiredProperties.Any(name => !properties.ContainsKey(name)))
        {
            throw new InvalidDataException(
                "The protected local-development enrollment is missing a required property.");
        }

        if (properties.TryGetValue("cpuVoltageValidationEnabled", out JsonElement capability)
            && capability.ValueKind != JsonValueKind.True)
        {
            throw new InvalidDataException("The optional CPU voltage capability must be true.");
        }

        foreach (string propertyName in RequiredProperties.Where(name => name != "schemaVersion"))
        {
            if (properties[propertyName].ValueKind != JsonValueKind.String)
            {
                throw new InvalidDataException(
                    $"The protected local-development enrollment property '{propertyName}' must be a string.");
            }
        }

        return properties;
    }

    private static void VerifyProtectedAcl(FileSystemSecurity security, bool directory)
    {
        SecurityIdentifier system = new(WellKnownSidType.LocalSystemSid, null);
        SecurityIdentifier administrators = new(
            WellKnownSidType.BuiltinAdministratorsSid,
            null);
        FileSystemAccessRule[] rules = security.GetAccessRules(
                includeExplicit: true,
                includeInherited: false,
                typeof(SecurityIdentifier))
            .Cast<FileSystemAccessRule>()
            .ToArray();
        InheritanceFlags expectedInheritance = directory
            ? InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit
            : InheritanceFlags.None;
        if (!security.AreAccessRulesProtected
            || security.GetOwner(typeof(SecurityIdentifier)) != administrators
            || rules.Length != 2
            || !HasFullControl(rules, system, expectedInheritance)
            || !HasFullControl(rules, administrators, expectedInheritance)
            || rules.Any(rule =>
                rule.AccessControlType != AccessControlType.Allow
                || (rule.IdentityReference != system
                    && rule.IdentityReference != administrators)))
        {
            throw new UnauthorizedAccessException(
                "The local-development enrollment is not protected for SYSTEM and administrators only.");
        }
    }

    private static bool HasFullControl(
        IEnumerable<FileSystemAccessRule> rules,
        SecurityIdentifier identity,
        InheritanceFlags inheritance) => rules.Any(rule =>
            rule.IdentityReference == identity
            && rule.AccessControlType == AccessControlType.Allow
            && (rule.FileSystemRights & FileSystemRights.FullControl)
                == FileSystemRights.FullControl
            && rule.InheritanceFlags == inheritance
            && rule.PropagationFlags == PropagationFlags.None);
}
