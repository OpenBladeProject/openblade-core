using OpenBlade.Contracts;

namespace OpenBlade.Protocol;

internal sealed partial class ExperimentalBladePort
{
    internal static CustomPerformanceCapabilities CreateCustomCapabilities() =>
        CreateCustomCapabilities(ExperimentalBladeModels.All.Single(item => item.ProductId == 0x02A0));

    internal static CustomPerformanceCapabilities CreateCustomCapabilities(ExperimentalBladeModel model) => new(
        (from cpu in Enum.GetValues<CustomPerformanceLevel>()
         from gpu in Enum.GetValues<CustomPerformanceLevel>()
         let combination = new CustomPerformanceCombination(cpu, gpu)
         where SupportsExperimentalCustom(model) && combination.IsWellFormed
             && (model.ProductId is 0x029F or 0x02A0
                 || cpu != CustomPerformanceLevel.Boost
                 && (cpu, gpu) is not (CustomPerformanceLevel.High, CustomPerformanceLevel.High))
         select combination).ToHashSet())
    {
        SupportsMaximumFans = model.ProductId is 0x029F or 0x02A0,
    };

    // 029F High/High and CPU Boost are explicit Synapse captures in tdakhran
    // 8e12e778/data/README.md. Product 672 sets CPU and GPU levels independently
    // in pinned 6737.e50bb836b878190315d7.js at UTF-8 bytes 1560351/1560392, with no
    // pair restriction. All twelve 02A0 pairs are source-backed experimental;
    // Observer additionally corroborates Boost/Low and Boost/High. See
    // docs/blade18-observer-evidence.md. Other models retain their bounds;
    // raw level 4 is not a normal Custom level.
    private async Task<CustomPerformanceSettings?> ReadCustomPerformanceAsync(
        CancellationToken cancellationToken, bool includeMaximumFans = true)
    {
        // Product 672 defaults Custom GETs to profile 1; the exact CPU request
        // is retained in the 2026-10-16 Observer fixture. Other profiles keep
        // their separately admitted upstream query shape; this does not prove 0 fails.
        byte profile = model.ProductId == 0x02A0 ? (byte)1 : (byte)0;
        ReadOnlyMemory<byte> cpu = await ExchangeAsync("CustomPerformance", 0x0D, 0x87,
            [profile, 1, 0], false, cancellationToken).ConfigureAwait(false);
        ReadOnlyMemory<byte> gpu = await ExchangeAsync("CustomPerformance", 0x0D, 0x87,
            [profile, 2, 0], false, cancellationToken).ConfigureAwait(false);
        string reported = $"CpuSelector={cpu.Span[1]};CpuLevel={cpu.Span[2]};GpuSelector={gpu.Span[1]};GpuLevel={gpu.Span[2]}";
        if (cpu.Span[1] != 1 || gpu.Span[1] != 2)
        {
            RecordQuery("CustomPerformance", "InconsistentState", reported, "ClusterSelector");
            throw new IOException("Experimental custom performance readback returned an unexpected cluster selector.");
        }
        var combination = new CustomPerformanceCombination(
            (CustomPerformanceLevel)cpu.Span[2], (CustomPerformanceLevel)gpu.Span[2]);
        if (!CreateCustomCapabilities(model).AllowedCombinations.Contains(combination))
        {
            RecordQuery("CustomPerformance", "UnsupportedState", reported, "OutsideRepresentableCustomLevels");
            throw ContentRejected("Experimental custom performance levels are not representable.",
                DeviceStateQuery.CustomPerformanceCombination, cpu.Span, gpu.Span);
        }
        var result = new CustomPerformanceSettings(combination.Cpu, combination.Gpu)
        {
            MaximumFans = includeMaximumFans && CreateCustomCapabilities(model).SupportsMaximumFans
                && await ReadMaximumFansAsync(cancellationToken).ConfigureAwait(false),
        };
        RecordQuery("CustomPerformance", "Confirmed", FormatCustomPerformance(result));
        return result;
    }

