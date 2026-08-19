using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using OpenBlade.Contracts;

namespace OpenBlade.Protocol;

internal sealed record ReviewedHyperBoostPowerSourceBoundaryPlan
{
    internal const string TrustedSha256 =
        "90C5CC2D06EB8C2CD47524E168550F3A75B6E55F5398A347A134BE67E1903DD1";
    private const string TrustedResource =
        "OpenBlade.Protocol.Fixtures." +
        "Rz09_0581_02e0_4_00_HyperBoostPowerSourceBoundaryPlan.json";
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

    public required IReadOnlyList<AdapterPowerState> InitialAdapterStates { get; init; }

    public required IReadOnlyList<AdapterPowerState> DestinationAdapterStates { get; init; }

    public required PerformancePreset BaselineMode { get; init; }

    public required FanMode BaselineFanMode { get; init; }

    public required int ObservationDurationSeconds { get; init; }

    public required int SampleIntervalMilliseconds { get; init; }

    public required int RequiredConsecutiveAutomaticSamples { get; init; }

    public required bool RequirePadAbsenceBeforeApply { get; init; }

    public required bool RequirePadAbsenceAfterTransition { get; init; }

    public required bool RequirePadAbsenceAfterReconnect { get; init; }

    public required bool ForbidOpenBladeDisableDuringObservation { get; init; }

    public required bool RequireFullPowerReconnectBeforeCleanup { get; init; }

    public required bool IndeterminateRetryForbidden { get; init; }

    [JsonIgnore]
    internal string SourceSha256 { get; private init; } = string.Empty;

    [JsonIgnore]
    internal TimeSpan ObservationDuration =>
        TimeSpan.FromSeconds(ObservationDurationSeconds);

    [JsonIgnore]
    internal TimeSpan SampleInterval =>
        TimeSpan.FromMilliseconds(SampleIntervalMilliseconds);

    internal static ReviewedHyperBoostPowerSourceBoundaryPlan LoadTrusted()
    {
        using Stream? stream = Assembly.GetExecutingAssembly()
            .GetManifestResourceStream(TrustedResource);
        if (stream is null)
        {
            throw new UnvalidatedCommandException(
                "The reviewed HyperBoost power-source boundary plan is unavailable.");
        }

        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        return Parse(buffer.ToArray(), TrustedSha256);
    }

    internal static ReviewedHyperBoostPowerSourceBoundaryPlan Parse(
        ReadOnlySpan<byte> utf8Json,
        string expectedSha256)
    {
        if (!ReviewedHyperBoostThermalPlan.IsSha256ForReviewedPlan(expectedSha256))
        {
            throw new UnvalidatedCommandException(
                "The reviewed HyperBoost power-source boundary plan hash is malformed.");
        }

        string actualSha256 = Convert.ToHexString(SHA256.HashData(utf8Json));
        if (!actualSha256.Equals(expectedSha256, StringComparison.Ordinal))
        {
            throw new UnvalidatedCommandException(
                "The reviewed HyperBoost power-source boundary plan hash does not match.");
        }

        ReviewedHyperBoostPowerSourceBoundaryPlan? plan;
        try
        {
            plan = JsonSerializer.Deserialize<ReviewedHyperBoostPowerSourceBoundaryPlan>(
                utf8Json,
                JsonOptions);
        }
        catch (JsonException)
        {
            throw new UnvalidatedCommandException(
                "The reviewed HyperBoost power-source boundary plan is malformed.");
        }

        if (plan is not
            {
                PlanId: "rz09-0581-bios-4.00-hyperboost-power-source-boundary-v1",
                ModelNumber: "RZ09-0581",
                BiosVersion: "4.00",
                InitialAdapterStates:
                    [AdapterPowerState.FullPower280W, AdapterPowerState.FullPower330W],
                DestinationAdapterStates:
                    [AdapterPowerState.UsbCPower100W, AdapterPowerState.Battery],
                BaselineMode: PerformancePreset.Balanced,
                BaselineFanMode: FanMode.Automatic,
                ObservationDurationSeconds: 10,
                SampleIntervalMilliseconds: 500,
                RequiredConsecutiveAutomaticSamples: 3,
                RequirePadAbsenceBeforeApply: true,
                RequirePadAbsenceAfterTransition: true,
                RequirePadAbsenceAfterReconnect: true,
                ForbidOpenBladeDisableDuringObservation: true,
                RequireFullPowerReconnectBeforeCleanup: true,
                IndeterminateRetryForbidden: true,
            })
        {
            throw new UnvalidatedCommandException(
                "The reviewed HyperBoost power-source boundary plan is incomplete or unsafe to execute.");
        }

        return plan with { SourceSha256 = actualSha256 };
    }
}
