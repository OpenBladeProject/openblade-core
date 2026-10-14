using OpenBlade.Contracts;

namespace OpenBlade.Protocol;

/// <summary>Upstream evidence permits opted-in testing, not a physical-validation claim.</summary>
internal sealed record ExperimentalBladeModel(
    string ModelNumber,
    string ProductName,
    ushort ProductId,
    bool SupportsBatteryProtection,
    string EvidenceSource);

internal static class ExperimentalBladeModels
{
    public const string RazerCtlSource = "https://github.com/tdakhran/razer-ctl/tree/8e12e778d303d4ed01eeb2c1e99ce2e97f1c8917";
    public const string BladeControlSource = "https://github.com/jdsgnrinfo/blade-control/tree/bc48c916d4d6cc47df8c0e32a73ef454dda37cdd";

    public const string Blade18Source = "https://github.com/sqmagellan/razer-ctl/tree/9c66e7055a0e31f5ec25c16185e66fde1f50cf4a";

    public static IReadOnlyList<ExperimentalBladeModel> All { get; } = Array.AsReadOnly<ExperimentalBladeModel>(
    [
        new("RZ09-0483", "Blade 16 - RZ09-0483", 0x029F, true, RazerCtlSource),
        // Published DMI report: https://img.unikoshardware.com/wp-content/uploads/2023/06/RAZER_BLADE_14Report.htm
        new("RZ09-0482", "Blade 14 - RZ09-0482", 0x029D, true, RazerCtlSource),
        // Published system model: https://browser.geekbench.com/v6/cpu/compare/14894027?baseline=2340230
        new("RZ09-0530", "Blade 14 - RZ09-0530", 0x02C5, true, BladeControlSource),
        // Windows System Model reported by an owner:
        // https://learn.microsoft.com/en-us/answers/questions/3810665/intel-wi-fi-6-ax201-is-not-working
        new("RZ09-0328", "Blade 15 Base Model (Early 2020) - RZ09-0328", 0x0255, false, BladeControlSource),
        // Exact-model owner report: https://github.com/sqmagellan/razer-ctl/issues/1
        // Windows product: https://browser.geekbench.com/v6/cpu/6428586
        new("RZ09-0484", "Blade 18 - RZ09-0484", 0x02A0, true, Blade18Source),
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
    private readonly Queue<ExperimentalControlObservation> observations = new();
    private readonly Dictionary<string, (string Outcome, string? Confirmed, string? Failure)> lastQueries = new(StringComparer.Ordinal);
    private int enabled;
    private int recoveryRequired;

    internal bool Enabled
    {
        get => Volatile.Read(ref enabled) != 0;
        set => Volatile.Write(ref enabled, value ? 1 : 0);
    }

    internal bool WritesAllowed => Enabled && Volatile.Read(ref recoveryRequired) == 0;

    internal void RequireRecovery() => Volatile.Write(ref recoveryRequired, 1);

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
            while (observations.Count >= DiagnosticExperimentalSupport.MaxObservations)
            {
                observations.Dequeue();
            }
            observations.Enqueue(new(DateTimeOffset.UtcNow, operation, stage, outcome,
                submitted, requested, confirmed, failure));
        }
    }

    internal DiagnosticExperimentalSupport Snapshot()
    {
        lock (sync)
        {
            return new(model.ModelNumber, model.EvidenceSource, true, Enabled, observations.ToArray());
        }
    }
}
