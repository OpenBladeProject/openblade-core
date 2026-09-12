using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using OpenBlade.Deployment;

namespace OpenBlade.DeploymentBroker;

public sealed record BrokerTrustConfiguration(
    string PolicyId,
    OpenBladeReleaseTrustPolicy TrustPolicy);

public interface IBrokerTrustPolicyProvider
{
    BrokerTrustConfiguration GetConfiguration();
}

public sealed class FixedBrokerTrustPolicyProvider(BrokerTrustConfiguration configuration)
    : IBrokerTrustPolicyProvider
{
    public BrokerTrustConfiguration GetConfiguration() => configuration;
}

public static class BrokerTrustPolicyValidator
{
    public static void Validate(BrokerTrustConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        if (string.IsNullOrWhiteSpace(configuration.PolicyId))
        {
            throw new InvalidDataException("The broker release policy ID is unavailable.");
        }

        try
        {
            _ = OpenBladeSignedReleaseVerifier.Verify(
                "{}"u8,
                new byte[OpenBladeSignedReleaseVerifier.P1363SignatureBytes],
                configuration.TrustPolicy);
        }
        catch (OpenBladeReleaseVerificationException exception) when (
            exception.Failure is OpenBladeReleaseVerificationFailure.ManifestInvalid)
        {
            return;
        }

        throw new InvalidDataException("The broker trust policy did not fail at the expected manifest gate.");
    }
}

public interface IBrokerAcceptedSequenceStore
{
    Task ConsumeAsync(long sequence, CancellationToken cancellationToken = default);
}

public sealed class BrokerAcceptedSequenceStore(BrokerPaths paths) : IBrokerAcceptedSequenceStore
{
    // Canonical writes contain at most 19 ASCII digits. Leave room for BOM-based
    // encodings accepted by the existing reader without permitting unbounded text.
    internal const int MaximumSequenceBytes = 128;

    public async Task ConsumeAsync(long sequence, CancellationToken cancellationToken = default)
    {
        if (sequence <= 0)
        {
            throw new InvalidDataException("The signed local development sequence is invalid.");
        }

        paths.EnsureDirectory(paths.RootDirectory);
        long current = 0;
        if (File.Exists(paths.AcceptedSequencePath))
        {
            ProtectedBrokerStorage.RejectReparsePoint(paths.AcceptedSequencePath);
            paths.VerifyFile(paths.AcceptedSequencePath);
            byte[] sequenceBytes = await BrokerStateFile.ReadAsync(
                paths.AcceptedSequencePath, MaximumSequenceBytes, cancellationToken)
                .ConfigureAwait(false);
            using StreamReader reader = new(new MemoryStream(sequenceBytes));
            string text = await reader.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
            if (!long.TryParse(
                    text,
                    System.Globalization.NumberStyles.None,
                    System.Globalization.CultureInfo.InvariantCulture,
                    out current)
                || current <= 0)
            {
                throw new InvalidDataException("The protected accepted sequence is malformed.");
            }
        }

        if (sequence <= current)
        {
            throw new InvalidOperationException(
                "The signed local development sequence must strictly increase.");
        }

        string temporary = paths.AcceptedSequencePath
            + "."
            + Guid.NewGuid().ToString("N")
            + ".tmp";
        try
        {
            byte[] bytes = Encoding.ASCII.GetBytes(
                sequence.ToString(System.Globalization.CultureInfo.InvariantCulture));
            await using (FileStream stream = new(
                temporary,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 32,
                FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await stream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                stream.Flush(flushToDisk: true);
            }

            paths.ProtectFile(temporary);
            if (File.Exists(paths.AcceptedSequencePath))
            {
                File.Replace(temporary, paths.AcceptedSequencePath, destinationBackupFileName: null);
            }
            else
            {
                File.Move(temporary, paths.AcceptedSequencePath, overwrite: false);
            }

            paths.ProtectFile(paths.AcceptedSequencePath);
            paths.VerifyFile(paths.AcceptedSequencePath);
        }
        finally
        {
            if (File.Exists(temporary))
            {
                File.Delete(temporary);
            }
        }
    }
}

public static class BrokerInstallerProcessPlan
{
    private static readonly string[] BlockedEnvironmentPrefixes =
    [
        "COMPLUS_",
        "COREHOST_",
        "COR_",
        "DOTNET_",
        "OPENBLADE_",
        "WIX",
    ];

