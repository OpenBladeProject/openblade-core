using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
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
        if (args.Length != 1 || args[0] is not ("install" or "uninstall" or "status"))
        {
            return 64;
        }

        switch (args[0])
        {
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
