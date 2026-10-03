using System.Text.Json;
using System.Text.RegularExpressions;
using OpenBlade.Deployment;

namespace OpenBlade.DeploymentBroker;

public sealed record BrokerInFlightDeployment(
    int SchemaVersion,
    Guid RequestId,
    int BrokerProcessId,
    int? InstallerProcessId,
    int? InstallerExitCode,
    string? PreviousVersion,
    string? PreviousProductVersion,
    string? PreviousPayloadSha256,
    string? InstallerSha256,
    string? TargetVersion,
    string? TargetSourceRevision,
    string? ManifestSha256,
    string? SigningKeyId,
    long? TargetSequence,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt)
{
    public string? CpuVoltageServiceVersion { get; init; }
}

public static partial class BrokerInFlightDeploymentCodec
{
    public const int CurrentSchemaVersion = 4;
    public const int MaximumBytes = 2048;

    public static byte[] SerializeCanonical(BrokerInFlightDeployment state)
    {
        Validate(state);
        using MemoryStream stream = new();
        using (Utf8JsonWriter writer = new(stream))
        {
            writer.WriteStartObject();
            writer.WriteNumber("schemaVersion", state.SchemaVersion);
            writer.WriteString("requestId", state.RequestId.ToString("D"));
            writer.WriteNumber("brokerProcessId", state.BrokerProcessId);
            if (state.InstallerProcessId.HasValue)
            {
                writer.WriteNumber("installerProcessId", state.InstallerProcessId.Value);
            }
            else
            {
                writer.WriteNull("installerProcessId");
            }

            if (state.InstallerExitCode.HasValue)
            {
                writer.WriteNumber("installerExitCode", state.InstallerExitCode.Value);
            }
            else
            {
                writer.WriteNull("installerExitCode");
            }

            WriteNullableString(writer, "previousVersion", state.PreviousVersion);
            WriteNullableString(writer, "previousProductVersion", state.PreviousProductVersion);
            WriteNullableString(writer, "previousPayloadSha256", state.PreviousPayloadSha256);
            if (state.InstallerSha256 is null)
            {
                writer.WriteNull("installerSha256");
                writer.WriteNull("targetVersion");
                writer.WriteNull("targetSourceRevision");
                writer.WriteNull("manifestSha256");
                writer.WriteNull("signingKeyId");
                writer.WriteNull("targetSequence");
            }
            else
            {
                writer.WriteString("installerSha256", state.InstallerSha256);
                writer.WriteString("targetVersion", state.TargetVersion);
                writer.WriteString("targetSourceRevision", state.TargetSourceRevision);
                writer.WriteString("manifestSha256", state.ManifestSha256);
                writer.WriteString("signingKeyId", state.SigningKeyId);
                writer.WriteNumber("targetSequence", state.TargetSequence!.Value);
            }

            writer.WriteString("createdAt", FormatTimestamp(state.CreatedAt));
            writer.WriteString("updatedAt", FormatTimestamp(state.UpdatedAt));
            if (state.CpuVoltageServiceVersion is not null)
            { writer.WriteString("cpuVoltageServiceVersion", state.CpuVoltageServiceVersion); }
            writer.WriteEndObject();
        }

        byte[] bytes = stream.ToArray();
        if (bytes.Length > MaximumBytes)
        {
            throw new InvalidDataException("The unresolved deployment state is too large.");
        }

        return bytes;
    }

