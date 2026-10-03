using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Win32.SafeHandles;
using OpenBlade.DeploymentBroker;

namespace OpenBlade.DevelopmentDeployment.Setup;

internal static class DevelopmentDeploymentLayout
{
    internal const string ServiceName = "OpenBlade Local Dev Deployment Broker";
    internal const string PolicyId = "openblade-local-dev-v1";
    internal const string BrokerExe = "OpenBlade.DeploymentBroker.exe";
    internal const string ClientExe = "OpenBlade.DeploymentClient.exe";
    internal const string TrustFile = "OpenBlade.LocalDevDeployment.json";

    internal static string CommonData =>
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);

    internal static string Programs =>
        Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);

    internal static string BrokerDirectory =>
        Path.Combine(Programs, "OpenBlade Local Dev Deployment Broker");

    internal static string ClientDirectory =>
        Path.Combine(Programs, "OpenBlade Local Dev Deployment Client");

    internal static string StateDirectory =>
        Path.Combine(CommonData, "OpenBlade", "LocalDevDeploymentBroker");

    internal static string InboxDirectory =>
        Path.Combine(CommonData, "OpenBlade", "LocalDevDeploymentInbox");

    internal static string ReceiptPath =>
        Path.Combine(CommonData, "OpenBlade", "LocalDevDeploymentReceipt.json");
}

internal sealed record DevelopmentTrustKey(
    [property: JsonPropertyName("keyId")] string KeyId,
    [property: JsonPropertyName("subjectPublicKeyInfoBase64")] string SubjectPublicKeyInfoBase64);

internal sealed record DevelopmentTrustPolicy(
    [property: JsonPropertyName("schemaVersion")] int SchemaVersion,
    [property: JsonPropertyName("enabled")] bool Enabled,
    [property: JsonPropertyName("policyId")] string PolicyId,
    [property: JsonPropertyName("signatureAlgorithm")] string SignatureAlgorithm,
    [property: JsonPropertyName("signingDomain")] string SigningDomain,
    [property: JsonPropertyName("manifestFileName")] string ManifestFileName,
    [property: JsonPropertyName("signatureFileName")] string SignatureFileName,
    [property: JsonPropertyName("installerFileName")] string InstallerFileName,
    [property: JsonPropertyName("productName")] string ProductName,
    [property: JsonPropertyName("bundleId")] string BundleId,
    [property: JsonPropertyName("providerKey")] string ProviderKey,
    [property: JsonPropertyName("upgradeCode")] string UpgradeCode,
    [property: JsonPropertyName("architecture")] string Architecture,
    [property: JsonPropertyName("minimumWindowsBuild")] int MinimumWindowsBuild,
    [property: JsonPropertyName("quietInstallArguments")] string QuietInstallArguments,
    [property: JsonPropertyName("trustedDevelopmentKeys")]
    IReadOnlyList<DevelopmentTrustKey> TrustedDevelopmentKeys);

internal static class DevelopmentDeploymentProvisioner
{
    internal static async Task<int> RunAsync(string[] args, CancellationToken cancellationToken)
    {
        if (args.Length != 1 || args[0] is not ("install" or "uninstall" or "status" or "upgrade-cpu-voltage"))
        {
            return 64;
        }

        switch (args[0])
        {
            case "upgrade-cpu-voltage":
                await UpgradeCpuVoltageAsync(cancellationToken).ConfigureAwait(false);
                return 0;
            case "install":
                await InstallAsync(cancellationToken).ConfigureAwait(false);
                return 0;
            case "uninstall":
                await UninstallAsync(cancellationToken).ConfigureAwait(false);
                return 0;
            default:
                return await RunServiceControlAsync(
                    ["query", DevelopmentDeploymentLayout.ServiceName],
                    cancellationToken,
                    tolerateFailure: true).ConfigureAwait(false)
                    ? 0
                    : 1;
        }
    }

