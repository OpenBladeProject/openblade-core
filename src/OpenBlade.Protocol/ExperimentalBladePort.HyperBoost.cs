using OpenBlade.Contracts;

namespace OpenBlade.Protocol;

internal sealed partial class ExperimentalBladePort
{
    internal const string MaximumFansClearedForHyperBoostNotice =
        "Maximum Fans was turned off for HyperBoost; reapply Custom to turn it back on.";

    // Synapse products 671/669/695 enable mode 7 for 029F/029D/02B7. Experimental
    // padless use shares that exact command; see docs/experimental-hyperboost-evidence.md.
    public Task SetHyperBoostAsync(bool enabled, CancellationToken cancellationToken) =>
        SetHyperBoostAsync(enabled, HyperBoostCoolingPadEntryBaseMode(model) ?? PerformancePreset.Performance,
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
        // The vendor SKU recheck declines new entries only. Exit and restoration
        // never depend on it, so a laptop can always leave HyperBoost.
        if (enabled && !HyperBoostSystemSkuPolicy.AllowsEntry(HyperBoostSkuAdmission))
        {
            policy.Record("HyperBoost", "Guard", "Blocked",
                confirmed: HyperBoostSkuDiagnostic,
                failure: HyperBoostSkuAdmission.ToString());
            throw new CapabilityUnavailableException(HyperBoostSystemSkuPolicy.EntryRestrictedMessage);
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
            // On battery this contract admits only the Balanced handoff (DC raw 6),
            // so every battery exit sends it, whatever base the session owns. The
            // owned AC base is not a battery mode to restore: Synapse's setter sends
            // Balanced on battery as DC raw 6 (policy bytes 1,398,122 for 671,
            // 1,558,223 for 669 and 1,892,480 for 695) and restores its
            // pre-HyperBoost mode on AC only (bytes 172,278, 1,240,504 and
            // 1,295,406). OpenBlade does not carry the owned base over: when AC
            // returns, the plugged-in profile applies again.
            bool batteryHandoff = !enabled && source == PowerSource.Battery;
            if (!ExternalPowerReported(source) && !batteryHandoff)
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
                Version minimum = HyperBoostMinimumMcuFirmware(model);
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
                // Synapse's setter writes [1, selector, mode, 0] from any state,
                // including mode 7 with manual fans (695 policy setter at byte 1,891,618;
                // fanModeValue auto:0 at byte 2,362,420). Exit admits that baseline
                // and the readback below must confirm Automatic fans.
                : !baseline.HyperBoostActive || baseline.Fans is not (FanMode.Automatic or FanMode.Fixed))
            {
                throw new BladeWriteException(WriteSubmissionState.KnownNotApplied,
                    "The confirmed HyperBoost transition baseline changed.");
            }
            string requested = enabled ? "HyperBoost" : batteryHandoff ? $"{baseMode}/BatteryHandoff=Balanced" : baseMode.ToString();
            policy.Record(operation, "Baseline", "Confirmed", requested: requested,
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
                    // Product 695 gates that clear on hasMaxFanSpeedModeEnabled,
                    // which its device definition omits (policy bytes 1,892,259 and
                    // 1,893,528), so Synapse leaves the bit set before HyperBoost on
                    // 02B7. OpenBlade still clears it there: a documented deviation
                    // that keeps one guarded cleanup on every mode-7 model.
                    // 029F/029D/02B7 represent the bit in Custom intent. The service
                    // journals that intent, and on exit its Custom restore compares
                    // Maximum Fans with the confirmed flag and writes it back when
                    // needed. Every model that admits HyperBoost today also admits
                    // Maximum Fans, so this refusal guards a future model without
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
            byte mode = enabled ? (byte)7 : batteryHandoff ? (byte)6 : (byte)(baseMode switch
            {
                PerformancePreset.Balanced => 0,
                PerformancePreset.Performance => 2,
                PerformancePreset.Silent => 5,
                PerformancePreset.Custom => 4,
                // Product 695 lists Turbo_Mode (main.27df2e7ddab0b6bc34cc.js byte 144,362)
                // and sends it as Gaming_Mode raw 1 (bytes 117,552 and 48,781).
                PerformancePreset.Turbo => 1,
                _ => throw new CapabilityUnavailableException("Unrepresentable HyperBoost restore mode."),
            });
            foreach (byte selector in new byte[] { 1, 2 })
            {
                RequireWriteAdmission();
                if (!StillAdmitted())
                {
                    throw new BladeWriteException(WriteSubmissionState.KnownNotApplied,
                        "HyperBoost cooling-pad or power admission changed before submission.");
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
            // The battery handoff submits raw 6; 02B7 reports it as its DC Balanced preset.
            // An AC exit must land on the owned base. Every exit hands fans to Automatic.
            if (confirmed.HyperBoostActive != enabled || confirmed.Fans != FanMode.Automatic
                || !enabled && !(batteryHandoff
                    ? confirmed.Performance is PerformancePreset.Balanced or PerformancePreset.BatteryBalanced
                    : confirmed.Performance == baseMode))
            {
                throw new IOException("Independent HyperBoost readback did not confirm the requested state.");
            }
            policy.Record(operation, "Readback", "Confirmed", true,
                requested,
                enabled ? "HyperBoost/Automatic" : $"{confirmed.Performance}/Automatic");
        }
        catch (Exception exception)
        {
            BladeWriteException failure = MapWriteFailure(operation, submitted, exception);
            // Every known pre-submission stop after the confirmed cleanup, such
            // as a lost exact host or a native guard refusal, leaves the flag
            // off. Say so; a possibly submitted mode-7 write stays indeterminate.
            if (maximumFansCleared && !submitted
                && failure.SubmissionState == WriteSubmissionState.KnownNotApplied)
            {
                throw new BladeWriteException(WriteSubmissionState.KnownNotApplied, failure.FailurePhase,
                    $"{failure.Message} {MaximumFansClearedForHyperBoostNotice}", exception)
                {
                    MaximumFansClearedBeforeFailure = true,
                };
            }
            throw failure;
        }
        finally
        {
            operationGate.Release();
        }
    }

    private static bool SupportsExperimentalHyperBoost(ExperimentalBladeModel model) =>
        model.ProductId is 0x029F or 0x029D or 0x02B7;

    // Products 671, 669 and 695 all declare isBalancedDCSupported (671 main byte
    // 61,433, 669 main byte 164,049, 695 main byte 164,228), so each sends battery
    // Balanced as raw 6. 029F/029D keep their earlier admitted presentation, which
    // shows raw 6 on battery as Balanced without a separate DC preset; 02B7's
    // reviewed thermal policy lists that preset, so it reports BatteryBalanced.
    private static bool UsesRaw6AsBatteryBalanced(ExperimentalBladeModel model) =>
        model.ProductId is 0x029F or 0x029D;

    // The default pad-present entry: 029D's vendor path uses Balanced, and 02B7
    // uses Turbo, its default AC mode (product 695 main byte 158,285,
    // defaultACPerformanceMode:1). Null keeps the legacy Performance inference (029F).
    private static PerformancePreset? HyperBoostCoolingPadEntryBaseMode(ExperimentalBladeModel model) =>
        model.ProductId switch
        {
            0x029D => PerformancePreset.Balanced,
            0x02B7 => PerformancePreset.Turbo,
            _ => null,
        };

    // Exact product-specific firmwareMinimumRequired, not BIOS provenance. Entry
    // compares the 00/87 MCU version with it, as Synapse's isFirmwareSupported does
    // (!check4DigitVersionGreater(minimum, current)).
    // 669 main.8e09720cb94da405eead.js byte 142,487: 1.1.1.3.
    // 671 main.2ceb8f5607c0e7a5901a.js: 1.5.0.0.
    // 695 main.27df2e7ddab0b6bc34cc.js byte 141,503: 1.3.0.0, with the release note
    // "Added support for new performance feature - Hyperboost"; policy
    // 6737.7ec91e479ef65c194b6c.js byte 2,386,571 applies it.
    internal static Version HyperBoostMinimumMcuFirmware(ExperimentalBladeModel model) => model.ProductId switch
    {
        0x029D => new(1, 1, 1, 3),
        0x02B7 => new(1, 3, 0, 0),
        0x029F => new(1, 5, 0, 0),
        _ => throw new CapabilityUnavailableException("HyperBoost is outside this exact experimental model's contract."),
    };

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