    public static BrokerInFlightDeployment ParseCanonical(ReadOnlySpan<byte> bytes)
    {
        if (bytes.IsEmpty || bytes.Length > MaximumBytes)
        {
            throw new InvalidDataException("The unresolved deployment state has an invalid size.");
        }

        try
        {
            using JsonDocument document = JsonDocument.Parse(bytes.ToArray());
            string[] required =
            [
                "schemaVersion",
                "requestId",
                "brokerProcessId",
                "installerProcessId",
                "installerExitCode",
                "previousVersion",
                "previousProductVersion",
                "previousPayloadSha256",
                "installerSha256",
                "targetVersion",
                "targetSourceRevision",
                "manifestSha256",
                "signingKeyId",
                "targetSequence",
                "createdAt",
                "updatedAt",
            ];
            HashSet<string> allowed = new(required, StringComparer.Ordinal);
            allowed.Add("cpuVoltageServiceVersion");
            HashSet<string> seen = new(StringComparer.OrdinalIgnoreCase);
            Dictionary<string, JsonElement> values = new(StringComparer.Ordinal);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                throw new InvalidDataException("The unresolved deployment state must be an object.");
            }

            foreach (JsonProperty property in document.RootElement.EnumerateObject())
            {
                if (!allowed.Contains(property.Name)
                    || !seen.Add(property.Name)
                    || !values.TryAdd(property.Name, property.Value))
                {
                    throw new InvalidDataException(
                        "The unresolved deployment state has an unknown or duplicate property.");
                }
            }

            if (values.Count != required.Length + (values.ContainsKey("cpuVoltageServiceVersion") ? 1 : 0)
                || required.Any(name => !values.ContainsKey(name)))
            {
                throw new InvalidDataException(
                    "The unresolved deployment state is missing a required property.");
            }

            BrokerInFlightDeployment state = new(
                ReadInt32(values, "schemaVersion"),
                ReadGuid(values, "requestId"),
                ReadInt32(values, "brokerProcessId"),
                ReadNullableInt32(values, "installerProcessId"),
                ReadNullableInt32(values, "installerExitCode"),
                ReadNullableString(values, "previousVersion"),
                ReadNullableString(values, "previousProductVersion"),
                ReadNullableString(values, "previousPayloadSha256"),
                ReadNullableString(values, "installerSha256"),
                ReadNullableString(values, "targetVersion"),
                ReadNullableString(values, "targetSourceRevision"),
                ReadNullableString(values, "manifestSha256"),
                ReadNullableString(values, "signingKeyId"),
                ReadNullableInt64(values, "targetSequence"),
                ReadTimestamp(values, "createdAt"),
                ReadTimestamp(values, "updatedAt"))
            {
                CpuVoltageServiceVersion = values.ContainsKey("cpuVoltageServiceVersion")
                    ? ReadNullableString(values, "cpuVoltageServiceVersion") : null,
            };
            Validate(state);
            if (!bytes.SequenceEqual(SerializeCanonical(state)))
            {
                throw new InvalidDataException(
                    "The unresolved deployment state is not canonical JSON.");
            }

            return state;
        }
        catch (InvalidDataException)
        {
            throw;
        }
        catch (Exception exception) when (exception is
            JsonException or InvalidOperationException or FormatException)
        {
            throw new InvalidDataException("The unresolved deployment state is malformed.", exception);
        }
    }

    private static void Validate(BrokerInFlightDeployment state)
    {
        ArgumentNullException.ThrowIfNull(state);
        bool previousBound = state.PreviousVersion is not null
            || state.PreviousProductVersion is not null
            || state.PreviousPayloadSha256 is not null;
        bool releaseBound = state.InstallerSha256 is not null
            || state.TargetVersion is not null
            || state.TargetSourceRevision is not null
            || state.ManifestSha256 is not null
            || state.SigningKeyId is not null;
        releaseBound = releaseBound || state.TargetSequence.HasValue;
        if (state.CpuVoltageServiceVersion is not null && (!releaseBound
            || state.TargetSourceRevision is not { Length: 40 }
            || state.CpuVoltageServiceVersion != state.TargetVersion + "+voltage-validation." + state.TargetSourceRevision[..8]))
        { throw new InvalidDataException("The unresolved CPU deployment profile is invalid."); }
        if (state.SchemaVersion != CurrentSchemaVersion
            || state.RequestId == Guid.Empty
            || state.BrokerProcessId <= 0
            || state.InstallerProcessId is <= 0
            || (state.InstallerExitCode.HasValue && !state.InstallerProcessId.HasValue)
            || (previousBound && (state.PreviousVersion is null
                || state.PreviousProductVersion is null
                || state.PreviousPayloadSha256 is null))
            || (releaseBound && (!previousBound
                || state.InstallerSha256 is null
                || state.TargetVersion is null
                || state.TargetSourceRevision is null
                || state.ManifestSha256 is null
                || state.SigningKeyId is null
                || !state.TargetSequence.HasValue))
            || (state.InstallerProcessId.HasValue && !releaseBound)
            || (state.PreviousVersion is not null
                && !IsInstalledVersion(state.PreviousVersion))
            || (state.PreviousProductVersion is not null
                && (state.PreviousProductVersion.Length is < 1 or > 160
                    || state.PreviousProductVersion.Any(char.IsControl)))
            || (state.PreviousPayloadSha256 is not null
                && !LowerSha256Pattern().IsMatch(state.PreviousPayloadSha256))
            || (state.InstallerSha256 is not null
                && !LowerSha256Pattern().IsMatch(state.InstallerSha256))
            || (state.TargetVersion is not null
                && !OpenBladeReleaseManifestCodec.IsCanonicalLocalDevVersion(
                    state.TargetVersion))
            || (state.TargetSourceRevision is not null
                && !SourceRevisionPattern().IsMatch(state.TargetSourceRevision))
            || (state.ManifestSha256 is not null
                && !LowerSha256Pattern().IsMatch(state.ManifestSha256))
            || (state.SigningKeyId is not null
                && !LowerSha256Pattern().IsMatch(state.SigningKeyId))
            || state.TargetSequence is <= 0
            || state.CreatedAt.Offset != TimeSpan.Zero
            || state.UpdatedAt.Offset != TimeSpan.Zero
            || state.UpdatedAt < state.CreatedAt)
        {
            throw new InvalidDataException("The unresolved deployment state fields are invalid.");
        }
    }

    private static bool IsInstalledVersion(string version) =>
        StableVersionPattern().IsMatch(version)
        || OpenBladeReleaseManifestCodec.IsCanonicalLocalDevVersion(version);

    private static int ReadInt32(IReadOnlyDictionary<string, JsonElement> values, string name) =>
        values[name].TryGetInt32(out int result)
            ? result
            : throw new InvalidDataException($"Unresolved state property '{name}' must be an integer.");

    private static int? ReadNullableInt32(
        IReadOnlyDictionary<string, JsonElement> values,
        string name) => values[name].ValueKind == JsonValueKind.Null
        ? null
        : ReadInt32(values, name);

    private static long? ReadNullableInt64(
        IReadOnlyDictionary<string, JsonElement> values,
        string name) => values[name].ValueKind == JsonValueKind.Null
        ? null
        : values[name].TryGetInt64(out long result)
            ? result
            : throw new InvalidDataException(
                $"Unresolved state property '{name}' must be an integer or null.");

    private static string? ReadNullableString(
        IReadOnlyDictionary<string, JsonElement> values,
        string name)
    {
        return values[name].ValueKind switch
        {
            JsonValueKind.Null => null,
            JsonValueKind.String => values[name].GetString(),
            _ => throw new InvalidDataException(
                $"Unresolved state property '{name}' must be a string or null."),
        };
    }

    private static Guid ReadGuid(IReadOnlyDictionary<string, JsonElement> values, string name) =>
        values[name].ValueKind == JsonValueKind.String
        && Guid.TryParseExact(values[name].GetString(), "D", out Guid result)
            ? result
            : throw new InvalidDataException($"Unresolved state property '{name}' must be a canonical GUID.");

    private static DateTimeOffset ReadTimestamp(
        IReadOnlyDictionary<string, JsonElement> values,
        string name) => values[name].ValueKind == JsonValueKind.String
        && DateTimeOffset.TryParseExact(
            values[name].GetString(),
            "yyyy-MM-dd'T'HH:mm:ss.fffffff'Z'",
            System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.AssumeUniversal
                | System.Globalization.DateTimeStyles.AdjustToUniversal,
            out DateTimeOffset result)
            ? result
            : throw new InvalidDataException(
                $"Unresolved state property '{name}' must be a canonical UTC timestamp.");

    private static string FormatTimestamp(DateTimeOffset value) =>
        value.UtcDateTime.ToString(
            "yyyy-MM-dd'T'HH:mm:ss.fffffff'Z'",
            System.Globalization.CultureInfo.InvariantCulture);

    private static void WriteNullableString(
        Utf8JsonWriter writer,
        string propertyName,
        string? value)
    {
        if (value is null)
        {
            writer.WriteNull(propertyName);
        }
        else
        {
            writer.WriteString(propertyName, value);
        }
    }

    [GeneratedRegex("^[0-9a-f]{64}$")]
    private static partial Regex LowerSha256Pattern();

    [GeneratedRegex("^(?:0|[1-9][0-9]*)\\.(?:0|[1-9][0-9]*)\\.(?:0|[1-9][0-9]*)$")]
    private static partial Regex StableVersionPattern();

    [GeneratedRegex("^[0-9a-fA-F]{40}$")]
    private static partial Regex SourceRevisionPattern();
}

