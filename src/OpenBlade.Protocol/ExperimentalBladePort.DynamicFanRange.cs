using OpenBlade.Contracts;

namespace OpenBlade.Protocol;

internal sealed partial class ExperimentalBladePort
{
    private static bool UsesDynamicExperimentalFanRange(ExperimentalBladeModel model) =>
        model.ProductId == 0x0245;

    internal static DeviceCapabilities WithConfirmedExperimentalFanRange(
        DeviceCapabilities capabilities, ExperimentalBladeModel selectedModel, FanSpeedRange? range) =>
        UsesDynamicExperimentalFanRange(selectedModel) && capabilities.FanSpeedRange is not null
            && range is { IsWellFormed: true }
            ? capabilities with
            {
                FanSpeedRange = range,
                FanSpeedRangesByPerformancePreset = new Dictionary<PerformancePreset, FanSpeedRange>
                {
                    [PerformancePreset.Balanced] = range,
                },
            }
            : capabilities;

    private async Task<FanSpeedRange> ReadDynamicExperimentalFanRangeAsync(CancellationToken cancellationToken)
    {
        // Exact product 581's pinned 6737 Ro() queries class 0 after selecting
        // a preset. Its B/D/F fields are minimum/default/maximum in hundreds
        // of RPM. Query the already confirmed current preset directly; never
        // reproduce Synapse's initialization writes to discover another range.
        ReadOnlyMemory<byte> payload = await ExchangeAsync("FanRange", 0x0D, 0x83,
            new byte[12], false, cancellationToken).ConfigureAwait(false);
        FanSpeedRange outer = GetExperimentalFanRange(model)
            ?? throw new CapabilityUnavailableException("The exact model has no reviewed manual fan envelope.");
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
        policy.ConfirmFanRange(range);
        RecordQuery("FanRange", "Confirmed",
            $"MinimumRpm={minimum};DefaultRpm={defaultRpm};MaximumRpm={maximum};AdmittedMinimumRpm={admittedMinimum};AdmittedMaximumRpm={admittedMaximum}");
        return range;
    }
}