    private static readonly string[] FixedArguments =
    [
        "/install",
        "/quiet",
        "/norestart",
    ];

    public static ProcessStartInfo Create(string protectedStagingDirectory)
    {
        string staging = Path.GetFullPath(protectedStagingDirectory);
        ProtectedBrokerStorage.RejectReparsePoint(staging);
        string installer = Path.Combine(staging, OpenBladeReleaseManifestCodec.InstallerFileName);
        if (!File.Exists(installer))
        {
            throw new FileNotFoundException("The fixed staged OpenBlade installer is unavailable.", installer);
        }

        ProtectedBrokerStorage.RejectReparsePoint(installer);
        ProcessStartInfo startInfo = new()
        {
            FileName = installer,
            WorkingDirectory = staging,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardError = false,
            RedirectStandardOutput = false,
        };
        foreach (string argument in FixedArguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        string windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        if (string.IsNullOrWhiteSpace(windows))
        {
            throw new InvalidOperationException("The Windows directory is unavailable.");
        }

        foreach (string name in startInfo.Environment.Keys
            .Where(IsBlockedEnvironmentVariable)
            .ToArray())
        {
            startInfo.Environment.Remove(name);
        }

        string system = Path.Combine(windows, "System32");
        startInfo.Environment["SystemRoot"] = windows;
        startInfo.Environment["WINDIR"] = windows;
        startInfo.Environment["ComSpec"] = Path.Combine(system, "cmd.exe");
        startInfo.Environment["PATH"] = $"{system};{windows}";
        startInfo.Environment["TEMP"] = staging;
        startInfo.Environment["TMP"] = staging;
        return startInfo;
    }

    internal static bool IsBlockedEnvironmentVariable(string name) =>
        string.Equals(name, "__COMPAT_LAYER", StringComparison.OrdinalIgnoreCase)
        || BlockedEnvironmentPrefixes.Any(prefix =>
            name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
}

public enum BrokerDeploymentDisposition
{
    Succeeded,
    RolledBack,
    Indeterminate,
}

public enum BrokerTransitionKind
{
    Accepted,
    ReleaseVerified,
    InstallerVerified,
    InstallerStarted,
    InstallerExited,
    HealthVerified,
    RollbackVerified,
    Ambiguous,
}

public sealed record BrokerTransition(
    int SchemaVersion,
    Guid RequestId,
    BrokerTransitionKind Transition,
    DateTimeOffset RecordedAt,
    string Detail);

public static class BrokerTransitionCodec
{
    public const int MaximumLineBytes = 512;

    public static byte[] SerializeCanonical(BrokerTransition transition)
    {
        Validate(transition);
        using MemoryStream stream = new();
        using (Utf8JsonWriter writer = new(stream))
        {
            writer.WriteStartObject();
            writer.WriteNumber("schemaVersion", transition.SchemaVersion);
            writer.WriteString("requestId", transition.RequestId.ToString("D"));
            writer.WriteString("transition", transition.Transition.ToString());
            writer.WriteString(
                "recordedAt",
                transition.RecordedAt.UtcDateTime.ToString(
                    "yyyy-MM-dd'T'HH:mm:ss.fffffff'Z'",
                    System.Globalization.CultureInfo.InvariantCulture));
            writer.WriteString("detail", transition.Detail);
            writer.WriteEndObject();
        }

        byte[] bytes = stream.ToArray();
        if (bytes.Length > MaximumLineBytes)
        {
            throw new InvalidDataException("The broker transition line exceeds its fixed size limit.");
        }

        return bytes;
    }

    public static BrokerTransition ParseCanonical(ReadOnlySpan<byte> bytes)
    {
        if (bytes.IsEmpty || bytes.Length > MaximumLineBytes)
        {
            throw new InvalidDataException("The broker transition line has an invalid size.");
        }

        if (bytes.Length >= 3
            && bytes[..3].SequenceEqual(new byte[] { 0xEF, 0xBB, 0xBF }))
        {
            throw new InvalidDataException("The broker transition line must not contain a BOM.");
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
                throw new InvalidDataException("The broker transition line must be an object.");
            }

            string[] required =
            [
                "schemaVersion",
                "requestId",
                "transition",
                "recordedAt",
                "detail",
            ];
            HashSet<string> allowed = new(required, StringComparer.Ordinal);
            HashSet<string> seenIgnoreCase = new(StringComparer.OrdinalIgnoreCase);
            Dictionary<string, JsonElement> values = new(StringComparer.Ordinal);
            foreach (JsonProperty property in document.RootElement.EnumerateObject())
            {
                if (!allowed.Contains(property.Name)
                    || !seenIgnoreCase.Add(property.Name)
                    || !values.TryAdd(property.Name, property.Value))
                {
                    throw new InvalidDataException(
                        "The broker transition has an unknown or duplicate property.");
                }
            }

            if (values.Count != required.Length
                || required.Any(name => !values.ContainsKey(name))
                || !values["schemaVersion"].TryGetInt32(out int schemaVersion)
                || values["requestId"].ValueKind != JsonValueKind.String
                || !Guid.TryParseExact(values["requestId"].GetString(), "D", out Guid requestId)
                || values["transition"].ValueKind != JsonValueKind.String
                || !Enum.TryParse(
                    values["transition"].GetString(),
                    ignoreCase: false,
                    out BrokerTransitionKind transitionKind)
                || values["recordedAt"].ValueKind != JsonValueKind.String
                || !DateTimeOffset.TryParseExact(
                    values["recordedAt"].GetString(),
                    "yyyy-MM-dd'T'HH:mm:ss.fffffff'Z'",
                    System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.AssumeUniversal
                        | System.Globalization.DateTimeStyles.AdjustToUniversal,
                    out DateTimeOffset recordedAt)
                || values["detail"].ValueKind != JsonValueKind.String)
            {
                throw new InvalidDataException("The broker transition fields are malformed.");
            }

            BrokerTransition transition = new(
                schemaVersion,
                requestId,
                transitionKind,
                recordedAt,
                values["detail"].GetString()!);
            Validate(transition);
            if (!bytes.SequenceEqual(SerializeCanonical(transition)))
            {
                throw new InvalidDataException("The broker transition line is not canonical JSON.");
            }

            return transition;
        }
        catch (InvalidDataException)
        {
            throw;
        }
        catch (Exception exception) when (exception is
            JsonException or InvalidOperationException or FormatException)
        {
            throw new InvalidDataException("The broker transition line is malformed.", exception);
        }
    }

    private static void Validate(BrokerTransition transition)
    {
        ArgumentNullException.ThrowIfNull(transition);
        if (transition.SchemaVersion != 1
            || transition.RequestId == Guid.Empty
            || !Enum.IsDefined(transition.Transition)
            || transition.RecordedAt.Offset != TimeSpan.Zero
            || string.IsNullOrWhiteSpace(transition.Detail)
            || transition.Detail.Length > 128
            || transition.Detail.Any(character => char.IsControl(character)))
        {
            throw new InvalidDataException("The broker transition is invalid.");
        }
    }
}

public sealed record BrokerTerminalReport(
    int SchemaVersion,
    Guid RequestId,
    Guid InstallationId,
    long Sequence,
    string DeploymentPolicyId,
    BrokerDeploymentDisposition Disposition,
    string ManifestSha256,
    string SigningKeyId,
    string PackageSha256,
    string PreviousVersion,
    string TargetVersion,
    int? InstallerExitCode,
    bool ServiceHealthy,
    DateTimeOffset CompletedAt);

public static class BrokerDeploymentClassifier
{
    public static BrokerDeploymentDisposition Classify(
        bool installerResultKnown,
        int? exitCode,
        bool exactTargetInstalled,
        bool serviceHealthy,
        bool exactPreviousStateRestored)
    {
        if (installerResultKnown
            && exitCode == 0
            && exactTargetInstalled
            && serviceHealthy)
        {
            return BrokerDeploymentDisposition.Succeeded;
        }

        if (installerResultKnown && exactPreviousStateRestored)
        {
            return BrokerDeploymentDisposition.RolledBack;
        }

        return BrokerDeploymentDisposition.Indeterminate;
    }