public sealed class BrokerInFlightStore
{
    private readonly BrokerPaths paths;
    private readonly Action<BrokerInFlightDeployment>? afterReplacement;
    private readonly Action<BrokerInFlightDeployment>? beforeReplacement;

    public BrokerInFlightStore(BrokerPaths paths)
        : this(paths, afterReplacement: null)
    {
    }

    internal BrokerInFlightStore(
        BrokerPaths paths,
        Action<BrokerInFlightDeployment>? afterReplacement,
        Action<BrokerInFlightDeployment>? beforeReplacement = null)
    {
        this.paths = paths ?? throw new ArgumentNullException(nameof(paths));
        this.afterReplacement = afterReplacement;
        this.beforeReplacement = beforeReplacement;
    }

    public async Task<BrokerInFlightDeployment?> LoadAsync(
        CancellationToken cancellationToken = default)
    {
        if (!File.Exists(paths.InFlightPath))
        {
            return null;
        }

        ProtectedBrokerStorage.RejectReparsePoint(paths.InFlightPath);
        paths.VerifyFile(paths.InFlightPath);
        byte[] bytes = await BrokerStateFile.ReadAsync(
            paths.InFlightPath, BrokerInFlightDeploymentCodec.MaximumBytes, cancellationToken)
            .ConfigureAwait(false);
        return BrokerInFlightDeploymentCodec.ParseCanonical(bytes);
    }

