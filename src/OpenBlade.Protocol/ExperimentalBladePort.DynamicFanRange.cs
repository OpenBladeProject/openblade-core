using OpenBlade.Contracts;

namespace OpenBlade.Protocol;

internal sealed partial class ExperimentalBladePort
{
    internal static bool UsesDynamicExperimentalFanRange(ExperimentalBladeModel model) =>
        model.ProductId is 0x0234 or 0x024C or 0x0245 or 0x0253 or 0x0246 or 0x0268
            or 0x0270 or 0x0276 or 0x028C or 0x028A or 0x028B
            or 0x02B6 or 0x02B7 or 0x02B8 or 0x02C7 or 0x0255 or 0x02C5;

    private static bool SupportsDynamicBatteryFixedFans(ExperimentalBladeModel model) => model.ProductId is 0x02C7 or 0x02C5;

    internal static DeviceCapabilities WithConfirmedExperimentalFanRange(
        DeviceCapabilities capabilities, ExperimentalBladeModel selectedModel,
        IReadOnlyDictionary<PerformancePreset, FanSpeedRange> confirmedRanges)
    {
        if (!UsesDynamicExperimentalFanRange(selectedModel) || capabilities.FanSpeedRange is null)
        {
            return capabilities;
        }
        var observed = new Dictionary<PerformancePreset, FanSpeedRange>();
        foreach ((PerformancePreset preset, FanSpeedRange range) in confirmedRanges)
        {
            // Observations never alter reviewed persisted-intent envelopes.
            FanSpeedRange? envelope = capabilities.GetFanIntentSpeedRange(preset);
            if (envelope is not null && range.IsWellFormed
                && envelope.Supports(range.MinimumRpm) && envelope.Supports(range.MaximumRpm))
            {
                observed[preset] = range;
            }
        }
        return capabilities with { ObservedFanSpeedRangesByPerformancePreset = observed };
    }

    private static byte DynamicFanPresetMode(PerformancePreset preset) => preset switch
    {
        PerformancePreset.Balanced => 0,
        PerformancePreset.Gaming or PerformancePreset.Turbo or PerformancePreset.Performance => 1,
        PerformancePreset.Creator or PerformancePreset.MaximumPower => 2,
        PerformancePreset.Custom => 4,
        PerformancePreset.Silent => 5,
        PerformancePreset.BatteryBalanced => 6,
        _ => throw new CapabilityUnavailableException("The preset has no reviewed dynamic fan selector."),
    };

    private async Task<FanSpeedRange> ReadDynamicExperimentalFanRangeAsync(
        CancellationToken cancellationToken, PerformancePreset? expectedPerformance = null,
        (byte First, byte Second, byte? PropagatedSecond)? expectedFanFlags = null)
    {
        // Metadata contains no preset identity. Bind it to agreeing paired
        // selectors before and after the query before publishing the observation.
        // Query the current preset directly without initialization or Auto writes.
        (ReadOnlyMemory<byte> beforeFirst, ReadOnlyMemory<byte> beforeSecond) =
            await ReadPerformanceSelectorsAsync(cancellationToken).ConfigureAwait(false);
        IReadOnlyDictionary<PerformancePreset, FanSpeedRange> envelopes = GetExperimentalFanRangesByPerformancePreset(model);
        PerformancePreset? preset = expectedPerformance;
        if (preset is null)
        {
            foreach (PerformancePreset candidate in envelopes.Keys)
            {
                if (DynamicFanPresetMode(candidate) == beforeFirst.Span[2])
                {
                    preset = candidate;
                    break;
                }
            }
        }
        bool flagsAdmitted = expectedFanFlags is { } flags
            ? beforeFirst.Span[3] == flags.First
                && (beforeSecond.Span[3] == flags.Second || beforeSecond.Span[3] == flags.PropagatedSecond)
            : beforeFirst.Span[3] == beforeSecond.Span[3] && beforeFirst.Span[3] <= 1;
        if (preset is not { } selected || !envelopes.TryGetValue(selected, out FanSpeedRange? outer)
            || beforeFirst.Span[2] != DynamicFanPresetMode(selected)
            || beforeFirst.Span[2] != beforeSecond.Span[2]
            || !flagsAdmitted)
        {
            RecordQuery("Performance", "InconsistentState", failure: "CurrentPresetMetadataAdmissionChanged");
            throw ContentRejected("The current preset cannot admit dynamic fan metadata.",
                DeviceStateQuery.PerformanceAndFans, beforeFirst.Span, beforeSecond.Span);
        }
        // Exact-model Ro() queries class 0; B/D/F are minimum/default/maximum
        // in hundreds of RPM, independently reviewed from target and tachometer GETs.
        ReadOnlyMemory<byte> payload = await ExchangeAsync("FanRange", 0x0D, 0x83,
            new byte[12], false, cancellationToken).ConfigureAwait(false);
        (ReadOnlyMemory<byte> afterFirst, ReadOnlyMemory<byte> afterSecond) =
            await ReadPerformanceSelectorsAsync(cancellationToken).ConfigureAwait(false);
        if (!beforeFirst.Span.SequenceEqual(afterFirst.Span) || !beforeSecond.Span.SequenceEqual(afterSecond.Span))
        {
            RecordQuery("FanRange", "InconsistentState", failure: "PresetOrManualStateChangedDuringMetadata");
            throw ContentRejected("The preset or manual fan flags changed while reading metadata.",
                DeviceStateQuery.PerformanceAndFans, afterFirst.Span, afterSecond.Span);
        }
        int minimum = payload.Span[6] * 100;
        int defaultRpm = payload.Span[8] * 100;
        int maximum = payload.Span[10] * 100;
        int admittedMinimum = Math.Max(minimum, outer.MinimumRpm);
        int admittedMaximum = Math.Min(maximum, outer.MaximumRpm);
        if (payload.Span[0] != 0 || minimum == 0 || minimum > defaultRpm
            || defaultRpm > maximum || admittedMinimum > admittedMaximum)
        {
            RecordQuery("FanRange", "UnsupportedState", failure: "InvalidCurrentPresetFanRange");
            throw ContentRejected("Current-preset fan metadata cannot admit a bounded manual fan operation.",
                DeviceStateQuery.PerformanceAndFans, payload.Span);
        }
        var range = new FanSpeedRange(admittedMinimum, admittedMaximum, outer.StepRpm);
        policy.ConfirmFanRange(selected, range);
        RecordQuery("FanRange", "Confirmed",
            $"Preset={selected};MinimumRpm={minimum};DefaultRpm={defaultRpm};MaximumRpm={maximum};AdmittedMinimumRpm={admittedMinimum};AdmittedMaximumRpm={admittedMaximum}");
        return range;
    }
}