    public static bool MayRetryAutomatically(BrokerDeploymentDisposition disposition) => false;
}

public sealed class BrokerDurableRecordStore
{
    internal const int MaximumJournalBytes = 1024 * 1024;
    private readonly BrokerPaths paths;
    private readonly Action<string>? beforeTerminalReportPublish;

    public BrokerDurableRecordStore(BrokerPaths paths)
        : this(paths, beforeTerminalReportPublish: null)
    {
    }

    internal BrokerDurableRecordStore(
        BrokerPaths paths,
        Action<string>? beforeTerminalReportPublish)
    {
        this.paths = paths ?? throw new ArgumentNullException(nameof(paths));
        this.beforeTerminalReportPublish = beforeTerminalReportPublish;
    }

    public async Task AppendTransitionAsync(
        BrokerTransition transition,
        CancellationToken cancellationToken = default)
    {
        paths.EnsureDirectory(paths.JournalDirectory);
        string journal = Path.Combine(paths.JournalDirectory, transition.RequestId.ToString("N") + ".jsonl");
        if (File.Exists(journal))
        {
            _ = await ReadTransitionsAsync(journal, cancellationToken).ConfigureAwait(false);
        }

        byte[] line = BrokerTransitionCodec.SerializeCanonical(transition);
        long existingLength = File.Exists(journal) ? new FileInfo(journal).Length : 0;
        if (existingLength + line.Length + 1 > MaximumJournalBytes)
        {
            throw new InvalidDataException("The broker transition journal exceeds its fixed size limit.");
        }

        await using FileStream stream = new(
            journal,
            FileMode.Append,
            FileAccess.Write,
            FileShare.Read,
            bufferSize: 4096,
            FileOptions.Asynchronous | FileOptions.WriteThrough);
        await stream.WriteAsync(line, cancellationToken).ConfigureAwait(false);
        await stream.WriteAsync("\n"u8.ToArray(), cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
        stream.Flush(flushToDisk: true);
        paths.ProtectFile(journal);
        paths.VerifyFile(journal);
    }

    public async Task ValidateExistingJournalsAsync(
        CancellationToken cancellationToken = default)
    {
        if (!Directory.Exists(paths.JournalDirectory))
        {
            return;
        }

        ProtectedBrokerStorage.RejectReparsePoint(paths.JournalDirectory);
        paths.VerifyDirectory(paths.JournalDirectory);
        foreach (string journal in Directory.EnumerateFiles(
            paths.JournalDirectory,
            "*.jsonl",
            SearchOption.TopDirectoryOnly))
        {
            cancellationToken.ThrowIfCancellationRequested();
            string name = Path.GetFileNameWithoutExtension(journal);
            if (name.Length != 32 || !Guid.TryParseExact(name, "N", out _))
            {
                throw new InvalidDataException("A broker transition journal has an unauthorized filename.");
            }

            _ = await ReadTransitionsAsync(journal, cancellationToken).ConfigureAwait(false);
        }
    }

    internal async Task<IReadOnlyList<BrokerTransition>> ReadTransitionsAsync(
        string journal,
        CancellationToken cancellationToken = default)
    {
        ProtectedBrokerStorage.RejectReparsePoint(journal);
        paths.VerifyFile(journal);
        byte[] bytes = await BrokerStateFile.ReadAsync(journal, MaximumJournalBytes, cancellationToken)
            .ConfigureAwait(false);
        if (bytes.Length == 0 || bytes[^1] != (byte)'\n')
        {
            throw new InvalidDataException("The broker transition journal has a torn final line.");
        }

        List<BrokerTransition> transitions = [];
        int start = 0;
        for (int index = 0; index < bytes.Length; index++)
        {
            if (bytes[index] != (byte)'\n')
            {
                continue;
            }

            int length = index - start;
            if (length <= 0 || (length > 0 && bytes[index - 1] == (byte)'\r'))
            {
                throw new InvalidDataException("The broker transition journal line framing is invalid.");
            }

            transitions.Add(BrokerTransitionCodec.ParseCanonical(
                bytes.AsSpan(start, length)));
            start = index + 1;
        }

        return transitions;
    }

    public async Task CreateTerminalReportOnceAsync(
        BrokerTerminalReport report,
        CancellationToken cancellationToken = default)
    {
        ValidateReport(report);
        paths.EnsureDirectory(paths.ReportDirectory);
        paths.VerifyDirectory(paths.ReportDirectory);
        string reportPath = Path.Combine(paths.ReportDirectory, report.RequestId.ToString("N") + ".json");
        if (File.Exists(reportPath))
        {
            ProtectedBrokerStorage.RejectReparsePoint(reportPath);
            paths.VerifyFile(reportPath);
            throw new IOException("A terminal report already exists for this request.");
        }

        string temporaryPath = Path.Combine(
            paths.ReportDirectory,
            report.RequestId.ToString("N")
                + "."
                + Guid.NewGuid().ToString("N")
                + ".tmp");
        byte[] bytes = SerializeCanonicalReport(report);
        try
        {
            await using (FileStream stream = new(
                temporaryPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.Read,
                bufferSize: 4096,
                FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await stream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                stream.Flush(flushToDisk: true);
            }

            paths.ProtectFile(temporaryPath);
            ProtectedBrokerStorage.RejectReparsePoint(temporaryPath);
            paths.VerifyFile(temporaryPath);
            beforeTerminalReportPublish?.Invoke(temporaryPath);
            File.Move(temporaryPath, reportPath, overwrite: false);
            ProtectedBrokerStorage.RejectReparsePoint(reportPath);
            paths.VerifyFile(reportPath);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }

    internal async Task<bool> MatchesExistingTerminalReportAsync(
        BrokerTerminalReport report,
        CancellationToken cancellationToken = default)
    {
        ValidateReport(report);
        string reportPath = Path.Combine(
            paths.ReportDirectory,
            report.RequestId.ToString("N") + ".json");
        if (!File.Exists(reportPath))
        {
            return false;
        }

        ProtectedBrokerStorage.RejectReparsePoint(paths.ReportDirectory);
        ProtectedBrokerStorage.RejectReparsePoint(reportPath);
        paths.VerifyDirectory(paths.ReportDirectory);
        paths.VerifyFile(reportPath);
        try
        {
            // Only completedAt may differ, and canonical UTC timestamps have fixed width.
            // A larger record cannot match, including a valid conflicting report.
            byte[] existing = await BrokerStateFile.ReadAsync(
                reportPath, SerializeCanonicalReport(report).Length, cancellationToken)
                .ConfigureAwait(false);
            using JsonDocument document = JsonDocument.Parse(existing);
            if (document.RootElement.ValueKind != JsonValueKind.Object
                || !document.RootElement.TryGetProperty("completedAt", out JsonElement completedAt)
                || completedAt.ValueKind != JsonValueKind.String
                || !DateTimeOffset.TryParseExact(
                    completedAt.GetString(),
                    "yyyy-MM-dd'T'HH:mm:ss.fffffff'Z'",
                    System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.AssumeUniversal
                        | System.Globalization.DateTimeStyles.AdjustToUniversal,
                    out DateTimeOffset recordedAt))
            {
                return false;
            }

            return existing.AsSpan().SequenceEqual(
                SerializeCanonicalReport(report with { CompletedAt = recordedAt }));
        }
        catch (JsonException)
        {
            return false;
        }
        catch (InvalidDataException)
        {
            return false;
        }
    }

    public static string ComputeSha256(ReadOnlySpan<byte> bytes) =>
        Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    private static byte[] SerializeCanonicalReport(BrokerTerminalReport report)
    {
        using MemoryStream stream = new();
        using (Utf8JsonWriter writer = new(stream))
        {
            writer.WriteStartObject();
            writer.WriteNumber("schemaVersion", report.SchemaVersion);
            writer.WriteString("requestId", report.RequestId.ToString("D"));
            writer.WriteString("installationId", report.InstallationId.ToString("D"));
            writer.WriteNumber("sequence", report.Sequence);
            writer.WriteString("deploymentPolicyId", report.DeploymentPolicyId);
            writer.WriteString("disposition", report.Disposition.ToString());
            writer.WriteString("manifestSha256", report.ManifestSha256);
            writer.WriteString("signingKeyId", report.SigningKeyId);
            writer.WriteString("packageSha256", report.PackageSha256);
            writer.WriteString("previousVersion", report.PreviousVersion);
            writer.WriteString("targetVersion", report.TargetVersion);
            if (report.InstallerExitCode.HasValue)
            {
                writer.WriteNumber("installerExitCode", report.InstallerExitCode.Value);
            }
            else
            {
                writer.WriteNull("installerExitCode");
            }

            writer.WriteBoolean("serviceHealthy", report.ServiceHealthy);
            writer.WriteString(
                "completedAt",
                report.CompletedAt.UtcDateTime.ToString(
                    "yyyy-MM-dd'T'HH:mm:ss.fffffff'Z'",
                    System.Globalization.CultureInfo.InvariantCulture));
            writer.WriteEndObject();
        }

        return stream.ToArray();
    }

    private static void ValidateReport(BrokerTerminalReport report)
    {
        ArgumentNullException.ThrowIfNull(report);
        if (report.SchemaVersion != 1
            || report.RequestId == Guid.Empty
            || report.InstallationId == Guid.Empty
            || report.Sequence <= 0
            || report.CompletedAt.Offset != TimeSpan.Zero
            || string.IsNullOrWhiteSpace(report.DeploymentPolicyId)
            || !IsLowerSha256(report.ManifestSha256)
            || !IsLowerSha256(report.SigningKeyId)
            || !IsLowerSha256(report.PackageSha256))
        {
            throw new InvalidDataException("The broker terminal report is invalid.");
        }
    }

    private static bool IsLowerSha256(string value) =>
        value.Length == 64
        && value.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f');
}

public static class BrokerRelaunchPolicy
{
    public static bool MayRelaunch(
        BrokerTerminalReport report,
        Guid expectedRequestId,
        Guid expectedInstallationId,
        string expectedPackageHash,
        bool clientIsNormalIntegrity,
        bool installedPayloadVerified,
        bool brokerResponseConfirmedSuccess) =>
        report.RequestId == expectedRequestId
        && report.InstallationId == expectedInstallationId
        && report.Disposition == BrokerDeploymentDisposition.Succeeded
        && report.InstallerExitCode == 0
        && report.ServiceHealthy
        && string.Equals(report.PackageSha256, expectedPackageHash, StringComparison.Ordinal)
        && clientIsNormalIntegrity
        && installedPayloadVerified
        && brokerResponseConfirmedSuccess;
}
