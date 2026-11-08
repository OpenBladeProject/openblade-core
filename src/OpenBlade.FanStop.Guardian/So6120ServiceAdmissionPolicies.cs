using OpenBlade.Contracts;
using OpenBlade.Service;

namespace OpenBlade.FanStop.Guardian;

internal static class So6120ServiceInitialAdmissionDiagnostics
{
    internal static string Refusal(string stage, string detail) =>
        $"Initial command-free Stop admission refused (stage={stage}; {detail}).";

    internal static string ThermalRefusal(FanStopSettings settings, FanTemperatureReadings reading,
        TimeSpan age, bool canceled, FanStopThermalDecision? decision = null) => Refusal("ThermalEntry",
            FormattableString.Invariant($"{(decision is null ? "" : decision.Diagnostic + "; ")}mode={settings.EndMode}; cpuC={Value(reading.CpuCelsius)}; gpuC={Value(reading.GpuCelsius)}; gpuInactive={reading.GpuInactive}; gpuPoweredDown={reading.GpuPoweredDown}; sampleAgeMs={Math.Clamp(age.TotalMilliseconds, -1, 60000):F1}; sampleCanceled={canceled}; cpuEntryLimitC={(settings.EndMode == FanStopEndMode.Auto ? 55 : settings.CpuImmediateCutoffCelsius)}; gpuEntryLimitC={(settings.EndMode == FanStopEndMode.Custom ? Value(settings.GpuImmediateCutoffCelsius) : "inactive-required")}; maxSampleAgeMs=2000"));

    private static string Value(int? value) => value?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "unavailable";
}

// Stop confirmation means observed stopped tachometers, not just a zero-PWM
// command. Keep that evidence sticky and preserve the entry/coast failure even
// when the same owner successfully restores Auto afterward.
internal sealed class So6120ServiceStopProgress
{
    internal bool StopConfirmed { get; private set; }
    internal string? IncompleteReason { get; private set; }
    internal string? ThermalReason { get; private set; }
    internal string? CompletionReason => IncompleteReason ?? ThermalReason;
    internal string ReturningReason => CompletionReason ?? "Auto requested or continuation admission ended.";

    internal void RecordThermalExit(FanStopEndReason endReason, FanStopThermalDecision? decision)
    {
        if (endReason == FanStopEndReason.ThermalThreshold && decision is not null)
        { ThermalReason ??= $"Stop ended (endReason={endReason}; {decision.Diagnostic})."; }
    }

    internal void RecordEntryFailure(So6120DutyFailure failure, string? admissionDetail)
    {
        string detail = string.IsNullOrWhiteSpace(admissionDetail) ? string.Empty : $" {admissionDetail}";
        string reason = $"Stop entry did not complete ({failure}).{detail}";
        IncompleteReason ??= reason[..Math.Min(reason.Length, 900)];
    }

    internal void RecordCoast(So6120StopCoastResult coast, FanStopEndReason endReason,
        FanStopThermalDecision? decision = null)
    {
        RecordThermalExit(endReason, decision);
        if (coast == So6120StopCoastResult.Stopped) { StopConfirmed = true; }
        else if (!StopConfirmed)
        {
            string thermalDetail = endReason == FanStopEndReason.ThermalThreshold && decision is not null
                ? "; " + decision.Diagnostic : "";
            IncompleteReason ??= $"Stopped fans were not confirmed during coast-down ({coast}; endReason={endReason}{thermalDetail}).";
        }
    }
}

internal sealed class So6120ServicePowerReadException(Exception inner)
    : IOException("The adapter read did not establish a resolved native operation.", inner)
{
    internal bool NativeOperationUnresolved => true;
}

internal static class So6120ServicePowerReadPolicy
{
    internal static PowerSource Classify(AdapterPowerState state) => state switch
    {
        AdapterPowerState.Battery => PowerSource.Battery,
        AdapterPowerState.FullPower280W or AdapterPowerState.FullPower330W
            or AdapterPowerState.SufficientExternal => PowerSource.PluggedIn,
        AdapterPowerState.UsbCPower45W or AdapterPowerState.UsbCPower60W or AdapterPowerState.UsbCPower65W
            or AdapterPowerState.UsbCPower80W or AdapterPowerState.UsbCPower90W or AdapterPowerState.UsbCPower100W
            or AdapterPowerState.LimitedExternal => PowerSource.UsbC,
        _ => PowerSource.ExternalUnknown,
    };

    internal static async Task<PowerSource> ReadAsync(Func<CancellationToken, Task<AdapterPowerState>> query,
        CancellationToken token)
    {
        try
        {
            // Unknown from this typed, completed reply ends Stop admission. It
            // does not invent ambiguity in the separate, still-owned EC path.
            return Classify(await query(token).ConfigureAwait(false));
        }
        catch (Exception exception) { throw new So6120ServicePowerReadException(exception); }
    }
}

// This cache grants no command authority. Every actual command still performs
// the independent full admission in ISo6120SessionEvidenceSource.Capture.
internal sealed class So6120ServiceContinuationMonitor(Func<bool> observe, TimeProvider clock)
{
    internal static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(1);
    internal static readonly TimeSpan MaximumSampleAge = TimeSpan.FromSeconds(2);
    private long? started;
    private bool admitted;
    internal bool SampleFresh { get; private set; }

    internal bool Observe()
    {
        if (started is long previous && clock.GetElapsedTime(previous) is var elapsed
            && elapsed >= TimeSpan.Zero && elapsed < PollInterval) { return admitted; }
        started = clock.GetTimestamp();
        admitted = false;
        SampleFresh = false;
        try
        {
            bool result = observe();
            TimeSpan age = clock.GetElapsedTime(started.Value);
            SampleFresh = age >= TimeSpan.Zero && age <= MaximumSampleAge;
            admitted = result && SampleFresh;
        }
        catch { admitted = false; }
        return admitted;
    }
}
