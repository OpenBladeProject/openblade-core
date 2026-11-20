using OpenBlade.Contracts;

namespace OpenBlade.Protocol;

internal sealed partial class ExperimentalBladePort
{
    // Synapse products 671/669 enable mode 7 for 029F/029D. Experimental padless use
    // shares that exact command; see docs/experimental-hyperboost-evidence.md.
    public Task SetHyperBoostAsync(bool enabled, CancellationToken cancellationToken) =>
        SetHyperBoostAsync(enabled, model.ProductId == 0x029D ? PerformancePreset.Balanced : PerformancePreset.Performance,
            ObserveHyperBoostCoolingPad() == CoolingPadConnectionState.PresentExact, cancellationToken);

    public Task SetHyperBoostAsync(bool enabled, PerformancePreset baseMode,
        CancellationToken cancellationToken) =>
        SetHyperBoostAsync(enabled, baseMode,
            ObserveHyperBoostCoolingPad() == CoolingPadConnectionState.PresentExact, cancellationToken);

    public async Task SetHyperBoostAsync(bool enabled, PerformancePreset baseMode,
        bool requiresCoolingPad, CancellationToken cancellationToken)
    {
        if (!SupportsExperimentalHyperBoost(model)
            || Capabilities.HyperBoostAccess != DeviceFeatureAccess.ReadWrite
            || Capabilities.HyperBoostBaseModes?.AccessFor(baseMode) != DeviceFeatureAccess.ReadWrite)
        {
            throw new CapabilityUnavailableException("HyperBoost is outside this exact experimental model's contract.");
        }
        await operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        bool submitted = false;
        bool maximumFansCleared = false;
        bool acceptsStatusOnlyAck = UsesSourceBackedSetAckContract(model);
        string operation = enabled ? "HyperBoost" : "HyperBoostRestore";
        try
        {
            RequireWriteAdmission();
            PowerSource source = capturePowerSource();
            bool balancedCleanup = !enabled && baseMode == PerformancePreset.Balanced;
            bool batteryCleanup = balancedCleanup && source == PowerSource.Battery;
            if (!ExternalPowerReported(source) && !batteryCleanup)
            {
                throw new BladeWriteException(WriteSubmissionState.KnownNotApplied,
                    "HyperBoost requires confirmed external power; battery exit restores Balanced with Automatic fans.");
            }
            bool StillAdmitted() => capturePowerSource() == source
                && (!enabled || ObserveHyperBoostCoolingPad() == (requiresCoolingPad
                    ? CoolingPadConnectionState.PresentExact : CoolingPadConnectionState.Absent));
            if (!StillAdmitted())
            {
                throw new BladeWriteException(WriteSubmissionState.KnownNotApplied,
                    "The journaled HyperBoost cooling-pad path could not be confirmed.");
            }
            if (enabled)
            {
                // Exact product-specific firmwareMinimumRequired, not BIOS provenance.
                Version minimum = model.ProductId == 0x029D ? new(1, 1, 1, 3) : new(1, 5, 0, 0);
                ReadOnlyMemory<byte> firmware = await ExchangeAsync("HyperBoostFirmware", 0, 0x87,
                    [0, 0, 0, 0], false, cancellationToken).ConfigureAwait(false);
                if (!McuVersionProtocol.TryDecodeQuery(firmware.Span, out string version)
                    || !Version.TryParse(version, out Version? parsed) || parsed < minimum)
                {
                    throw new BladeWriteException(WriteSubmissionState.KnownNotApplied,
                        $"{model.ModelNumber} HyperBoost requires MCU firmware {minimum} or later.");
                }
                RecordQuery("HyperBoostFirmware", "Confirmed", version);
            }
            ProductionPerformanceFanState baseline = await ReadPerformanceAsync(cancellationToken).ConfigureAwait(false);
            if (enabled ? baseline.HyperBoostActive || baseline.Performance != baseMode
                || baseline.Fans is not (FanMode.Automatic or FanMode.Fixed)
                : !baseline.HyperBoostActive || baseline.Fans != FanMode.Automatic)
            {
                throw new BladeWriteException(WriteSubmissionState.KnownNotApplied,
                    "The confirmed HyperBoost transition baseline changed.");
            }
            policy.Record(operation, "Baseline", "Confirmed", requested: enabled ? "HyperBoost" : baseMode.ToString(),
                confirmed: $"{baseline.Performance}/{baseline.Fans};HyperBoost={baseline.HyperBoostActive}");
            // Synapse clears Maximum Fans when leaving Custom. Reuse the shared
            // whole-byte guard: never clear local dimming or another power option.
            if (enabled)
            {
                byte flags = await ReadPowerModeControlAsync(cancellationToken).ConfigureAwait(false);
                if ((flags & TemporaryFullChargeProtocol.MaximumFanSpeedBit) != 0)
                {
                    // Products 671/669 both clear this bit before any non-Custom
                    // setter (671 policy byte 1397809, 669 policy byte 1557908).
                    // 029F/029D represent it in Custom intent. The service journals
                    // that intent, and on exit its Custom restore compares Maximum
                    // Fans with the confirmed flag and writes it back when needed.
                    // Every model that admits HyperBoost today also admits Maximum
                    // Fans, so this refusal is a guard for a future model without
                    // representable intent: never clear a flag it cannot restore.
                    if (!CreateCustomCapabilities(model).SupportsMaximumFans)
                    {
                        throw new BladeWriteException(WriteSubmissionState.KnownNotApplied,
                            "Turn off Maximum Fans in Synapse before enabling HyperBoost; OpenBlade cannot restore that option on this model.");
                    }
                    RequireMaximumFansWriteBaseline(flags);
                    // Cleanup belongs to this HyperBoost request; it cannot inherit
                    // ordinary Unknown-AC admission before the mode-7 command.
                    bool statusOnlyPowerModeAck = UsesStatusOnlyPowerModeControlAck(model);
                    await ExchangeAsync("MaximumFans", 0x07, 0x0F, [0], true, cancellationToken,
                        variableResponse: statusOnlyPowerModeAck,
                        payloadValidator: statusOnlyPowerModeAck ? static payload => payload.Length <= 1 : null,
                        requireEcho: !statusOnlyPowerModeAck,
                        writeAdmission: StillAdmitted,
                        allowUnclassifiedExternal: false).ConfigureAwait(false);
                    submitted = true;
                    if (await ReadPowerModeControlAsync(cancellationToken).ConfigureAwait(false) != 0)
                    {
                        throw new IOException("HyperBoost Maximum Fans cleanup was not confirmed.");
                    }
                    // The cleanup is confirmed and mode 7 has not been sent, so
                    // the state is fully known. A later admission refusal is not
                    // ambiguous; the service can reapply the saved Custom intent.
                    submitted = false;
                    maximumFansCleared = true;
                }
            }
            byte mode = enabled ? (byte)7 : (byte)(baseMode switch
            {
                PerformancePreset.Balanced => source == PowerSource.Battery ? 6 : 0,
                PerformancePreset.Performance => 2,
                PerformancePreset.Silent => 5,
                PerformancePreset.Custom => 4,
                _ => throw new CapabilityUnavailableException("Unrepresentable HyperBoost restore mode."),
            });
            foreach (byte selector in new byte[] { 1, 2 })
            {
                RequireWriteAdmission();
                if (!StillAdmitted())
                {
                    throw new BladeWriteException(WriteSubmissionState.KnownNotApplied,
                        maximumFansCleared && !submitted
                            ? "HyperBoost cooling-pad or power admission changed before submission. Maximum Fans was turned off for HyperBoost; reapply Custom to turn it back on."
                            : "HyperBoost cooling-pad or power admission changed before submission.");
                }
                await ExchangeAsync(operation, 0x0D, 0x02, [1, selector, mode, 0], true, cancellationToken,
                    variableResponse: acceptsStatusOnlyAck,
                    payloadValidator: acceptsStatusOnlyAck ? static payload => payload.Length <= 4 : null,
                    requireEcho: !acceptsStatusOnlyAck,
                    writeAdmission: StillAdmitted,
                    allowUnclassifiedExternal: false).ConfigureAwait(false);
                submitted = true;
            }
            ProductionPerformanceFanState confirmed = await ReadPerformanceAsync(cancellationToken).ConfigureAwait(false);
            if (confirmed.HyperBoostActive != enabled || confirmed.Fans != FanMode.Automatic
                || !enabled && confirmed.Performance != baseMode)
            {
                throw new IOException("Independent HyperBoost readback did not confirm the requested state.");
            }
            policy.Record(operation, "Readback", "Confirmed", true,
                enabled ? "HyperBoost" : baseMode.ToString(),
                enabled ? "HyperBoost/Automatic" : $"{baseMode}/Automatic");
        }
        catch (Exception exception)
        {
            throw MapWriteFailure(operation, submitted, exception);
        }
        finally
        {
            operationGate.Release();
        }
    }

    private static bool SupportsExperimentalHyperBoost(ExperimentalBladeModel model) =>
        model.ProductId is 0x029F or 0x029D;

    private CoolingPadConnectionState ObserveHyperBoostCoolingPad()
    {
        try
        {
            return coolingPadDiscovery?.Observe() ?? CoolingPadConnectionState.ObservationFailed;
        }
        catch
        {
            return CoolingPadConnectionState.ObservationFailed;
        }
    }
}
