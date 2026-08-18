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

internal sealed record ReviewedHyperBoostThermalPlan
{
    internal const string TrustedPadAbsentNoLoadSha256 =
        "589A94BCE7050BA43195BBFE5FAC4E8EDF337D481705047C668460ACAB675D2B";
    private const string TrustedPadAbsentNoLoadResource =
        "OpenBlade.Protocol.Fixtures." +
        "Rz09_0581_02e0_4_00_PadAbsentHyperBoostNoLoadPlan.json";
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
        if (!IsSha256(expectedSha256))
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
            // A reviewed external workload needs its own trusted executor and
            // lifecycle contract. The first admitted phase is no-load dwell only.
            && WorkloadKind == HyperBoostValidationWorkloadKind.SustainedNoLoadDwell
            && string.IsNullOrWhiteSpace(WorkloadName)
            && WorkloadSha256 is null;
    }

    private static bool IsSha256(string? value) =>
        value is not null && Sha256Pattern.IsMatch(value);
}
