using OpenBlade.Contracts;

namespace OpenBlade.Protocol;

/// <summary>Upstream evidence permits experimental controls, not a physical-validation claim.</summary>
internal sealed record ExperimentalBladeModel(
    string ModelNumber,
    string ProductName,
    ushort ProductId,
    bool SupportsBatteryProtection,
    string EvidenceSource,
    string? AdapterPowerEvidenceSource = null);

internal static class ExperimentalBladeModels
{
    public const string RazerCtlSource = "https://github.com/tdakhran/razer-ctl/tree/8e12e778d303d4ed01eeb2c1e99ce2e97f1c8917";
    public const string BladeControlSource = "https://github.com/jdsgnrinfo/blade-control/tree/bc48c916d4d6cc47df8c0e32a73ef454dda37cdd";

    public const string Blade18Source = "https://github.com/sqmagellan/razer-ctl/tree/9c66e7055a0e31f5ec25c16185e66fde1f50cf4a";

    internal const string Blade18SynapseSource = "https://apps.razer.com/synapse/products/672/mw/main.57245df0507455a5119e.js";
    internal const string Blade18ObserverSource = "https://github.com/user-attachments/files/33499731/blade-evidence-20261015-1459.zip";

    internal const string MaintainedRazerCtlSource = "https://github.com/sqmagellan/razer-ctl/tree/9c66e7055a0e31f5ec25c16185e66fde1f50cf4a";
    internal const string OpenRazerSource = "https://github.com/openrazer/openrazer/tree/477f4d6d3ab7e9deb6310d5bd3742560c5cbe80d";
    internal const string ExpandedBladeControlSource = "https://github.com/jdsgnrinfo/blade-control/tree/92beedc82e6e378cb71dac276e5820c8ea84247d";

    internal static IReadOnlyList<ExperimentalFeatureEvidence> FeatureEvidenceFor(ExperimentalBladeModel model)
    {
        IReadOnlyList<ExperimentalFeatureEvidence> features = model.ProductId switch
        {
            0x029F => [
                new("PerformanceAndCustom", "UpstreamOwnerTested", [RazerCtlSource, MaintainedRazerCtlSource]),
                new("FixedFansAndMaximumFans", "UpstreamOwnerTested", [RazerCtlSource, MaintainedRazerCtlSource]),
                new("KeyboardAndLogoLighting", "UpstreamOwnerTested", [MaintainedRazerCtlSource]),
                new("CustomFrames", "UpstreamOwnerTestedWithoutRgbReadback", [MaintainedRazerCtlSource, OpenRazerSource]),
                new("UniformAmbientAndAudio", "SourceBackedExtrapolationWithoutRgbReadback", [MaintainedRazerCtlSource, OpenRazerSource]),
                new("ChargeLimits50To100", "UpstreamOwnerTested", [MaintainedRazerCtlSource])],
            0x029D => [
                new("CustomAndFixedFans", "SourceBackedExtrapolation", [RazerCtlSource, MaintainedRazerCtlSource]),
                new("KeyboardLightingAndLogoOnOff", "UpstreamOwnerReported", [OpenRazerSource, "https://github.com/openrazer/openrazer/pull/2237"]),
                new("LogoStaticAndBreathing", "SourceBackedExtrapolation", [RazerCtlSource, MaintainedRazerCtlSource]),
                new("ChargeLimits50To100", "SourceBackedExtrapolation", [RazerCtlSource, MaintainedRazerCtlSource])],
            0x02C5 => [
                new("BatteryPerformanceModes", "UpstreamOwnerReported", [ExpandedBladeControlSource, "https://github.com/jdsgnrinfo/blade-control/issues/2"]),
                new("CustomPerformance", "SourceBackedExtrapolation", [ExpandedBladeControlSource]),
                new("KeyboardLightingAndLogoOnOff", "UpstreamOwnerReported", [OpenRazerSource, "https://github.com/openrazer/openrazer/pull/2539"]),
                new("LogoStaticAndBreathing", "SourceBackedExtrapolation", [ExpandedBladeControlSource, MaintainedRazerCtlSource]),
                new("ChargeLimits50To100", "SourceBackedExtrapolation", [ExpandedBladeControlSource, MaintainedRazerCtlSource])],
            0x0255 => [
                new("GamingAndCustom", "UpstreamOwnerReported", [ExpandedBladeControlSource]),
                new("SingleZoneRgbLighting", "UpstreamOwnerTested", [ExpandedBladeControlSource])],
            0x02A0 => [
                new("CustomCpuBoostGpuHigh", "SynapseSourceAndObservedRequestsWithoutIndependentReadback",
                    [Blade18SynapseSource, Blade18ObserverSource,
                     "https://apps.razer.com/synapse/products/672/mw/rzDevice25.85ce8530211c29f3c304.js",
                     "https://apps.razer.com/synapse/products/672/mw/6737.e50bb836b878190315d7.js"]),
                new("AdditionalLightingEffects", "SynapseSourceObservedOnly", [Blade18SynapseSource]),
                new("FixedFanTargets", "SynapseSourceConflictsWithOwnerReport", [Blade18SynapseSource, "https://github.com/sqmagellan/razer-ctl/issues/1"]),
                new("MaximumFans", "UpstreamOwnerReported", [Blade18Source, "https://github.com/sqmagellan/razer-ctl/issues/1"]),
                new("KeyboardAndLogoLighting", "UpstreamOwnerReported", [Blade18Source, "https://github.com/sqmagellan/razer-ctl/issues/1"]),
                new("UniformStaticRgb", "SourceBackedExtrapolationWithoutRgbReadback", [OpenRazerSource, MaintainedRazerCtlSource]),
                new("CustomAndChargeLimits50To100", "SourceBackedExtrapolation", [Blade18Source])],
            _ => [],
        };
        return model.AdapterPowerEvidenceSource is { } source
            ? [.. features, new("AdapterPowerClassification", "SynapseSourceBacked", [source])]
            : features;
    }