    private static async Task UpgradeCpuVoltageAsync(CancellationToken cancellationToken)
    {
        string[] expectedPayload =
        [
            "OpenBlade.DevelopmentDeployment.Setup.exe",
            DevelopmentDeploymentLayout.BrokerExe,
            DevelopmentDeploymentLayout.ClientExe,
            DevelopmentDeploymentLayout.TrustFile,
        ];
        if (!Directory.EnumerateFileSystemEntries(AppContext.BaseDirectory)
            .Select(Path.GetFileName).Order(StringComparer.Ordinal)
            .SequenceEqual(expectedPayload.Order(StringComparer.Ordinal)))
        {
            throw new InvalidDataException("The attended upgrade requires the exact four-file provisioner payload.");
        }

        using FileStream brokerSource = OpenPayload(DevelopmentDeploymentLayout.BrokerExe);
        using FileStream clientSource = OpenPayload(DevelopmentDeploymentLayout.ClientExe);
        using FileStream trustSource = OpenPayload(DevelopmentDeploymentLayout.TrustFile);
        DevelopmentTrustKey trust = await ReadTrustAsync(cancellationToken).ConfigureAwait(false);
        BrokerPaths paths = BrokerPaths.CreateDefault();
        BrokerEnrollmentStore store = new(paths);
        BrokerEnrollment original = await store.LoadAsync(cancellationToken).ConfigureAwait(false);
        BrokerEnrollment upgraded = CreateCpuVoltageUpgrade(
            original, trust, ActiveConsoleUser.GetSid(), RequireBrokerVersion(brokerSource.Name));
        string brokerTarget = Path.Combine(DevelopmentDeploymentLayout.BrokerDirectory, DevelopmentDeploymentLayout.BrokerExe);
        string clientTarget = Path.Combine(DevelopmentDeploymentLayout.ClientDirectory, DevelopmentDeploymentLayout.ClientExe);
        RequireProtectedUpgradeTarget(brokerTarget, null);
        RequireProtectedUpgradeTarget(clientTarget, original.EnrolledAccountSid);
        if (!string.Equals(RequireBrokerVersion(brokerTarget), original.BrokerVersion, StringComparison.Ordinal))
        {
            throw new InvalidDataException("The installed broker version differs from its enrollment.");
        }
        RequireSafeAncestors(DevelopmentDeploymentLayout.ReceiptPath);
        VerifyUpgradeFileSecurity(DevelopmentDeploymentLayout.ReceiptPath, original.EnrolledAccountSid);
        if (!(await File.ReadAllBytesAsync(DevelopmentDeploymentLayout.ReceiptPath, cancellationToken).ConfigureAwait(false))
            .SequenceEqual(CreateReceipt(original)))
        {
            throw new InvalidDataException("The public receipt does not match the protected enrollment.");
        }

        using CpuUpgradeService service = CpuUpgradeService.Open();
        service.RequireConfiguration(disabled: false);
        RequireNoUnresolvedDeployment(paths.RootDirectory);
        using (WindowsCpuVoltageDeployment.RequirePriorClosedAndAcquireLease(requireExistingClosedHost: true)) { }

        // Disabling start closes the normal-user service-start race until every replacement is durable.
        // Any failure from this point leaves maintenance disabled for attended diagnosis, never uninstall.
        service.SetDisabled(true);
        await service.StopAndWaitAsync(cancellationToken).ConfigureAwait(false);
        using IDisposable cpuLease = WindowsCpuVoltageDeployment.RequirePriorClosedAndAcquireLease(requireExistingClosedHost: true);
        service.RequireStopped();
        RequireNoUnresolvedDeployment(paths.RootDirectory);
        BrokerEnrollment confirmed = await store.LoadAsync(cancellationToken).ConfigureAwait(false);
        if (confirmed != original)
        {
            throw new InvalidOperationException("The broker enrollment changed during maintenance admission.");
        }
        _ = CreateCpuVoltageUpgrade(confirmed, trust, ActiveConsoleUser.GetSid(), upgraded.BrokerVersion);
        RequireProtectedUpgradeTarget(brokerTarget, null);
        RequireProtectedUpgradeTarget(clientTarget, original.EnrolledAccountSid);
        await ReplaceUpgradeFileAsync(brokerTarget, brokerSource, cancellationToken).ConfigureAwait(false);
        await ReplaceUpgradeFileAsync(clientTarget, clientSource, cancellationToken).ConfigureAwait(false);
        using MemoryStream enrollmentBytes = new(BrokerEnrollmentCodec.SerializeCanonical(upgraded));
        await ReplaceUpgradeFileAsync(paths.EnrollmentPath, enrollmentBytes, cancellationToken).ConfigureAwait(false);
        using MemoryStream receiptBytes = new(CreateReceipt(upgraded));
        await ReplaceUpgradeFileAsync(DevelopmentDeploymentLayout.ReceiptPath, receiptBytes, cancellationToken).ConfigureAwait(false);
        if (await store.LoadAsync(cancellationToken).ConfigureAwait(false) != upgraded)
        {
            throw new InvalidDataException("The upgraded enrollment could not be verified.");
        }
        RequireProtectedUpgradeTarget(brokerTarget, null);
        RequireProtectedUpgradeTarget(clientTarget, original.EnrolledAccountSid);
        VerifyUpgradeFileSecurity(DevelopmentDeploymentLayout.ReceiptPath, original.EnrolledAccountSid);
        await VerifyUpgradePayloadAsync(brokerTarget, brokerSource, cancellationToken).ConfigureAwait(false);
        await VerifyUpgradePayloadAsync(clientTarget, clientSource, cancellationToken).ConfigureAwait(false);
        if (!(await File.ReadAllBytesAsync(DevelopmentDeploymentLayout.ReceiptPath, cancellationToken).ConfigureAwait(false))
            .SequenceEqual(CreateReceipt(upgraded)))
        {
            throw new InvalidDataException("The upgraded public receipt could not be verified.");
        }
        service.RequireStopped();
        try
        {
            service.SetDisabled(false);
            service.RequireConfiguration(disabled: false);
        }
        catch (Exception completionFailure)
        {
            try
            {
                service.SetDisabled(true);
                service.RequireConfiguration(disabled: true);
            }
            catch (Exception retentionFailure)
            {
                throw new AggregateException(
                    "Broker maintenance completion and disabled-state verification are unresolved.",
                    completionFailure, retentionFailure);
            }
            throw;
        }
    }

