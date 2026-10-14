using OpenBlade.Contracts;

namespace OpenBlade.Protocol;

internal sealed partial class ExperimentalBladePort
{
    internal static CustomPerformanceCapabilities CreateCustomCapabilities() => new(
        (from cpu in Enum.GetValues<CustomPerformanceLevel>()
         from gpu in Enum.GetValues<CustomPerformanceLevel>()
         let combination = new CustomPerformanceCombination(cpu, gpu)
         where combination.IsWellFormed
         select combination).ToHashSet());

    // Protocol extrapolation for opted-in 02A0 testing. The owner report does not
    // independently validate CPU/GPU levels; never reinterpret Boost or Undervolt.
    private async Task<CustomPerformanceSettings?> ReadCustomPerformanceAsync(CancellationToken cancellationToken)
    {
        ReadOnlyMemory<byte> cpu = await ExchangeAsync("CustomPerformance", 0x0D, 0x87,
            [0, 1, 0], false, cancellationToken).ConfigureAwait(false);
        ReadOnlyMemory<byte> gpu = await ExchangeAsync("CustomPerformance", 0x0D, 0x87,
            [0, 2, 0], false, cancellationToken).ConfigureAwait(false);
        string reported = $"CpuSelector={cpu.Span[1]};CpuLevel={cpu.Span[2]};GpuSelector={gpu.Span[1]};GpuLevel={gpu.Span[2]}";
        if (cpu.Span[1] != 1 || gpu.Span[1] != 2)
        {
            RecordQuery("CustomPerformance", "InconsistentState", reported, "ClusterSelector");
            throw new IOException("Experimental custom performance readback returned an unexpected cluster selector.");
        }
        var combination = new CustomPerformanceCombination(
            (CustomPerformanceLevel)cpu.Span[2], (CustomPerformanceLevel)gpu.Span[2]);
        if (!combination.IsWellFormed)
        {
            RecordQuery("CustomPerformance", "UnsupportedState", reported, "OutsideRepresentableCustomLevels");
            return null;
        }
        var result = new CustomPerformanceSettings(combination.Cpu, combination.Gpu);
        RecordQuery("CustomPerformance", "Confirmed", FormatCustomPerformance(result));
        return result;
    }

    public async Task SetCustomPerformanceAsync(CustomPerformanceSettings value, CancellationToken cancellationToken)
    {
        if (model.ProductId != 0x02A0 || !CustomPerformanceSettings.IsWellFormed(value)
            || !CreateCustomCapabilities().Supports(value) || value.GpuClockOffsets is not null)
        {
            throw new CapabilityUnavailableException("This custom performance request is outside the experimental model contract.");
        }
        await operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        bool submitted = false;
        string requested = FormatCustomPerformance(value);
        try
        {
            RequireWriteAdmission(thermal: true);
            ProductionPerformanceFanState baseline = await ReadPerformanceAsync(cancellationToken).ConfigureAwait(false);
            if (baseline.HyperBoostActive || baseline.Fans != FanMode.Automatic
                || baseline.Performance is not (PerformancePreset.Balanced or PerformancePreset.Silent
                    or PerformancePreset.Performance or PerformancePreset.Custom))
            {
                throw new BladeWriteException(WriteSubmissionState.KnownNotApplied,
                    "Experimental custom performance requires a confirmed ordinary AC preset with Automatic fans.");
            }
            // Read the stored levels before entering Custom. An ACK or a guessed
            // default cannot provide an exact baseline for this extrapolated command.
            CustomPerformanceSettings? baselineLevels = baseline.Performance == PerformancePreset.Custom
                ? baseline.CustomPerformance
                : await ReadCustomPerformanceAsync(cancellationToken).ConfigureAwait(false);
            if (baselineLevels is null)
            {
                throw new BladeWriteException(WriteSubmissionState.KnownNotApplied,
                    "Experimental custom performance requires representable baseline CPU/GPU levels.");
            }
            policy.Record("CustomPerformance", "Baseline", "Confirmed", requested: requested,
                confirmed: $"{baseline.Performance}/{baseline.Fans};{FormatCustomPerformance(baselineLevels)}");
            if (baseline.Performance == PerformancePreset.Custom && baselineLevels == value)
            {
                return;
            }
            CustomPerformanceSettings currentLevels = baselineLevels;
            if (baseline.Performance != PerformancePreset.Custom)
            {
                foreach (byte selector in new byte[] { 1, 2 })
                {
                    RequireWriteAdmission(thermal: true);
                    await ExchangeAsync("CustomPerformance", 0x0D, 0x02, [1, selector, 4, 0],
                        true, cancellationToken).ConfigureAwait(false);
                    submitted = true;
                }
                ProductionPerformanceFanState entered = await ReadPerformanceAsync(cancellationToken).ConfigureAwait(false);
                if (entered.Performance != PerformancePreset.Custom || entered.Fans != FanMode.Automatic
                    || entered.HyperBoostActive || entered.CustomPerformance is null)
                {
                    throw new IOException("Independent readback did not confirm entry into Custom with representable levels.");
                }
                currentLevels = entered.CustomPerformance;
            }
            // Lower the currently High domain before raising the other one. Every
            // intermediate state must satisfy the same contract as the final state.
            byte[] clusters = currentLevels.Gpu == CustomPerformanceLevel.High && value.Cpu == CustomPerformanceLevel.High
                ? [2, 1] : [1, 2];
            foreach (byte cluster in clusters)
            {
                CustomPerformanceLevel level = cluster == 1 ? value.Cpu : value.Gpu;
                CustomPerformanceLevel current = cluster == 1 ? currentLevels.Cpu : currentLevels.Gpu;
                if (current == level)
                {
                    continue;
                }
                var expected = cluster == 1
                    ? new CustomPerformanceSettings(level, currentLevels.Gpu)
                    : new CustomPerformanceSettings(currentLevels.Cpu, level);
                RequireWriteAdmission(thermal: true);
                await ExchangeAsync("CustomPerformance", 0x0D, 0x07, [1, cluster, (byte)level],
                    true, cancellationToken).ConfigureAwait(false);
                submitted = true;
                CustomPerformanceSettings? observed = await ReadCustomPerformanceAsync(cancellationToken).ConfigureAwait(false);
                if (observed is null || observed != expected)
                {
                    throw new IOException("Independent custom level readback did not confirm the requested intermediate state.");
                }
                currentLevels = observed;
            }
            ProductionPerformanceFanState confirmed = await ReadPerformanceAsync(cancellationToken).ConfigureAwait(false);
            if (confirmed.Performance != PerformancePreset.Custom || confirmed.Fans != FanMode.Automatic
                || confirmed.HyperBoostActive || confirmed.CustomPerformance != value)
            {
                throw new IOException("Independent custom performance readback did not confirm the requested levels.");
            }
            policy.Record("CustomPerformance", "Readback", "Confirmed", true, requested, requested);
        }
        catch (Exception exception)
        {
            throw MapWriteFailure("CustomPerformance", submitted, exception);
        }
        finally
        {
            operationGate.Release();
        }
    }

    private static string FormatCustomPerformance(CustomPerformanceSettings value) => $"CPU={value.Cpu};GPU={value.Gpu}";
}