    public static IReadOnlyList<ExperimentalBladeModel> All { get; } = Array.AsReadOnly<ExperimentalBladeModel>(
    [
        new("RZ09-0483", "Blade 16 - RZ09-0483", 0x029F, true, RazerCtlSource,
            "https://apps.razer.com/synapse/products/671/mw/main.2ceb8f5607c0e7a5901a.js"),
        // Published DMI report: https://img.unikoshardware.com/wp-content/uploads/2023/06/RAZER_BLADE_14Report.htm
        new("RZ09-0482", "Blade 14 - RZ09-0482", 0x029D, true, RazerCtlSource,
            "https://apps.razer.com/synapse/products/669/mw/main.8e09720cb94da405eead.js"),
        // Published system model: https://browser.geekbench.com/v6/cpu/compare/14894027?baseline=2340230
        new("RZ09-0530", "Blade 14 - RZ09-0530", 0x02C5, true, BladeControlSource,
            "https://apps.razer.com/synapse/products/709/mw/main.bb0dd1873a58a987c63a.js"),
        // Windows System Model reported by an owner:
        // https://learn.microsoft.com/en-us/answers/questions/3810665/intel-wi-fi-6-ax201-is-not-working
        new("RZ09-0328", "Blade 15 Base Model (Early 2020) - RZ09-0328", 0x0255, false, BladeControlSource,
            "https://apps.razer.com/synapse/products/597/mw/main.a1e24f33c1759c4315e3.js"),
        // Exact-model owner report: https://github.com/sqmagellan/razer-ctl/issues/1
        // Windows product: https://browser.geekbench.com/v6/cpu/6428586
        new("RZ09-0484", "Blade 18 - RZ09-0484", 0x02A0, true, Blade18Source,
            "https://apps.razer.com/synapse/products/672/mw/main.57245df0507455a5119e.js"),
    ]);