    internal static BrokerEnrollment CreateCpuVoltageUpgrade(
        BrokerEnrollment original, DevelopmentTrustKey trust, string consoleSid, string brokerVersion)
    {
        if (!string.Equals(original.EnrolledAccountSid, consoleSid, StringComparison.Ordinal)
            || !string.Equals(original.DevelopmentSigningKeyId, trust.KeyId, StringComparison.Ordinal)
            || !string.Equals(original.DevelopmentSigningSubjectPublicKeyInfoBase64, trust.SubjectPublicKeyInfoBase64, StringComparison.Ordinal))
        {
            throw new InvalidDataException("The attended upgrade must retain the enrolled console account and signing key.");
        }
        BrokerEnrollment upgraded = original with { BrokerVersion = brokerVersion, CpuVoltageValidationEnabled = true };
        _ = BrokerEnrollmentCodec.SerializeCanonical(upgraded);
        return upgraded;
    }

    private static FileStream OpenPayload(string name)
    {
        string path = RequirePayloadFile(name);
        RequireSafeAncestors(path);
        return new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
    }

    internal static void RequireNoUnresolvedDeployment(string stateDirectory)
    {
        // Enumeration propagates access failures and treats even malformed/directory markers as unresolved.
        if (Directory.EnumerateFileSystemEntries(stateDirectory)
            .Any(path => string.Equals(Path.GetFileName(path), "unresolved-deployment.json", StringComparison.OrdinalIgnoreCase)))
        {
            throw new InvalidOperationException("An unresolved deployment prevents broker maintenance.");
        }
    }

    internal static async Task ReplaceUpgradeFileAsync(string destination, Stream source, CancellationToken cancellationToken)
    {
        string temporary = destination + ".cpu-upgrade";
        await using (FileStream output = new FileInfo(temporary).Create(
            FileMode.CreateNew, FileSystemRights.FullControl, FileShare.None,
            65536, FileOptions.Asynchronous | FileOptions.WriteThrough,
            new FileInfo(destination).GetAccessControl()))
        {
            await source.CopyToAsync(output, cancellationToken).ConfigureAwait(false);
            await output.FlushAsync(cancellationToken).ConfigureAwait(false);
            output.Flush(flushToDisk: true);
        }
        // ReplaceFile preserves the already verified destination ACL. No durable state is deleted on failure.
        File.Replace(temporary, destination, destinationBackupFileName: null);
    }

    internal static async Task VerifyUpgradePayloadAsync(string destination, Stream source, CancellationToken cancellationToken)
    {
        using FileStream installed = new(destination, FileMode.Open, FileAccess.Read, FileShare.Read);
        source.Position = 0;
        byte[] installedHash = await SHA256.HashDataAsync(installed, cancellationToken).ConfigureAwait(false);
        byte[] sourceHash = await SHA256.HashDataAsync(source, cancellationToken).ConfigureAwait(false);
        if (installed.Length != source.Length
            || !CryptographicOperations.FixedTimeEquals(installedHash, sourceHash))
        {
            throw new InvalidDataException("The installed upgrade payload differs from the pinned attended payload.");
        }
    }

    private static void RequireSafeAncestors(string path)
    {
        for (string? current = Path.GetFullPath(path); current is not null; current = Path.GetDirectoryName(current))
        {
            if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
            {
                throw new UnauthorizedAccessException("Broker maintenance paths must not contain reparse points.");
            }
        }
    }

    private static void RequireProtectedUpgradeTarget(string path, string? readSid)
    {
        RequireSafeAncestors(path);
        DirectorySecurity actual = new DirectoryInfo(Path.GetDirectoryName(path)!).GetAccessControl();
        DirectorySecurity expected = CreateDirectorySecurity(readSid, FileSystemRights.ReadAndExecute);
        if (actual.GetSecurityDescriptorSddlForm(AccessControlSections.Access | AccessControlSections.Owner)
            != expected.GetSecurityDescriptorSddlForm(AccessControlSections.Access | AccessControlSections.Owner))
        {
            throw new UnauthorizedAccessException("The installed broker/client directory ACL differs from enrollment.");
        }
        VerifyUpgradeFileSecurity(path, readSid);
    }

    private static void VerifyUpgradeFileSecurity(string path, string? readSid)
    {
        RequireUpgradeFileSecurity(new FileInfo(path).GetAccessControl(), readSid);
    }

