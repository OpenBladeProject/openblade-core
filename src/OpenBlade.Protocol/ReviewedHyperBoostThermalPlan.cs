using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace OpenBlade.Protocol;

internal enum HyperBoostGpuSensorPolicy
{
    Required,
    AllowInactive,
}

internal enum HyperBoostValidationWorkloadKind
{
    SustainedNoLoadDwell,
    ExternalReviewedLoad,
}

internal enum HyperBoostWorkloadIdentityPolicy
{
    NotApplicable,
    RuntimeExactExecutable,
}

internal sealed record ReviewedHyperBoostThermalPlan
{
    internal const string TrustedPadAbsentNoLoadSha256 =
        "2E4837EA0E9B81814FC551954031B5C5BF60A8E3CF593EEA9443684B62B09F33";
    internal const string TrustedPadAbsentExternalLoadSha256 =
        "2D1BBA887F0DC952EEABB39BC3E670A2CE3DA338EC05E46646B1E9F64D780086";
    private const string TrustedPadAbsentNoLoadResource =
        "OpenBlade.Protocol.Fixtures." +
        "Rz09_0581_02e0_4_00_PadAbsentHyperBoostNoLoadPlan.json";
    private const string TrustedPadAbsentExternalLoadResource =
        "OpenBlade.Protocol.Fixtures." +
        "Rz09_0581_02e0_4_00_PadAbsentHyperBoostExternalLoadPlan.json";
    // The validation context never reuses a transaction ID. The complete run
    // needs 14 fixed transactions plus five for each sustained sample, including
    // the four IDs reserved for exact Balanced restoration and readback.
    private const long MaximumSampleCount = 48;
    private static readonly Regex Sha256Pattern = new(
        "^[0-9A-F]{64}$",
        RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    public required string PlanId { get; init; }

    public required string ModelNumber { get; init; }

    public required string BiosVersion { get; init; }

    public required int DurationSeconds { get; init; }

    public required int SampleIntervalMilliseconds { get; init; }

    public required double MaximumCpuTemperatureCelsius { get; init; }

    public required double MaximumGpuTemperatureCelsius { get; init; }

    public required HyperBoostGpuSensorPolicy GpuSensorPolicy { get; init; }

    public int? MinimumFan1Rpm { get; init; }

    public int? MinimumFan2Rpm { get; init; }

    public required HyperBoostValidationWorkloadKind WorkloadKind { get; init; }

    public required HyperBoostWorkloadIdentityPolicy WorkloadIdentityPolicy
    {
        get;
        init;
    }

    public string? WorkloadName { get; init; }

    public string? WorkloadSha256 { get; init; }

    [JsonIgnore]
    internal string SourceSha256 { get; private init; } = string.Empty;

    [JsonIgnore]
    internal TimeSpan Duration => TimeSpan.FromSeconds(DurationSeconds);

    [JsonIgnore]
    internal TimeSpan SampleInterval =>
        TimeSpan.FromMilliseconds(SampleIntervalMilliseconds);

    internal static ReviewedHyperBoostThermalPlan Parse(
        ReadOnlySpan<byte> utf8Json,
        string expectedSha256)
    {
        if (!IsSha256ForReviewedPlan(expectedSha256))
        {
            throw new UnvalidatedCommandException(
                "The reviewed HyperBoost thermal-plan hash is malformed.");
        }

        string actualSha256 = Convert.ToHexString(SHA256.HashData(utf8Json));
        if (!actualSha256.Equals(expectedSha256, StringComparison.Ordinal))
        {
            throw new UnvalidatedCommandException(
                "The reviewed HyperBoost thermal-plan hash does not match.");
        }

        ReviewedHyperBoostThermalPlan? plan;
        try
        {
            plan = JsonSerializer.Deserialize<ReviewedHyperBoostThermalPlan>(
                utf8Json,
                JsonOptions);
        }
        catch (JsonException)
        {
            throw new UnvalidatedCommandException(
                "The reviewed HyperBoost thermal plan is malformed.");
        }

        if (plan is null || !plan.IsWellFormed())
        {
            throw new UnvalidatedCommandException(
                "The reviewed HyperBoost thermal plan is incomplete or unsafe to execute.");
        }

        return plan with { SourceSha256 = actualSha256 };
    }

    internal static ReviewedHyperBoostThermalPlan LoadTrustedPadAbsentNoLoad()
    {
        using Stream? stream = Assembly.GetExecutingAssembly()
            .GetManifestResourceStream(TrustedPadAbsentNoLoadResource);
        if (stream is null)
        {
            throw new UnvalidatedCommandException(
                "The reviewed HyperBoost thermal plan is unavailable.");
        }

        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        return Parse(buffer.ToArray(), TrustedPadAbsentNoLoadSha256);
    }

    internal static ReviewedHyperBoostThermalPlan LoadTrustedPadAbsentExternalLoad()
    {
        using Stream? stream = Assembly.GetExecutingAssembly()
            .GetManifestResourceStream(TrustedPadAbsentExternalLoadResource);
        if (stream is null)
        {
            throw new UnvalidatedCommandException(
                "The reviewed external HyperBoost workload plan is unavailable.");
        }

        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        return Parse(buffer.ToArray(), TrustedPadAbsentExternalLoadSha256);
    }

    internal ReviewedHyperBoostThermalPlan BindRuntimeExactExecutable(
        HyperBoostExternalWorkloadIdentity identity)
    {
        if (WorkloadKind != HyperBoostValidationWorkloadKind.ExternalReviewedLoad
            || WorkloadIdentityPolicy
                != HyperBoostWorkloadIdentityPolicy.RuntimeExactExecutable
            || WorkloadName is not null
            || WorkloadSha256 is not null
            || !identity.IsWellFormed)
        {
            throw new UnvalidatedCommandException(
                "The reviewed external-load plan cannot bind this workload identity.");
        }

        return this with
        {
            WorkloadName = identity.ExecutableName,
            WorkloadSha256 = identity.ExecutableSha256,
        };
    }

    [JsonIgnore]
    internal bool HasBoundRuntimeExactExecutable =>
        WorkloadKind == HyperBoostValidationWorkloadKind.ExternalReviewedLoad
        && WorkloadIdentityPolicy
            == HyperBoostWorkloadIdentityPolicy.RuntimeExactExecutable
        && WorkloadName is { Length: > 0 and <= 128 }
        && Path.GetFileName(WorkloadName) == WorkloadName
        && WorkloadName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
        && IsSha256ForReviewedPlan(WorkloadSha256);

    private bool IsWellFormed()
    {
        return !string.IsNullOrWhiteSpace(PlanId)
            && PlanId.Length <= 128
            && ModelNumber == "RZ09-0581"
            && BiosVersion == "4.00"
            && DurationSeconds > 0
            && SampleIntervalMilliseconds > 0
            && SampleInterval <= Duration
            && Duration.Ticks % SampleInterval.Ticks == 0
            && Duration.Ticks / SampleInterval.Ticks <= MaximumSampleCount
            && double.IsFinite(MaximumCpuTemperatureCelsius)
            && MaximumCpuTemperatureCelsius is > 0 and <= 115
            && double.IsFinite(MaximumGpuTemperatureCelsius)
            && MaximumGpuTemperatureCelsius is > 0 and <= 125
            && Enum.IsDefined(GpuSensorPolicy)
            && MinimumFan1Rpm is null or >= 0
            && MinimumFan2Rpm is null or >= 0
            && WorkloadContractIsWellFormed();
    }

    private bool WorkloadContractIsWellFormed() => WorkloadKind switch
    {
        HyperBoostValidationWorkloadKind.SustainedNoLoadDwell =>
            WorkloadIdentityPolicy == HyperBoostWorkloadIdentityPolicy.NotApplicable
            && string.IsNullOrWhiteSpace(WorkloadName)
            && WorkloadSha256 is null,
        HyperBoostValidationWorkloadKind.ExternalReviewedLoad =>
            WorkloadIdentityPolicy
                == HyperBoostWorkloadIdentityPolicy.RuntimeExactExecutable
            && (WorkloadName is null && WorkloadSha256 is null
                || HasBoundRuntimeExactExecutable),
        _ => false,
    };

    internal static bool IsSha256ForReviewedPlan(string? value) =>
        value is not null && Sha256Pattern.IsMatch(value);
}