    internal static IHostIdentitySource CreateIdentity(
        ExperimentalBladeModel model,
        IRegistryValueSource registry,
        IWindowsPlatformEvidenceSource platform) =>
        new WindowsHostIdentitySource(registry, platform,
            new ExpectedHostIdentity("Razer", model.ProductName, model.ModelNumber,
                Array.Empty<string>(), FirmwareAdmissionScope.ModelScoped));

    internal static ExperimentalBladeModel? FindCurrentHost()
    {
        if (!OperatingSystem.IsWindows())
        {
            return null;
        }
        var registry = new WindowsRegistryValueSource();
        var platform = new RuntimeWindowsPlatformEvidenceSource(registry);
        return All.FirstOrDefault(model =>
            CreateIdentity(model, registry, platform).ReadExactIdentity().Evidence is not null);
    }
}

internal sealed class ExperimentalFeaturesPolicy(ExperimentalBladeModel model)
{
    private readonly object sync = new();
    private readonly LinkedList<ExperimentalControlObservation> observations = new();
    private readonly Dictionary<string, (string Outcome, string? Confirmed, string? Failure)> lastQueries = new(StringComparer.Ordinal);
    private LinkedListNode<ExperimentalControlObservation>? uniformFrameSuccess;
    private long uniformFrameSuccessCount;
    private int recoveryRequired;

    internal bool IsPaused => Volatile.Read(ref recoveryRequired) != 0;

    internal bool WritesAllowed => !IsPaused;

    internal void RequireRecovery() => Volatile.Write(ref recoveryRequired, 1);

    // The caller owns the provider hardware gate and has reconciled fresh readback.
    internal void CompleteRecovery() => Volatile.Write(ref recoveryRequired, 0);

    internal void Record(string operation, string stage, string outcome, bool submitted = false,
        string? requested = null, string? confirmed = null, string? failure = null)
    {
        lock (sync)
        {
            if (stage == "Query")
            {
                var current = (outcome, confirmed, failure);
                if (lastQueries.TryGetValue(operation, out var previous) && previous == current)
                {
                    return;
                }
                lastQueries[operation] = current;
            }
            Append(new(DateTimeOffset.UtcNow, operation, stage, outcome,
                submitted, requested, confirmed, failure));
        }
    }

    internal void RecordUniformFrameApplied(RgbColor color)
    {
        lock (sync)
        {
            uniformFrameSuccessCount++;
            if (uniformFrameSuccess?.List is not null)
            {
                observations.Remove(uniformFrameSuccess);
            }
            uniformFrameSuccess = Append(new(
                DateTimeOffset.UtcNow,
                "KeyboardUniformFrame",
                "Readback",
                "Unavailable",
                true,
                $"LastRGB={color.Red:X2}{color.Green:X2}{color.Blue:X2}",
                $"AppliedWithoutColorReadback;Frames={uniformFrameSuccessCount}",
                "FrameColorReadbackUnavailable"));
        }
    }

    private LinkedListNode<ExperimentalControlObservation> Append(
        ExperimentalControlObservation observation)
    {
        while (observations.Count >= DiagnosticExperimentalSupport.MaxObservations)
        {
            LinkedListNode<ExperimentalControlObservation>? removed = observations.First;
            if (removed is null)
            {
                break;
            }
            observations.RemoveFirst();
            if (ReferenceEquals(removed, uniformFrameSuccess))
            {
                uniformFrameSuccess = null;
            }
        }
        return observations.AddLast(observation);
    }

    internal DiagnosticExperimentalSupport Snapshot()
    {
        lock (sync)
        {
            return new(model.ModelNumber, model.EvidenceSource, true, IsPaused, observations.ToArray())
            {
                FeatureEvidence = ExperimentalBladeModels.FeatureEvidenceFor(model),
            };
        }
    }
}