    internal static void RequireUpgradeFileSecurity(FileSecurity security, string? readSid)
    {
        SecurityIdentifier system = new(WellKnownSidType.LocalSystemSid, null);
        SecurityIdentifier administrators = new(WellKnownSidType.BuiltinAdministratorsSid, null);
        FileSystemAccessRule[] rules = security.GetAccessRules(true, true, typeof(SecurityIdentifier))
            .Cast<FileSystemAccessRule>().ToArray();
        const FileSystemRights writes = FileSystemRights.Write | FileSystemRights.Delete
            | FileSystemRights.ChangePermissions | FileSystemRights.TakeOwnership
            | (FileSystemRights)0x40000000 | (FileSystemRights)0x10000000;
        if (security.GetOwner(typeof(SecurityIdentifier)) != administrators
            || !rules.Any(rule => rule.IdentityReference == system && rule.AccessControlType == AccessControlType.Allow && rule.FileSystemRights == FileSystemRights.FullControl)
            || !rules.Any(rule => rule.IdentityReference == administrators && rule.AccessControlType == AccessControlType.Allow && rule.FileSystemRights == FileSystemRights.FullControl)
            || rules.Any(rule => rule.AccessControlType != AccessControlType.Allow
                || rule.IdentityReference != system && rule.IdentityReference != administrators
                && (rule.IdentityReference.Value != readSid || (rule.FileSystemRights & writes) != 0)))
        {
            throw new UnauthorizedAccessException("The installed broker/client/receipt file is not protected.");
        }
    }