    public async Task SetCustomPerformanceAsync(CustomPerformanceSettings value, CancellationToken cancellationToken)
    {
        if (!SupportsExperimentalCustom(model) || !CustomPerformanceSettings.IsWellFormed(value)
            || !CreateCustomCapabilities(model).Supports(value) || value.GpuClockOffsets is not null)
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
            // 029F/029D admit fixed targets through Custom entry (sqmagellan
            // 9c66e705/librazer/src/command.rs). 02A0 admits fixed targets only
            // in Balanced, so it requires Automatic handback before Custom.
            if (baseline.HyperBoostActive || baseline.Fans != FanMode.Automatic
                    && (GetExperimentalFanRange(model) is null || model.ProductId == 0x02A0)
                || baseline.Performance is not (PerformancePreset.Balanced or PerformancePreset.Silent
                    or PerformancePreset.Performance or PerformancePreset.Custom))
            {
                throw new BladeWriteException(WriteSubmissionState.KnownNotApplied,
                    "Experimental custom performance requires an ordinary AC preset and a model-supported fan baseline.");
            }
            if (baseline.Fans == FanMode.Fixed && value.MaximumFans)
            {
                // Maximum fans overrides fixed targets. Require an explicit
                // Automatic request rather than silently discarding fan intent.
                throw new BladeWriteException(WriteSubmissionState.KnownNotApplied,
                    "Select Automatic fans before enabling Custom maximum fans.");
            }
            (int First, int Second)? baselineTargets = baseline.Fans == FanMode.Fixed
                ? await ReadFanTargetsAsync(cancellationToken).ConfigureAwait(false) : null;
            // Read the stored levels before entering Custom. An ACK or a guessed
            // default cannot provide an exact baseline for this extrapolated command.
            CustomPerformanceSettings? baselineLevels = baseline.Performance == PerformancePreset.Custom
                ? baseline.CustomPerformance
                : await ReadCustomPerformanceAsync(cancellationToken, includeMaximumFans: false).ConfigureAwait(false);
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
            if (CreateCustomCapabilities(model).SupportsMaximumFans)
            {
                // 07/0F replaces the entire shared Power Mode Control byte.
                // Refuse a conflicting maximum-fan change before mode/level writes.
                byte flags = await ReadPowerModeControlAsync(cancellationToken).ConfigureAwait(false);
                if (((flags & TemporaryFullChargeProtocol.MaximumFanSpeedBit) != 0) != value.MaximumFans)
                {
                    RequireMaximumFansWriteBaseline(flags);
                }
            }
            CustomPerformanceSettings currentLevels = baselineLevels;
            if (baseline.Performance != PerformancePreset.Custom)
            {
                foreach (byte selector in new byte[] { 1, 2 })
                {
                    RequireWriteAdmission(thermal: true);
                    await ExchangeAsync("CustomPerformance", 0x0D, 0x02,
                        [1, selector, 4, baseline.Fans == FanMode.Fixed ? (byte)1 : (byte)0],
                        true, cancellationToken).ConfigureAwait(false);
                    submitted = true;
                }
                ProductionPerformanceFanState entered = await ReadPerformanceAsync(cancellationToken).ConfigureAwait(false);
                if (entered.Performance != PerformancePreset.Custom || entered.Fans != baseline.Fans
                    || entered.HyperBoostActive || entered.CustomPerformance is null
                    || baselineTargets.HasValue
                        && await ReadFanTargetsAsync(cancellationToken).ConfigureAwait(false) != baselineTargets.Value)
                {
                    throw new IOException("Independent readback did not confirm entry into Custom with representable levels.");
                }
                currentLevels = entered.CustomPerformance;
            }
            // Each setter changes one cluster. Use the fewest admitted intermediate
            // combinations; only models with concrete pair restrictions need a bridge.
            CustomPerformanceCombination[] path = PlanCustomLevelChanges(
                CreateCustomCapabilities(model), currentLevels, value);
            foreach (CustomPerformanceCombination next in path)
            {
                byte cluster = next.Cpu != currentLevels.Cpu ? (byte)1 : (byte)2;
                CustomPerformanceLevel level = cluster == 1 ? next.Cpu : next.Gpu;
                CustomPerformanceLevel current = cluster == 1 ? currentLevels.Cpu : currentLevels.Gpu;
                if (current == level)
                {
                    continue;
                }
                var expected = cluster == 1
                    ? new CustomPerformanceSettings(level, currentLevels.Gpu)
                    : new CustomPerformanceSettings(currentLevels.Cpu, level);
                expected = expected with { MaximumFans = currentLevels.MaximumFans };
                if (!CreateCustomCapabilities(model).Supports(expected))
                {
                    throw new IOException("The intermediate custom combination is not admitted for this model.");
                }
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
            if (currentLevels.MaximumFans != value.MaximumFans)
            {
                // Recheck immediately before the whole-byte SET in this session;
                // do not extrapolate read-modify-write or clear another feature.
                RequireMaximumFansWriteBaseline(
                    await ReadPowerModeControlAsync(cancellationToken).ConfigureAwait(false));
                RequireWriteAdmission(thermal: true);
                // Products 671 and 672 inherit the same pinned rzDevice25 SET contract:
                // 671 rzDevice25.5e330aa9fdf6f29eec62.js
                // (SHA-256 41E7758114AD3605B5B3EE0E65E79AF3055124F775904902BA3285F710AF55C9),
                // and 672 rzDevice25.85ce8530211c29f3c304.js
                // (SHA-256 DFB3E3612D36B20910AF02E61A17ED64A1FFD3781E09C63BAAEE7CBEFDEBCA24).
                // Both accept a correlated successful 07/0F ACK without requiring an echo
                // (671 helper/public chars 20394/140294; 672 chars 52955/141017).
                // Bound it to zero or one byte and keep the independent GET authoritative.
                bool acceptsStatusOnlyAck = model.ProductId is 0x029F or 0x02A0;
                await ExchangeAsync("MaximumFans", 0x07, 0x0F,
                    [value.MaximumFans ? TemporaryFullChargeProtocol.MaximumFanSpeedBit : (byte)0],
                    true, cancellationToken,
                    variableResponse: acceptsStatusOnlyAck,
                    payloadValidator: acceptsStatusOnlyAck ? static payload => payload.Length <= 1 : null,
                    requireEcho: !acceptsStatusOnlyAck).ConfigureAwait(false);
                submitted = true;
                if (await ReadMaximumFansAsync(cancellationToken).ConfigureAwait(false) != value.MaximumFans)
                {
                    throw new IOException("Independent maximum-fan readback did not confirm the requested flag.");
                }
            }
            ProductionPerformanceFanState confirmed = await ReadPerformanceAsync(cancellationToken).ConfigureAwait(false);
            if (confirmed.Performance != PerformancePreset.Custom || confirmed.Fans != baseline.Fans
                || confirmed.HyperBoostActive || confirmed.CustomPerformance != value
                || baselineTargets.HasValue
                    && await ReadFanTargetsAsync(cancellationToken).ConfigureAwait(false) != baselineTargets.Value)
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

    private static CustomPerformanceCombination[] PlanCustomLevelChanges(
        CustomPerformanceCapabilities capabilities,
        CustomPerformanceSettings baseline,
        CustomPerformanceSettings requested)
    {
        var start = new CustomPerformanceCombination(baseline.Cpu, baseline.Gpu);
        var target = new CustomPerformanceCombination(requested.Cpu, requested.Gpu);
        if (start == target)
        {
            return [];
        }
        // At most twelve combinations exist. Breadth-first search preserves direct
        // transitions and adds a bridge only when both direct orderings are blocked.
        var visited = new HashSet<CustomPerformanceCombination> { start };
        var queue = new Queue<(CustomPerformanceCombination State, CustomPerformanceCombination[] Path)>();
        queue.Enqueue((start, []));
        CustomPerformanceCombination[] candidates = capabilities.AllowedCombinations
            .OrderByDescending(item => item.Cpu == target.Cpu)
            .ThenByDescending(item => item.Gpu == target.Gpu)
            .ThenBy(item => Math.Abs((int)item.Cpu - (int)CustomPerformanceLevel.Medium))
            .ToArray();
        while (queue.TryDequeue(out var current))
        {
            foreach (CustomPerformanceCombination next in candidates)
            {
                if ((next.Cpu == current.State.Cpu) == (next.Gpu == current.State.Gpu)
                    || !visited.Add(next))
                {
                    continue;
                }
                CustomPerformanceCombination[] path = [.. current.Path, next];
                if (next == target)
                {
                    return path;
                }
                queue.Enqueue((next, path));
            }
        }
        throw new IOException("No admitted transition connects these custom performance combinations.");
    }

    private async Task<bool> ReadMaximumFansAsync(CancellationToken cancellationToken) =>
        (await ReadPowerModeControlAsync(cancellationToken).ConfigureAwait(false)
            & TemporaryFullChargeProtocol.MaximumFanSpeedBit) != 0;

    private async Task<byte> ReadPowerModeControlAsync(CancellationToken cancellationToken)
    {
        ReadOnlyMemory<byte> payload = await ExchangeAsync("MaximumFans", 0x07, 0x8F,
            [0], false, cancellationToken, variableResponse: true,
            payloadValidator: static value => value.Length <= RazerReport.MaximumPayloadLength).ConfigureAwait(false);
        if (!TemporaryFullChargeProtocol.TryDecodeQuery(payload.Span, out _))
        {
            RecordQuery("MaximumFans", "UnsupportedState",
                DeviceStateContentRejection.FormatPayload(payload.Span), "OutsideSharedPowerModeControlContract");
            throw ContentRejected("Experimental Power Mode Control is outside the shared bitmask contract.",
                DeviceStateQuery.PerformanceAndFans, payload.Span);
        }
        RecordQuery("MaximumFans", "Confirmed",
            $"PowerModeControl=0x{payload.Span[0]:X2};MaximumFans={(payload.Span[0] & TemporaryFullChargeProtocol.MaximumFanSpeedBit) != 0};"
                + $"OtherSettings=0x{payload.Span[0] & ~TemporaryFullChargeProtocol.MaximumFanSpeedBit:X2};"
                + $"TemporaryFullChargeBit={(payload.Span[0] & TemporaryFullChargeProtocol.TemporaryFullChargeBit) != 0};"
                + $"CpuBoostBit={(payload.Span[0] & TemporaryFullChargeProtocol.CpuBoostBit) != 0};"
                + $"LocalDimmingBit={(payload.Span[0] & TemporaryFullChargeProtocol.LocalDimmingBit) != 0}");
        return payload.Span[0];
    }

    private static void RequireMaximumFansWriteBaseline(byte flags)
    {
        byte otherSettings = (byte)(flags & ~TemporaryFullChargeProtocol.MaximumFanSpeedBit);
        if (otherSettings == 0)
        {
            return;
        }
        var names = new List<string>(TemporaryFullChargeProtocol.DescribeOtherSettings(otherSettings));
        if ((otherSettings & TemporaryFullChargeProtocol.TemporaryFullChargeBit) != 0)
        {
            names.Add("temporary full charge");
        }
        throw new BladeWriteException(WriteSubmissionState.KnownNotApplied,
            "OpenBlade won't change maximum fans because it would also change other laptop settings that are on ("
                + string.Join(", ", names) + ").");
    }

    private static string FormatCustomPerformance(CustomPerformanceSettings value) =>
        $"CPU={value.Cpu};GPU={value.Gpu}" + (value.MaximumFans ? ";MaximumFans=True" : string.Empty);
}