    internal async Task<BrokerInFlightDeployment> CreateOnceAsync(
        Guid requestId,
        DateTimeOffset now,
        CancellationToken cancellationToken = default)
    {
        BrokerInFlightDeployment state = new(
            BrokerInFlightDeploymentCodec.CurrentSchemaVersion,
            requestId,
            Environment.ProcessId,
            InstallerProcessId: null,
            InstallerExitCode: null,
            PreviousVersion: null,
            PreviousProductVersion: null,
            PreviousPayloadSha256: null,
            InstallerSha256: null,
            TargetVersion: null,
            TargetSourceRevision: null,
            ManifestSha256: null,
            SigningKeyId: null,
            TargetSequence: null,
            CreatedAt: now.ToUniversalTime(),
            UpdatedAt: now.ToUniversalTime());
        paths.EnsureDirectory(paths.RootDirectory);
        await PublishCreateOnceAsync(state, cancellationToken).ConfigureAwait(false);
        return state;
    }

    internal void BindPreviousState(
        Guid requestId,
        BrokerInstalledState before,
        DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(before);
        BrokerInFlightDeployment current = LoadRequired(requestId);
        if (current.PreviousVersion is not null || current.InstallerSha256 is not null)
        {
            throw new InvalidDataException(
                "The unresolved deployment already has an installed-state binding.");
        }

        PublishReplacement(current with
        {
            PreviousVersion = before.Version,
            PreviousProductVersion = before.ProductVersion,
            PreviousPayloadSha256 = before.PayloadSha256,
            UpdatedAt = now.ToUniversalTime(),
        });
    }

    internal void BindRelease(
        Guid requestId,
        string installerSha256,
        string targetVersion,
        string targetSourceRevision,
        string manifestSha256,
        string signingKeyId,
        long targetSequence,
        DateTimeOffset now,
        string? cpuVoltageServiceVersion = null)
    {
        BrokerInFlightDeployment current = LoadRequired(requestId);
        if (current.PreviousVersion is null || current.InstallerSha256 is not null)
        {
            throw new InvalidDataException(
                "The unresolved deployment is not ready for a release binding.");
        }

        PublishReplacement(current with
        {
            InstallerSha256 = installerSha256,
            TargetVersion = targetVersion,
            TargetSourceRevision = targetSourceRevision,
            ManifestSha256 = manifestSha256,
            SigningKeyId = signingKeyId,
            TargetSequence = targetSequence,
            CpuVoltageServiceVersion = cpuVoltageServiceVersion,
            UpdatedAt = now.ToUniversalTime(),
        });
    }