    private static async Task InstallAsync(CancellationToken cancellationToken)
    {
        await RequireUnenrolledMachineAsync(cancellationToken).ConfigureAwait(false);

        string brokerSource = RequirePayloadFile(DevelopmentDeploymentLayout.BrokerExe);
        string clientSource = RequirePayloadFile(DevelopmentDeploymentLayout.ClientExe);
        DevelopmentTrustKey trust = await ReadTrustAsync(cancellationToken).ConfigureAwait(false);
        string enrolledSid = ActiveConsoleUser.GetSid();
        string brokerVersion = RequireBrokerVersion(brokerSource);
        BrokerEnrollment enrollment = new(
            BrokerEnrollmentCodec.CurrentSchemaVersion,
            Guid.NewGuid(),
            enrolledSid,
            DevelopmentDeploymentLayout.PolicyId,
            brokerVersion,
            trust.KeyId,
            trust.SubjectPublicKeyInfoBase64,
            DateTimeOffset.UtcNow);

        _ = BrokerEnrollmentCodec.SerializeCanonical(enrollment);

        try
        {
            CopyProtectedPayload(
                brokerSource,
                DevelopmentDeploymentLayout.BrokerDirectory,
                enrolledSid: null);
            CopyProtectedPayload(
                clientSource,
                DevelopmentDeploymentLayout.ClientDirectory,
                enrolledSid);

            Directory.CreateDirectory(DevelopmentDeploymentLayout.StateDirectory);
            SecureDirectory(DevelopmentDeploymentLayout.StateDirectory, null, FileSystemRights.Read);
            await new BrokerEnrollmentStore(BrokerPaths.CreateDefault())
                .CreateOnceAsync(enrollment, cancellationToken)
                .ConfigureAwait(false);

            Directory.CreateDirectory(DevelopmentDeploymentLayout.InboxDirectory);
            SecureDirectory(
                DevelopmentDeploymentLayout.InboxDirectory,
                enrolledSid,
                FileSystemRights.Modify);
            await WriteReceiptAsync(enrollment, enrolledSid, cancellationToken)
                .ConfigureAwait(false);

            string brokerPath = Path.Combine(
                DevelopmentDeploymentLayout.BrokerDirectory,
                DevelopmentDeploymentLayout.BrokerExe);
            await RunServiceControlAsync(
                [
                    "create",
                    DevelopmentDeploymentLayout.ServiceName,
                    "binPath=",
                    QuoteServiceBinaryPath(brokerPath),
                    "start=",
                    "demand",
                    "obj=",
                    "LocalSystem",
                    "DisplayName=",
                    DevelopmentDeploymentLayout.ServiceName,
                ],
                cancellationToken).ConfigureAwait(false);
            await RunServiceControlAsync(
                ["sdset", DevelopmentDeploymentLayout.ServiceName, CreateServiceSddl(enrolledSid)],
                cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            await UninstallAsync(CancellationToken.None).ConfigureAwait(false);
            throw;
        }
    }

    private static async Task RequireUnenrolledMachineAsync(CancellationToken cancellationToken)
    {
        bool serviceExists = await RunServiceControlAsync(
            ["query", DevelopmentDeploymentLayout.ServiceName],
            cancellationToken,
            tolerateFailure: true).ConfigureAwait(false);
        string[] paths =
        [
            DevelopmentDeploymentLayout.BrokerDirectory,
            DevelopmentDeploymentLayout.ClientDirectory,
            DevelopmentDeploymentLayout.StateDirectory,
            DevelopmentDeploymentLayout.InboxDirectory,
            DevelopmentDeploymentLayout.ReceiptPath,
        ];
        if (serviceExists || paths.Any(path => File.Exists(path) || Directory.Exists(path)))
        {
            throw new InvalidOperationException(
                "Local-development deployment is already enrolled or requires attended cleanup.");
        }
    }

    private static async Task UninstallAsync(CancellationToken cancellationToken)
    {
        await RunServiceControlAsync(
            ["stop", DevelopmentDeploymentLayout.ServiceName],
            cancellationToken,
            tolerateFailure: true).ConfigureAwait(false);
        await RunServiceControlAsync(
            ["delete", DevelopmentDeploymentLayout.ServiceName],
            cancellationToken,
            tolerateFailure: true).ConfigureAwait(false);

        string[] paths =
        [
            DevelopmentDeploymentLayout.ReceiptPath,
            DevelopmentDeploymentLayout.InboxDirectory,
            DevelopmentDeploymentLayout.StateDirectory,
            DevelopmentDeploymentLayout.ClientDirectory,
            DevelopmentDeploymentLayout.BrokerDirectory,
        ];
        foreach (string path in paths)
        {
            await DeleteWithRetryAsync(path, cancellationToken).ConfigureAwait(false);
        }
    }

    private static async Task<bool> RunServiceControlAsync(
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken,
        bool tolerateFailure = false)
    {
        ProcessStartInfo startInfo = new(Path.Combine(Environment.SystemDirectory, "sc.exe"))
        {
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (string argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using Process process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Could not start the Service Control utility.");
        await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        if (process.ExitCode != 0 && !tolerateFailure)
        {
            throw new InvalidOperationException(
                $"The Service Control utility failed with exit code {process.ExitCode}.");
        }

        return process.ExitCode == 0;
    }

    private static string RequirePayloadFile(string fileName)
    {
        string path = Path.Combine(AppContext.BaseDirectory, fileName);
        if (!File.Exists(path) || (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
        {
            throw new InvalidDataException($"The fixed payload '{fileName}' is unavailable.");
        }

        return path;
    }

    private static async Task<DevelopmentTrustKey> ReadTrustAsync(
        CancellationToken cancellationToken)
    {
        string path = RequirePayloadFile(DevelopmentDeploymentLayout.TrustFile);
        DevelopmentTrustPolicy policy = JsonSerializer.Deserialize<DevelopmentTrustPolicy>(
            await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false),
            new JsonSerializerOptions { UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow })
            ?? throw new InvalidDataException("The local-development trust file is invalid.");
        if (policy.SchemaVersion != 2
            || !policy.Enabled
            || !string.Equals(policy.PolicyId, DevelopmentDeploymentLayout.PolicyId, StringComparison.Ordinal)
            || !string.Equals(policy.SignatureAlgorithm, "ECDSA-P256-SHA256-P1363", StringComparison.Ordinal)
            || !string.Equals(policy.SigningDomain, "OpenBlade-Local-Dev-Deployment-Manifest-v1", StringComparison.Ordinal)
            || !string.Equals(policy.ManifestFileName, "OpenBlade-Local-Dev-Deployment.json", StringComparison.Ordinal)
            || !string.Equals(policy.SignatureFileName, "OpenBlade-Local-Dev-Deployment.sig", StringComparison.Ordinal)
            || !string.Equals(policy.InstallerFileName, "OpenBlade-Setup.exe", StringComparison.Ordinal)
            || !string.Equals(policy.ProductName, "OpenBlade", StringComparison.Ordinal)
            || !string.Equals(policy.BundleId, "OSSBlade.OpenBlade", StringComparison.Ordinal)
            || !string.Equals(policy.ProviderKey, "OSSBlade.OpenBlade", StringComparison.Ordinal)
            || !string.Equals(policy.UpgradeCode, "FF859CA9-D69F-4995-A2AD-69E540E45724", StringComparison.Ordinal)
            || !string.Equals(policy.Architecture, "x64", StringComparison.Ordinal)
            || policy.MinimumWindowsBuild != 26100
            || !string.Equals(policy.QuietInstallArguments, "/install /quiet /norestart", StringComparison.Ordinal)
            || policy.TrustedDevelopmentKeys.Count != 1)
        {
            throw new InvalidDataException("The local-development trust policy is unexpected.");
        }

        return policy.TrustedDevelopmentKeys[0];
    }

    private static string RequireBrokerVersion(string brokerPath)
    {
        string? productVersion = FileVersionInfo.GetVersionInfo(brokerPath).ProductVersion;
        string version = productVersion?.Split('+', 2)[0] ?? string.Empty;
        if (!Version.TryParse(version, out _))
        {
            throw new InvalidDataException("The broker payload version is unavailable.");
        }

        return version;
    }

    private static void CopyProtectedPayload(
        string source,
        string targetDirectory,
        string? enrolledSid)
    {
        Directory.CreateDirectory(targetDirectory);
        SecureDirectory(targetDirectory, enrolledSid, FileSystemRights.ReadAndExecute);
        string target = Path.Combine(targetDirectory, Path.GetFileName(source));
        File.Copy(source, target, overwrite: false);
    }

    internal static DirectorySecurity CreateDirectorySecurity(
        string? userSid,
        FileSystemRights userRights)
    {
        DirectorySecurity security = new();
        SecurityIdentifier administrators = new(
            WellKnownSidType.BuiltinAdministratorsSid,
            null);
        security.SetOwner(administrators);
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        AddDirectoryRule(
            security,
            new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null),
            FileSystemRights.FullControl);
        AddDirectoryRule(security, administrators, FileSystemRights.FullControl);
        if (userSid is not null)
        {
            AddDirectoryRule(security, new SecurityIdentifier(userSid), userRights);
        }

        return security;
    }

    internal static string CreateServiceSddl(string sid)
    {
        _ = new SecurityIdentifier(sid);
        return "D:"
            + "(A;;CCLCSWRPWPDTLOCRRC;;;SY)"
            + "(A;;CCDCLCSWRPWPDTLOCRSDRCWDWO;;;BA)"
            + $"(A;;CCLCRP;;;{sid})";
    }

    internal static string QuoteServiceBinaryPath(string path) => $"\"{path}\"";

    private static void AddDirectoryRule(
        DirectorySecurity security,
        SecurityIdentifier sid,
        FileSystemRights rights)
    {
        security.AddAccessRule(new FileSystemAccessRule(
            sid,
            rights,
            InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
            PropagationFlags.None,
            AccessControlType.Allow));
    }

    private static void SecureDirectory(
        string path,
        string? sid,
        FileSystemRights userRights)
    {
        new DirectoryInfo(path).SetAccessControl(CreateDirectorySecurity(sid, userRights));
    }

    internal static byte[] CreateReceipt(BrokerEnrollment enrollment)
    {
        using MemoryStream output = new();
        using (Utf8JsonWriter writer = new(output))
        {
            writer.WriteStartObject();
            writer.WriteNumber("schemaVersion", 1);
            writer.WriteString("installationId", enrollment.InstallationId.ToString("D"));
            writer.WriteString("enrolledAccountSid", enrollment.EnrolledAccountSid);
            writer.WriteString("releasePolicyId", enrollment.DeploymentPolicyId);
            writer.WriteString(
                "developmentSigningKeyId",
                enrollment.DevelopmentSigningKeyId);
            writer.WriteString(
                "developmentSigningSubjectPublicKeyInfoBase64",
                enrollment.DevelopmentSigningSubjectPublicKeyInfoBase64);
            writer.WriteString("brokerVersion", enrollment.BrokerVersion);
            writer.WriteString(
                "enrolledAt",
                enrollment.EnrolledAt.UtcDateTime.ToString(
                    "yyyy-MM-dd'T'HH:mm:ss.fffffff'Z'",
                    System.Globalization.CultureInfo.InvariantCulture));
            if (enrollment.CpuVoltageValidationEnabled)
            {
                writer.WriteBoolean("cpuVoltageValidationEnabled", true);
            }
            writer.WriteEndObject();
        }

        return output.ToArray();
    }

    private static async Task WriteReceiptAsync(
        BrokerEnrollment enrollment,
        string enrolledSid,
        CancellationToken cancellationToken)
    {
        byte[] bytes = CreateReceipt(enrollment);
        await using (FileStream stream = new(
            DevelopmentDeploymentLayout.ReceiptPath,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            bufferSize: 4096,
            FileOptions.WriteThrough | FileOptions.Asynchronous))
        {
            await stream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
            await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            stream.Flush(flushToDisk: true);
        }

        FileSecurity security = new();
        SecurityIdentifier administrators = new(
            WellKnownSidType.BuiltinAdministratorsSid,
            null);
        security.SetOwner(administrators);
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        security.AddAccessRule(new FileSystemAccessRule(
            new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null),
            FileSystemRights.FullControl,
            AccessControlType.Allow));
        security.AddAccessRule(new FileSystemAccessRule(
            administrators,
            FileSystemRights.FullControl,
            AccessControlType.Allow));
        security.AddAccessRule(new FileSystemAccessRule(
            new SecurityIdentifier(enrolledSid),
            FileSystemRights.Read,
            AccessControlType.Allow));
        new FileInfo(DevelopmentDeploymentLayout.ReceiptPath).SetAccessControl(security);
    }

    private static async Task DeleteWithRetryAsync(
        string path,
        CancellationToken cancellationToken)
    {
        const int maximumAttempts = 20;
        for (int attempt = 0; attempt < maximumAttempts; attempt++)
        {
            try
            {
                if (File.Exists(path))
                {
                    File.Delete(path);
                }
                else if (Directory.Exists(path))
                {
                    Directory.Delete(path, recursive: true);
                }

                return;
            }
            catch (Exception exception) when (
                attempt < maximumAttempts - 1
                && IsRetryableDeleteFailure(exception))
            {
                await Task.Delay(Math.Min(100 * (attempt + 1), 1000), cancellationToken)
                    .ConfigureAwait(false);
            }
        }
    }

    internal static bool IsRetryableDeleteFailure(Exception exception) =>
        exception is IOException or UnauthorizedAccessException;
}

// Fixed-service maintenance only: no caller-supplied service, executable, account or arguments.
internal sealed class CpuUpgradeService : IDisposable
{
    private readonly SafeServiceHandle service;

    private CpuUpgradeService(SafeServiceHandle service) => this.service = service;

    internal static CpuUpgradeService Open()
    {
        using SafeServiceHandle manager = Native.OpenSCManagerW(null, null, 1);
        if (manager.IsInvalid) { throw Failure(); }
        SafeServiceHandle service = Native.OpenServiceW(manager, DevelopmentDeploymentLayout.ServiceName, 1 | 2 | 4 | 0x20);
        if (service.IsInvalid) { service.Dispose(); throw Failure(); }
        return new CpuUpgradeService(service);
    }

    internal static bool IsExpectedConfiguration(uint type, uint start, string binary, string account, bool disabled) =>
        type == 0x10 && start == (disabled ? 4u : 3u)
        && string.Equals(account, "LocalSystem", StringComparison.Ordinal)
        && string.Equals(binary, DevelopmentDeploymentProvisioner.QuoteServiceBinaryPath(
            Path.Combine(DevelopmentDeploymentLayout.BrokerDirectory, DevelopmentDeploymentLayout.BrokerExe)), StringComparison.OrdinalIgnoreCase);

    internal void RequireConfiguration(bool disabled)
    {
        _ = Native.QueryServiceConfigW(service, 0, 0, out uint size);
        if (Marshal.GetLastWin32Error() != 122 || size == 0 || size > 8192) { throw Failure(); }
        nint buffer = Marshal.AllocHGlobal(checked((int)size));
        try
        {
            if (!Native.QueryServiceConfigW(service, buffer, size, out _)) { throw Failure(); }
            Configuration config = Marshal.PtrToStructure<Configuration>(buffer);
            if (!IsExpectedConfiguration(config.Type, config.Start,
                Marshal.PtrToStringUni(config.Binary) ?? string.Empty,
                Marshal.PtrToStringUni(config.Account) ?? string.Empty, disabled))
            {
                throw new InvalidDataException("The fixed broker service configuration differs from enrollment.");
            }
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }

    internal void SetDisabled(bool disabled)
    {
        if (!Native.ChangeServiceConfigW(service, uint.MaxValue, disabled ? 4u : 3u, uint.MaxValue,
            null, null, 0, null, null, null, null)) { throw Failure(); }
        RequireConfiguration(disabled);
    }

    internal async Task StopAndWaitAsync(CancellationToken cancellationToken)
    {
        using CancellationTokenSource deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(30));
        Status status = ReadStatus();
        if (status.State == 1 && status.ProcessId == 0) { return; }
        if (status.State != 4 || status.ProcessId == 0)
        {
            throw new InvalidOperationException("The broker is neither stopped nor stably running; maintenance cannot proceed.");
        }
        using Process process = Process.GetProcessById(checked((int)status.ProcessId));
        // Capture a process handle before requesting stop; a later reused PID is not terminal evidence.
        _ = process.Handle;
        string expectedImage = Path.Combine(DevelopmentDeploymentLayout.BrokerDirectory, DevelopmentDeploymentLayout.BrokerExe);
        Status confirmed = ReadStatus();
        if (!string.Equals(process.MainModule?.FileName, expectedImage, StringComparison.OrdinalIgnoreCase)
            || confirmed.State != 4 || confirmed.ProcessId != status.ProcessId)
        {
            throw new InvalidOperationException("The running service PID is not the exact protected broker image.");
        }
        if (!Native.ControlService(service, 1, out _)) { throw Failure(); }
        await process.WaitForExitAsync(deadline.Token).ConfigureAwait(false);
        while (ReadStatus().State != 1)
        {
            await Task.Delay(100, deadline.Token).ConfigureAwait(false);
        }
        RequireStopped();
    }

    internal void RequireStopped()
    {
        RequireConfiguration(disabled: true);
        Status status = ReadStatus();
        if (status.State != 1 || status.ProcessId != 0)
        {
            throw new InvalidOperationException("The broker has not reached a verified stopped state.");
        }
    }

    private Status ReadStatus()
    {
        if (!Native.QueryServiceStatusEx(service, 0, out Status status, checked((uint)Marshal.SizeOf<Status>()), out _)) { throw Failure(); }
        return status;
    }

    private static Win32Exception Failure() => new(Marshal.GetLastWin32Error(), "Fixed broker maintenance service control failed.");
    public void Dispose() => service.Dispose();

    [StructLayout(LayoutKind.Sequential)]
    private struct Status
    {
        internal uint Type, State, Controls, ExitCode, SpecificExitCode, Checkpoint, WaitHint, ProcessId, Flags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Configuration
    {
        internal uint Type, Start, ErrorControl;
        internal nint Binary, Group;
        internal uint Tag;
        internal nint Dependencies, Account, DisplayName;
    }

    private sealed class SafeServiceHandle : SafeHandleZeroOrMinusOneIsInvalid
    {
        private SafeServiceHandle() : base(true) { }
        protected override bool ReleaseHandle() => Native.CloseServiceHandle(handle);
    }

    private static class Native
    {
        [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        internal static extern SafeServiceHandle OpenSCManagerW(string? machine, string? database, uint access);
        [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        internal static extern SafeServiceHandle OpenServiceW(SafeServiceHandle manager, string name, uint access);
        [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool QueryServiceConfigW(SafeServiceHandle service, nint buffer, uint size, out uint needed);
        [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool ChangeServiceConfigW(SafeServiceHandle service, uint type, uint start, uint error,
            string? binary, string? group, nint tag, string? dependencies, string? account, string? password, string? display);
        [DllImport("advapi32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool QueryServiceStatusEx(SafeServiceHandle service, int level, out Status status, uint size, out uint needed);
        [DllImport("advapi32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool ControlService(SafeServiceHandle service, uint control, out Status status);
        [DllImport("advapi32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool CloseServiceHandle(nint service);
    }
}

internal static class ActiveConsoleUser
{
    private const int ErrorInsufficientBuffer = 122;

    internal static string GetSid()
    {
        uint sessionId = Native.WTSGetActiveConsoleSessionId();
        if (sessionId == uint.MaxValue)
        {
            throw new InvalidOperationException("No active console user is available.");
        }

        string userName = QuerySessionString(sessionId, Native.WtsInfoClass.UserName);
        string domainName = QuerySessionString(sessionId, Native.WtsInfoClass.DomainName);
        if (string.IsNullOrWhiteSpace(userName) || string.IsNullOrWhiteSpace(domainName))
        {
            throw new InvalidOperationException("The active console account is unavailable.");
        }

        string accountName = domainName + "\\" + userName;
        string sid = LookupSid(accountName);
        string confirmedUserName = QuerySessionString(
            sessionId,
            Native.WtsInfoClass.UserName);
        string confirmedDomainName = QuerySessionString(
            sessionId,
            Native.WtsInfoClass.DomainName);
        if (Native.WTSGetActiveConsoleSessionId() != sessionId
            || !string.Equals(userName, confirmedUserName, StringComparison.Ordinal)
            || !string.Equals(domainName, confirmedDomainName, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "The active console account changed during enrollment.");
        }

        return sid;
    }

    private static string QuerySessionString(uint sessionId, Native.WtsInfoClass infoClass)
    {
        if (!Native.WTSQuerySessionInformationW(
                nint.Zero,
                sessionId,
                infoClass,
                out nint buffer,
                out _))
        {
            throw new Win32Exception(
                Marshal.GetLastWin32Error(),
                "The active console account could not be queried.");
        }

        try
        {
            return Marshal.PtrToStringUni(buffer) ?? string.Empty;
        }
        finally
        {
            Native.WTSFreeMemory(buffer);
        }
    }

    private static string LookupSid(string accountName)
    {
        uint sidLength = 0;
        uint domainLength = 0;
        _ = Native.LookupAccountNameW(
            null,
            accountName,
            null,
            ref sidLength,
            null,
            ref domainLength,
            out _);
        int sizingError = Marshal.GetLastWin32Error();
        if (sizingError != ErrorInsufficientBuffer || sidLength == 0)
        {
            throw new Win32Exception(
                sizingError,
                "The active console SID size could not be resolved.");
        }

        byte[] sidBytes = new byte[sidLength];
        StringBuilder referencedDomain = new(checked((int)domainLength));
        if (!Native.LookupAccountNameW(
                null,
                accountName,
                sidBytes,
                ref sidLength,
                referencedDomain,
                ref domainLength,
                out _))
        {
            throw new Win32Exception(
                Marshal.GetLastWin32Error(),
                "The active console SID could not be resolved.");
        }

        return new SecurityIdentifier(sidBytes, 0).Value;
    }

    private static class Native
    {
        internal enum WtsInfoClass
        {
            UserName = 5,
            DomainName = 7,
        }

        internal enum SidNameUse
        {
            User = 1,
            Group,
            Domain,
            Alias,
            WellKnownGroup,
            DeletedAccount,
            Invalid,
            Unknown,
            Computer,
            Label,
            LogonSession,
        }

        [DllImport("kernel32.dll")]
        internal static extern uint WTSGetActiveConsoleSessionId();

        [DllImport("wtsapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool WTSQuerySessionInformationW(
            nint server,
            uint sessionId,
            WtsInfoClass infoClass,
            out nint buffer,
            out uint bytesReturned);

        [DllImport("wtsapi32.dll")]
        internal static extern void WTSFreeMemory(nint memory);

        [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool LookupAccountNameW(
            string? systemName,
            string accountName,
            byte[]? sid,
            ref uint sidSize,
            StringBuilder? referencedDomainName,
            ref uint referencedDomainNameSize,
            out SidNameUse use);
    }
}
