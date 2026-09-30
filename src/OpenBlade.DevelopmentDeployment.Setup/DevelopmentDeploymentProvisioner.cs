using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Security.Cryptography;
using System.ServiceProcess;
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
        if (args.Length != 1 || args[0] is not ("install" or "repair" or "uninstall" or "status"))
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
            case "repair":
                await RepairAsync(cancellationToken).ConfigureAwait(false);
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

    private static async Task RepairAsync(CancellationToken cancellationToken)
    {
        // This attended verb replaces binaries only. Enrollment and all operation history
        // remain authoritative; there is deliberately no install/uninstall fallback.
        RequireExactRepairPackage(AppContext.BaseDirectory);
        using FileStream broker = OpenRepairSource(DevelopmentDeploymentLayout.BrokerExe);
        using FileStream client = OpenRepairSource(DevelopmentDeploymentLayout.ClientExe);
        using FileStream provisioner = OpenRepairSource("OpenBlade.DevelopmentDeployment.Setup.exe");
        using FileStream trustFile = OpenRepairSource(DevelopmentDeploymentLayout.TrustFile);
        BrokerEnrollment enrollment = await new BrokerEnrollmentStore(BrokerPaths.CreateDefault())
            .LoadAsync(cancellationToken).ConfigureAwait(false);
        DevelopmentTrustKey trust = await ReadTrustAsync(cancellationToken).ConfigureAwait(false);
        string sid = ActiveConsoleUser.GetSid();
        string version = RequireBrokerVersion(broker.Name);
        RequireRepairIdentity(enrollment, trust, sid, version);
        if (!string.Equals(RequireBrokerVersion(client.Name), version, StringComparison.Ordinal)
            || !string.Equals(RequireBrokerVersion(provisioner.Name), version, StringComparison.Ordinal))
        {
            throw new InvalidDataException("Repair package executables must have the enrolled broker version.");
        }

        string brokerTarget = Path.Combine(DevelopmentDeploymentLayout.BrokerDirectory, DevelopmentDeploymentLayout.BrokerExe);
        string clientTarget = Path.Combine(DevelopmentDeploymentLayout.ClientDirectory, DevelopmentDeploymentLayout.ClientExe);
        RequireProtectedRepairPath(brokerTarget, null);
        RequireProtectedRepairPath(clientTarget, sid);
        RequireProtectedRepairFile(DevelopmentDeploymentLayout.ReceiptPath, sid);
        RequireRepairReceipt(enrollment, await File.ReadAllBytesAsync(
            DevelopmentDeploymentLayout.ReceiptPath, cancellationToken).ConfigureAwait(false));
        if (!string.Equals(RequireBrokerVersion(brokerTarget), version, StringComparison.Ordinal)
            || !string.Equals(RequireBrokerVersion(clientTarget), version, StringComparison.Ordinal))
        {
            throw new InvalidDataException("Installed executable versions do not match the existing enrollment.");
        }

        using var service = new ServiceController(DevelopmentDeploymentLayout.ServiceName);
        RepairServiceConfiguration.RequireConfiguration(service.ServiceHandle, brokerTarget);
        RequireRepairIdentity(enrollment, trust, ActiveConsoleUser.GetSid(), version);
        await RunRepairTransactionAsync(
            async token =>
            {
                RepairServiceConfiguration.SetStartType(service.ServiceHandle, 4);
                if (service.Status != ServiceControllerStatus.Stopped)
                {
                    if (service.Status != ServiceControllerStatus.StopPending)
                    {
                        service.Stop(stopDependentServices: false);
                    }
                    await WaitForRepairStopAsync(() =>
                    {
                        service.Refresh();
                        return service.Status == ServiceControllerStatus.Stopped;
                    }, TimeSpan.FromSeconds(30), token).ConfigureAwait(false);
                }
            },
            token => ReplaceRepairPayloadsAsync(broker, brokerTarget, client, clientTarget,
                _ => Task.CompletedTask, token, verifyReplacement: () =>
                {
                    RequireMatchingRepairBytes(broker, brokerTarget);
                    RequireMatchingRepairBytes(client, clientTarget);
                }),
            () =>
            {
                service.Refresh();
                if (service.Status != ServiceControllerStatus.Stopped)
                {
                    throw new InvalidOperationException("Repair cannot finish unless the fixed broker remains stopped.");
                }
                RepairServiceConfiguration.SetStartType(service.ServiceHandle, 3);
            }, cancellationToken).ConfigureAwait(false);
        // Remain stopped: starting the repaired broker is a separate explicit action.
    }

    internal static void RequireExactRepairPackage(string directory)
    {
        RejectRepairReparseAncestors(directory);
        string[] expected = [DevelopmentDeploymentLayout.BrokerExe, DevelopmentDeploymentLayout.ClientExe,
            "OpenBlade.DevelopmentDeployment.Setup.exe", DevelopmentDeploymentLayout.TrustFile];
        string[] actual = Directory.GetFileSystemEntries(directory).Select(Path.GetFileName).OfType<string>().ToArray();
        if (!actual.Order(StringComparer.Ordinal).SequenceEqual(expected.Order(StringComparer.Ordinal)))
        {
            throw new InvalidDataException("Repair requires the exact four-file provisioner package.");
        }
        foreach (string name in expected)
        {
            string path = Path.Combine(directory, name);
            RejectRepairReparseAncestors(path);
            if (!File.Exists(path))
            {
                throw new InvalidDataException("Repair package contains a non-file payload.");
            }
        }
    }

    private static FileStream OpenRepairSource(string name) => new(
        RequirePayloadFile(name), FileMode.Open, FileAccess.Read, FileShare.Read);

    internal static void RequireRepairIdentity(BrokerEnrollment enrollment, DevelopmentTrustKey trust,
        string activeSid, string version)
    {
        _ = BrokerEnrollmentCodec.SerializeCanonical(enrollment);
        if (!string.Equals(enrollment.EnrolledAccountSid, activeSid, StringComparison.Ordinal)
            || !string.Equals(enrollment.BrokerVersion, version, StringComparison.Ordinal)
            || !string.Equals(enrollment.DeploymentPolicyId, DevelopmentDeploymentLayout.PolicyId, StringComparison.Ordinal)
            || !string.Equals(enrollment.DevelopmentSigningKeyId, trust.KeyId, StringComparison.Ordinal)
            || !string.Equals(enrollment.DevelopmentSigningSubjectPublicKeyInfoBase64,
                trust.SubjectPublicKeyInfoBase64, StringComparison.Ordinal))
        {
            throw new InvalidDataException("Repair must preserve the enrolled account, version, policy and signing key.");
        }
    }

    internal static void RequireRepairReceipt(BrokerEnrollment enrollment, ReadOnlySpan<byte> receipt)
    {
        if (!receipt.SequenceEqual(CreateReceipt(enrollment)))
        {
            throw new InvalidDataException("Repair receipt does not match the unchanged enrollment.");
        }
    }

    internal static async Task WaitForRepairStopAsync(Func<bool> isStopped, TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        long started = Stopwatch.GetTimestamp();
        while (!isStopped())
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (Stopwatch.GetElapsedTime(started) >= timeout)
            {
                throw new System.TimeoutException("The fixed deployment broker did not stop within the repair deadline.");
            }
            await Task.Delay(TimeSpan.FromMilliseconds(100), cancellationToken).ConfigureAwait(false);
        }
        cancellationToken.ThrowIfCancellationRequested();
    }

    internal static async Task ReplaceRepairPayloadsAsync(Stream broker, string brokerTarget,
        Stream client, string clientTarget, Func<CancellationToken, Task> stop,
        CancellationToken cancellationToken, Action<string, string, string?>? replace = null,
        Action? verifyReplacement = null)
    {
        await stop(cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        replace ??= File.Replace;
        string suffix = ".repair-" + Guid.NewGuid().ToString("N");
        string[] targets = [clientTarget, brokerTarget];
        Stream[] sources = [client, broker];
        string[] staged = targets.Select(path => path + suffix + ".new").ToArray();
        string[] backups = targets.Select(path => path + suffix + ".old").ToArray();
        int replaced = 0;
        int attempted = -1;
        bool retainBackups = false;
        try
        {
            for (int index = 0; index < targets.Length; index++)
            {
                await using FileStream output = new(staged[index], FileMode.CreateNew, FileAccess.Write,
                    FileShare.None, 81920, FileOptions.Asynchronous | FileOptions.WriteThrough);
                await sources[index].CopyToAsync(output, cancellationToken).ConfigureAwait(false);
                await output.FlushAsync(cancellationToken).ConfigureAwait(false);
                output.Flush(flushToDisk: true);
            }
            for (; replaced < targets.Length; replaced++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                attempted = replaced;
                replace(staged[replaced], targets[replaced], backups[replaced]);
            }
            verifyReplacement?.Invoke();
        }
        catch (Exception failure)
        {
            List<Exception> failures = [failure];
            // A failing native replacement may already have produced its backup.
            // Include that attempted file, rather than assuming a throw means no rename.
            for (int index = attempted; index >= 0; index--)
            {
                if (!File.Exists(backups[index]))
                {
                    continue;
                }
                try { replace(backups[index], targets[index], null); }
                catch (Exception rollbackFailure) { failures.Add(rollbackFailure); retainBackups = true; }
            }
            if (failures.Count > 1)
            {
                throw new AggregateException("Repair failed and rollback was incomplete; broker remains stopped and backups are retained.", failures);
            }
            throw;
        }
        finally
        {
            // Each replacement is atomic, not the pair. No retries or restart after a
            // failed replacement; failed rollback retains protected copies for inspection.
            foreach (string path in staged.Concat(retainBackups ? [] : backups))
            {
                if (File.Exists(path))
                {
                    File.Delete(path);
                }
            }
        }
    }

    internal static async Task RunRepairTransactionAsync(Func<CancellationToken, Task> disableAndStop,
        Func<CancellationToken, Task> replacePayloads, Action restoreDemand, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            Task stop = Task.Run(() => disableAndStop(cancellationToken), CancellationToken.None);
            try
            {
                await stop.WaitAsync(TimeSpan.FromSeconds(35), cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                if (!stop.IsCompleted)
                {
                    _ = ObserveLateRepairStopAsync(stop);
                }
            }
            cancellationToken.ThrowIfCancellationRequested();
            await replacePayloads(cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            restoreDemand();
        }
        catch (Exception exception)
        {
            throw new InvalidOperationException(
                "Attended broker repair did not complete. No start was requested. The broker may remain disabled; confirm it is stopped before repeating repair. Existing enrollment and deployment history were preserved.", exception);
        }
    }

    private static async Task ObserveLateRepairStopAsync(Task stop)
    {
        try { await stop.ConfigureAwait(false); }
        catch (Exception exception) { Trace.TraceError("Late fixed broker stop failed: {0}", exception); }
        // A late stop never advances to payload replacement or restores start access.
    }

    private static void RequireMatchingRepairBytes(Stream expected, string installed)
    {
        expected.Position = 0;
        byte[] expectedHash = SHA256.HashData(expected);
        using FileStream actual = new(installed, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (!SHA256.HashData(actual).AsSpan().SequenceEqual(expectedHash))
        {
            throw new InvalidDataException("Repair executable verification failed.");
        }
    }

    private static void RejectRepairReparseAncestors(string path)
    {
        for (string? current = Path.GetFullPath(path); current is not null; current = Path.GetDirectoryName(current))
        {
            if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
            {
                throw new InvalidDataException("Repair does not follow reparse points.");
            }
        }
    }

    private static void RequireProtectedRepairPath(string path, string? sid)
    {
        RejectRepairReparseAncestors(path);
        DirectorySecurity actual = new DirectoryInfo(Path.GetDirectoryName(path)!).GetAccessControl();
        DirectorySecurity expected = CreateDirectorySecurity(sid, FileSystemRights.ReadAndExecute);
        static string RuleKey(FileSystemAccessRule rule) =>
            $"{rule.IdentityReference.Value}:{rule.AccessControlType}:{rule.FileSystemRights}:{rule.InheritanceFlags}:{rule.PropagationFlags}";
        var actualRules = actual.GetAccessRules(true, true, typeof(SecurityIdentifier)).Cast<FileSystemAccessRule>();
        var expectedRules = expected.GetAccessRules(true, true, typeof(SecurityIdentifier)).Cast<FileSystemAccessRule>();
        if (!actual.AreAccessRulesProtected
            || actual.GetOwner(typeof(SecurityIdentifier)) != expected.GetOwner(typeof(SecurityIdentifier))
            || !actualRules.Select(RuleKey).Order(StringComparer.Ordinal)
                .SequenceEqual(expectedRules.Select(RuleKey).Order(StringComparer.Ordinal)))
        {
            throw new UnauthorizedAccessException("Repair executable directory access is unexpected.");
        }
        RequireProtectedRepairFile(path, sid);
    }

    private static void RequireProtectedRepairFile(string path, string? sid)
    {
        RejectRepairReparseAncestors(path);
        FileSecurity security = new FileInfo(path).GetAccessControl();
        var admins = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null);
        var system = new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null);
        if (security.GetOwner(typeof(SecurityIdentifier)) != admins)
        {
            throw new UnauthorizedAccessException("Repair files must remain administrator-owned.");
        }
        foreach (FileSystemAccessRule rule in security.GetAccessRules(true, true, typeof(SecurityIdentifier)))
        {
            if (rule.AccessControlType != AccessControlType.Allow)
            {
                throw new UnauthorizedAccessException("Repair file access rules are unexpected.");
            }
            if (rule.IdentityReference == admins || rule.IdentityReference == system)
            {
                continue;
            }
            if (rule.IdentityReference.Value != sid
                || (rule.FileSystemRights & ~(FileSystemRights.ReadAndExecute | FileSystemRights.Synchronize)) != 0)
            {
                throw new UnauthorizedAccessException("Repair files may not be writable by the enrolled user or other accounts.");
            }
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
            || policy.TrustedDevelopmentKeys is not { Count: 1 })
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

internal static class RepairServiceConfiguration
{
    internal static void SetStartType(SafeHandle service, uint startType)
    {
        if (startType is not (3 or 4))
        {
            throw new ArgumentOutOfRangeException(nameof(startType));
        }
        if (!Native.ChangeServiceConfigW(service, uint.MaxValue, startType, uint.MaxValue,
            null, null, 0, null, null, null, null))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Cannot change the fixed broker repair start gate.");
        }
    }
    internal static void RequireConfiguration(SafeHandle service, string brokerTarget)
    {
        _ = Native.QueryServiceConfigW(service, 0, 0, out uint needed);
        if (Marshal.GetLastWin32Error() != 122 || needed == 0 || needed > 65536)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Cannot read the enrolled broker service configuration.");
        }
        nint buffer = Marshal.AllocHGlobal(checked((int)needed));
        try
        {
            if (!Native.QueryServiceConfigW(service, buffer, needed, out _))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error());
            }
            Configuration config = Marshal.PtrToStructure<Configuration>(buffer);
            RequireFixedConfiguration(config.ServiceType, config.StartType,
                Marshal.PtrToStringUni(config.BinaryPath), Marshal.PtrToStringUni(config.Account), brokerTarget);
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }

    internal static void RequireFixedConfiguration(uint serviceType, uint startType,
        string? binaryPath, string? account, string brokerTarget)
    {
        if (serviceType != 0x10 || startType is not (3 or 4)
            || !string.Equals(binaryPath, DevelopmentDeploymentProvisioner.QuoteServiceBinaryPath(brokerTarget), StringComparison.OrdinalIgnoreCase)
            || !string.Equals(account, "LocalSystem", StringComparison.Ordinal))
        {
            throw new InvalidDataException("Repair requires the original fixed LocalSystem demand-start broker service.");
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Configuration
    {
        public uint ServiceType;
        public uint StartType;
        public uint ErrorControl;
        public nint BinaryPath;
        public nint LoadOrderGroup;
        public uint TagId;
        public nint Dependencies;
        public nint Account;
        public nint DisplayName;
    }

    private static class Native
    {
        [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool ChangeServiceConfigW(SafeHandle service, uint serviceType,
            uint startType, uint errorControl, string? binaryPath, string? loadOrderGroup,
            nint tagId, string? dependencies, string? account, string? password, string? displayName);
        [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool QueryServiceConfigW(SafeHandle service, nint config, uint size, out uint needed);
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