    internal void BindInstallerProcess(Guid requestId, int processId, DateTimeOffset now)
    {
        BrokerInFlightDeployment current = LoadRequired(requestId);
        if (current.InstallerSha256 is null || current.InstallerProcessId.HasValue)
        {
            throw new InvalidDataException(
                "The unresolved deployment is not ready for a process binding.");
        }

        PublishReplacement(current with
        {
            InstallerProcessId = processId,
            UpdatedAt = now.ToUniversalTime(),
        });
    }

    internal void BindInstallerResult(Guid requestId, int exitCode, DateTimeOffset now)
    {
        BrokerInFlightDeployment current = LoadRequired(requestId);
        if (!current.InstallerProcessId.HasValue || current.InstallerExitCode.HasValue)
        {
            throw new InvalidDataException(
                "The unresolved deployment is not ready for an installer result binding.");
        }

        PublishReplacement(current with
        {
            InstallerExitCode = exitCode,
            UpdatedAt = now.ToUniversalTime(),
        });
    }

    internal void ClearResolved(Guid requestId)
    {
        _ = LoadRequired(requestId);
        File.Delete(paths.InFlightPath);
    }

    private BrokerInFlightDeployment LoadRequired(Guid requestId)
    {
        BrokerInFlightDeployment state = LoadAsync(CancellationToken.None)
            .GetAwaiter()
            .GetResult()
            ?? throw new InvalidDataException("The unresolved deployment latch is missing.");
        if (state.RequestId != requestId)
        {
            throw new UnauthorizedAccessException(
                "The unresolved deployment belongs to a different request.");
        }

        return state;
    }

    private async Task PublishCreateOnceAsync(
        BrokerInFlightDeployment state,
        CancellationToken cancellationToken)
    {
        string temporary = GetTemporaryPath(state.RequestId);
        try
        {
            await WriteProtectedAsync(temporary, state, cancellationToken).ConfigureAwait(false);
            File.Move(temporary, paths.InFlightPath, overwrite: false);
            paths.VerifyFile(paths.InFlightPath);
        }
        finally
        {
            if (File.Exists(temporary))
            {
                File.Delete(temporary);
            }
        }
    }

    private void PublishReplacement(BrokerInFlightDeployment state)
    {
        string temporary = GetTemporaryPath(state.RequestId);
        try
        {
            WriteProtectedAsync(temporary, state, CancellationToken.None)
                .GetAwaiter()
                .GetResult();
            beforeReplacement?.Invoke(state);
            File.Replace(temporary, paths.InFlightPath, destinationBackupFileName: null);
            afterReplacement?.Invoke(state);
            paths.VerifyFile(paths.InFlightPath);
        }
        finally
        {
            if (File.Exists(temporary))
            {
                File.Delete(temporary);
            }
        }
    }

    private async Task WriteProtectedAsync(
        string path,
        BrokerInFlightDeployment state,
        CancellationToken cancellationToken)
    {
        byte[] bytes = BrokerInFlightDeploymentCodec.SerializeCanonical(state);
        await using (FileStream stream = new(
            path,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.Read,
            bufferSize: 1024,
            FileOptions.Asynchronous | FileOptions.WriteThrough))
        {
            await stream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
            await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            stream.Flush(flushToDisk: true);
        }

        paths.ProtectFile(path);
        ProtectedBrokerStorage.RejectReparsePoint(path);
        paths.VerifyFile(path);
    }

    private string GetTemporaryPath(Guid requestId) => Path.Combine(
        paths.RootDirectory,
        "unresolved-deployment."
            + requestId.ToString("N")
            + "."
            + Guid.NewGuid().ToString("N")
            + ".tmp");
}
