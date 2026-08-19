using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using OpenBlade.Contracts;

namespace OpenBlade.Protocol;

internal sealed record ReviewedHyperBoostAdapterTransitionPlan
{
    internal const string TrustedSha256 =
        "95E2333A19118480E6D8193933B7548587BF5C40D151C99BFE81E06D421C7537";
    private const string TrustedResource =
        "OpenBlade.Protocol.Fixtures." +
        "Rz09_0581_02e0_4_00_PadAbsentHyperBoostAdapterTransitionPlan.json";
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

    public required AdapterPowerState DestinationAdapterState { get; init; }

    public required PerformancePreset BaselineMode { get; init; }

    public required FanMode BaselineFanMode { get; init; }

    public required bool RequirePadAbsenceBeforeApply { get; init; }

    public required bool RequirePadAbsenceAfterSwap { get; init; }

    public required bool RequirePadAbsenceAfterRestoration { get; init; }

    public required bool RestoreBalancedImmediately { get; init; }

    public required bool IndeterminateRetryForbidden { get; init; }

    [JsonIgnore]
    internal string SourceSha256 { get; private init; } = string.Empty;

    internal static ReviewedHyperBoostAdapterTransitionPlan LoadTrusted()
    {
        using Stream? stream = Assembly.GetExecutingAssembly()
            .GetManifestResourceStream(TrustedResource);
        if (stream is null)
        {
            throw new UnvalidatedCommandException(
                "The reviewed HyperBoost adapter-transition plan is unavailable.");
        }

        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        return Parse(buffer.ToArray(), TrustedSha256);
    }

    internal static ReviewedHyperBoostAdapterTransitionPlan Parse(
        ReadOnlySpan<byte> utf8Json,
        string expectedSha256)
    {
        if (!ReviewedHyperBoostThermalPlan.IsSha256ForReviewedPlan(expectedSha256))
        {
            throw new UnvalidatedCommandException(
                "The reviewed HyperBoost adapter-transition plan hash is malformed.");
        }

        string actualSha256 = Convert.ToHexString(SHA256.HashData(utf8Json));
        if (!actualSha256.Equals(expectedSha256, StringComparison.Ordinal))
        {
            throw new UnvalidatedCommandException(
                "The reviewed HyperBoost adapter-transition plan hash does not match.");
        }

        ReviewedHyperBoostAdapterTransitionPlan? plan;
        try
        {
            plan = JsonSerializer.Deserialize<ReviewedHyperBoostAdapterTransitionPlan>(
                utf8Json,
                JsonOptions);
        }
        catch (JsonException)
        {
            throw new UnvalidatedCommandException(
                "The reviewed HyperBoost adapter-transition plan is malformed.");
        }

        if (plan is not
            {
                PlanId: "rz09-0581-bios-4.00-pad-absent-adapter-transition-v1",
                ModelNumber: "RZ09-0581",
                BiosVersion: "4.00",
                InitialAdapterStates:
                    [AdapterPowerState.FullPower280W, AdapterPowerState.FullPower330W],
                DestinationAdapterState: AdapterPowerState.UsbCPower100W,
                BaselineMode: PerformancePreset.Balanced,
                BaselineFanMode: FanMode.Automatic,
                RequirePadAbsenceBeforeApply: true,
                RequirePadAbsenceAfterSwap: true,
                RequirePadAbsenceAfterRestoration: true,
                RestoreBalancedImmediately: true,
                IndeterminateRetryForbidden: true,
            })
        {
            throw new UnvalidatedCommandException(
                "The reviewed HyperBoost adapter-transition plan is incomplete or unsafe to execute.");
        }

        return plan with { SourceSha256 = actualSha256 };
    }
}
