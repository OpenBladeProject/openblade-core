using OpenBlade.Contracts;

namespace OpenBlade.Protocol;

internal sealed partial class ExperimentalBladePort
{
    private async Task SetReviewedMcuBuiltInEffectAsync(LightingSettings settings,
        LightingSettings? baseline, ReadOnlyMemory<byte> baselinePayload, CancellationToken token, Action markSubmitted)
    {
        if (UsesLegacyReviewedMcuEffectSetter(model))
        {
            // CH580/DI780/PI480's selected Synapse setter clears the custom
            // overlay before its equality shortcuts and activates saved LED 5
            // state. The caller separately restores requested active brightness.
            byte[] arguments = settings.Effect switch
            {
                LightingEffect.Off => [0],
                LightingEffect.Spectrum => [4],
                LightingEffect.Static => [6, settings.Color.Red, settings.Color.Green, settings.Color.Blue],
                LightingEffect.Breathing => CreateReviewedMcuBreathingArguments(settings, legacy: true),
                LightingEffect.Wave => [1, ReviewedMcuWaveDirection(settings)],
                _ => throw new CapabilityUnavailableException("This MCU lighting destination is not admitted."),
            };
            RequireWriteAdmission();
            await ExchangeAsync("KeyboardLighting", 0x03, 0x0A, arguments, true, token,
                variableResponse: true, payloadValidator: static value => value.Length <= RazerReport.MaximumPayloadLength,
                requireEcho: false).ConfigureAwait(false);
            markSubmitted();
        }
        else
        {
            // SO6100/KI8100/KI8110 Off returns before clearing a stale custom
            // overlay when active mode is already Off. Only this concrete MCU
            // equality case needs a different effect first. Random Breathing
            // clears that overlay unconditionally for LED 5; verify it before
            // the final Off. Any possible submission failure stops, never retries.
            if (settings.Effect == LightingEffect.Off && baseline?.Effect == LightingEffect.Off
                || NeedsReviewedMcuColorBridge(settings, baselinePayload.Span))
            {
                await SetBuiltInEffectAsync(LightingSettingsFactory.BreathingRandom(100), token).ConfigureAwait(false);
                markSubmitted();
                await ConfirmReviewedMcuEffectAsync(LightingSettingsFactory.BreathingRandom(100), token).ConfigureAwait(false);
            }
            await SetBuiltInEffectAsync(settings, token).ConfigureAwait(false);
            markSubmitted();
        }
        await ConfirmReviewedMcuEffectAsync(settings, token).ConfigureAwait(false);
    }

    private bool NeedsReviewedMcuColorBridge(LightingSettings requested, ReadOnlySpan<byte> actual)
    {
        if (actual.Length != 12) { return false; }
        // KI8100 skips Static RGB updates when already Static. KI8110 copies
        // those colors even on its equality branch; it needs no bridge.
        if (requested.Effect == LightingEffect.Static)
        {
            return model.ProductId == 0x02B8 && actual[2] == 1
                && (actual[6] != requested.Color.Red || actual[7] != requested.Color.Green
                    || actual[8] != requested.Color.Blue);
        }
        if (requested.Effect != LightingEffect.Breathing
            || requested.Parameters is not BreathingLightingParameters
            { ColorMode: BreathingColorMode.Single or BreathingColorMode.Dual }
            || actual[2] != 2)
        {
            return false;
        }
        byte[] arguments = CreateReviewedMcuBreathingArguments(requested, legacy: false);
        // All three newer images return before copying RGB when the active
        // color count is unchanged. A confirmed count-0 transition is needed
        // only for changed colors at the same count, including while dark.
        return actual[5] == arguments[5]
            && !actual.Slice(6, arguments.Length - 6).SequenceEqual(arguments.AsSpan(6));
    }

    private static byte[] CreateReviewedMcuBreathingArguments(LightingSettings settings, bool legacy, byte region = 5)
    {
        byte count;
        byte[] colors;
        switch (settings.Parameters)
        {
            case null:
            case BreathingLightingParameters { ColorMode: BreathingColorMode.Random }:
                count = 0;
                colors = [];
                break;
            case BreathingLightingParameters { ColorMode: BreathingColorMode.Single, PrimaryColor: { } primary }:
                count = 1;
                colors = [primary.Red, primary.Green, primary.Blue];
                break;
            case BreathingLightingParameters { ColorMode: BreathingColorMode.Dual, PrimaryColor: { } primary, SecondaryColor: { } secondary }:
                count = 2;
                colors = [primary.Red, primary.Green, primary.Blue, secondary.Red, secondary.Green, secondary.Blue];
                break;
            default:
                throw new CapabilityUnavailableException("The requested MCU Breathing colors are not admitted.");
        }
        return legacy ? [3, count == 0 ? (byte)3 : count, .. colors] : [0, region, 2, 0, 0, count, .. colors];
    }

    private static byte[] CreateReviewedMcuEffectArguments(LightingSettings settings, byte region = 5) => settings.Effect switch
    {
        LightingEffect.Off => [0, region, 0],
        LightingEffect.Static => [0, region, 1, 0, 0, 1, settings.Color.Red, settings.Color.Green, settings.Color.Blue],
        LightingEffect.Breathing => CreateReviewedMcuBreathingArguments(settings, legacy: false, region),
        LightingEffect.Wave => [0, region, 4, ReviewedMcuWaveDirection(settings)],
        _ => throw new CapabilityUnavailableException("This MCU lighting destination is not admitted."),
    };

    // Each selected product's own vendor LeftRight mapping uses left=1/right=2.
    // The exact MCU Wave consumers independently implement both raw branches.
    private static byte ReviewedMcuWaveDirection(LightingSettings settings) =>
        settings.Parameters is WaveLightingParameters { Direction: LightingDirection.Leftward } ? (byte)1 : (byte)2;

    private async Task ConfirmReviewedMcuEffectAsync(LightingSettings requested, CancellationToken token)
    {
        (_, ReadOnlyMemory<byte> payload) = await ReadEffectAsync(token).ConfigureAwait(false);
        LightingSettings? actual = DecodeReviewedMcuEffect(payload.Span, requested.Brightness);
        if (actual is null || !KeyboardSettingsMatch(actual, requested))
        {
            throw new IOException("Independent active MCU lighting readback did not confirm the requested effect and parameters.");
        }
    }

    private async Task<(LightingEffect Effect, ReadOnlyMemory<byte> Payload)> ReadReviewedMcuEffectAsync(
        CancellationToken token)
    {
        ReadOnlyMemory<byte> payload = await ReadReviewedMcuEffectPayloadAsync(token).ConfigureAwait(false);
        LightingEffect effect = payload.Span[2] switch
        {
            0 => LightingEffect.Off,
            1 => LightingEffect.Static,
            2 => LightingEffect.Breathing,
            3 => LightingEffect.Spectrum,
            4 => LightingEffect.Wave,
            _ => throw new IOException("The reviewed MCU does not report a built-in effect."),
        };
        RecordQuery("KeyboardLighting", "Confirmed", effect.ToString());
        return (effect, payload);
    }

    private async Task<ReadOnlyMemory<byte>> ReadReviewedMcuEffectPayloadAsync(CancellationToken token)
    {
        // These six exact MCU handlers clear payload 2..11 before publishing
        // effect, Wave direction/speed, color count and both RGB colors. The
        // fixed 90-byte reply preserves the requested DataSize. A compact GET
        // cannot distinguish random breathing from single/dual-color breathing.
        // See the separately reproduced MCU review, not another model's offsets.
        ReadOnlyMemory<byte> payload = await ExchangeAsync("KeyboardLighting", 0x0F, 0x82,
            [0, 5, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0], false, token,
            variableResponse: true, payloadValidator: static value => value.Length == 12)
            .ConfigureAwait(false);
        // Any answer from this register ends a run of refused ownership reads.
        ResetReviewedMcuSelectionDeferral();
        try
        {
            _ = DecodeReviewedMcuEffect(payload.Span, 100);
        }
        catch (DeviceStateContentRejectedException)
        {
            session.RetainQueryFailure(0x0F, 0x82, FirmwareQueryOutcome.ContentRejected);
            RecordQuery("KeyboardLighting", "UnsupportedState", failure: "McuEffectParameters");
            throw;
        }
        return payload;
    }

    private async Task<(LightingSettings? Settings, byte RawBrightness, ReadOnlyMemory<byte> EffectPayload)>
        ReadReviewedMcuKeyboardStateAsync(byte raw, bool allowConfirmedDark, CancellationToken token)
    {
        ReadOnlyMemory<byte> payload = await ReadReviewedMcuEffectPayloadAsync(token).ConfigureAwait(false);
        byte percent = checked((byte)LightingBrightnessProtocol.RawToPercent(raw));
        if (payload.Span[2] == 8)
        {
            // Custom pixels remain unknown at every brightness, including zero.
            // Returning null preserves independent fields without inventing Off.
            RecordQuery("KeyboardLighting", "Unavailable", failure: "CustomPixelsReadbackUnavailable");
            return (null, raw, payload);
        }
        if (payload.Span[2] != 0 && percent == 0)
        {
            if (raw == 0)
            {
                // Independent brightness can legitimately darken a selected
                // built-in effect. Ordinary status reads must preserve that
                // confirmed field without inventing Off or a zero-brightness
                // complete setting. MCU models admit no host-frame baseline,
                // so this does not depend on allowConfirmedDark's frame gate.
                RecordQuery("KeyboardLighting", "Unavailable", "ConfirmedDarkEffect");
                return (null, raw, payload);
            }
            session.RetainQueryFailure(0x03, 0x83, FirmwareQueryOutcome.ContentRejected);
            session.RetainQueryFailure(0x0F, 0x82, FirmwareQueryOutcome.ContentRejected);
            throw new DeviceStateContentRejectedException(
                "The reported effect at zero brightness cannot form a confirmed lighting setting.",
                DeviceStateQuery.KeyboardLightingCombination,
                DeviceStateContentRejection.FormatPayloads([0, 5, raw], payload.Span));
        }
        LightingSettings? observed = DecodeReviewedMcuEffect(payload.Span, percent);
        // Mode 8 identifies custom output, but never its pixels. A direct
        // built-in destination clears it using the reviewed command contract;
        // no guessed restoration baseline is needed or retained.
        RecordQuery("KeyboardLighting", observed is null ? "Unavailable" : "Confirmed",
            observed?.Effect.ToString(), observed is null ? "EffectParametersReadbackUnavailable" : null);
        return (observed, raw, payload);
    }

    private LightingSettings? DecodeReviewedMcuEffect(ReadOnlySpan<byte> payload, byte brightness)
    {
        if (payload.Length != 12 || payload[0] != 0 || payload[1] != 5)
        {
            throw RejectedMcuEffect(payload);
        }
        bool unusedFlags = payload[3] == 0 && payload[4] == 0;
        RgbColor primary = new(payload[6], payload[7], payload[8]);
        RgbColor secondary = new(payload[9], payload[10], payload[11]);
        switch (payload[2])
        {
            case 0 when payload[3..].ContainsAnyExcept((byte)0) == false:
                return LightingSettingsFactory.Off();
            case 1 when unusedFlags && payload[5] == 1 && payload[9..].ContainsAnyExcept((byte)0) == false:
                // CH580/SO6100 can render a hidden palette instead of stored
                // RGB. Their getter does not expose that selector.
                return model.ProductId is 0x028A or 0x02B7
                    ? null : LightingSettingsFactory.Static(primary, brightness);
            case 2 when unusedFlags && payload[5] == 0 && payload[6..].ContainsAnyExcept((byte)0) == false:
                return LightingSettingsFactory.BreathingRandom(brightness);
            case 2 when unusedFlags && payload[5] == 1 && payload[9..].ContainsAnyExcept((byte)0) == false:
                return LightingSettingsFactory.BreathingSingle(primary, brightness);
            case 2 when unusedFlags && payload[5] == 2:
                return LightingSettingsFactory.BreathingDual(primary, secondary, brightness);
            case 3 when payload[3..].ContainsAnyExcept((byte)0) == false:
                // Newer firmware has a separate, unreadable Spectrum policy
                // and palette selector. Its mode alone is not a complete setting.
                return UsesLegacyReviewedMcuEffectSetter(model)
                    ? LightingSettingsFactory.SpectrumCycling(brightness) : null;
            case 4 when payload[3] is 1 or 2 && payload[4] == 40
                && payload[5..].ContainsAnyExcept((byte)0) == false:
                return LightingSettingsFactory.Wave(payload[3] == 1
                    ? LightingDirection.Leftward : LightingDirection.Rightward, brightness);
            case 8 when payload[3..].ContainsAnyExcept((byte)0) == false:
                return null;
            default:
                throw RejectedMcuEffect(payload);
        }
    }

    private static DeviceStateContentRejectedException RejectedMcuEffect(ReadOnlySpan<byte> payload) => new(
        "The reviewed MCU lighting reply has an unknown effect or inconsistent parameters.",
        DeviceStateQuery.KeyboardLightingEffect,
        DeviceStateContentRejection.FormatPayload(payload));

    // LED38, the RZ09-0529 Vapor Chamber. Only Synapse product 711 wires
    // hasVCLed. KI8110 1.07.00 (class-0F handler 0x9400, get table 0x9524,
    // set table 0x96B2) keeps its bank-0 record at 0x100015D6 + 0x40 * 2.
    // See docs/rz09-0529-lighting-expansion.md.
    private const byte VaporChamberRegion = 38;

    private static bool SupportsRz090529VaporChamberFrames(ExperimentalBladeModel model) =>
        model.ProductId == 0x02C7;

    private ExperimentalVaporChamberAvailability? vaporChamberAvailability;
    private ExperimentalVaporChamberAvailability.Key vaporChamberAvailabilityKey;
    private bool vaporChamberReadFailed;

    // The factory binds its identity-scoped result so every later session for the
    // same device reuses a completed failure. A directly constructed port keeps
    // only its own result.
    internal void BindVaporChamberAvailability(
        ExperimentalVaporChamberAvailability availability, ExperimentalVaporChamberAvailability.Key key)
    {
        vaporChamberAvailability = availability;
        vaporChamberAvailabilityKey = key;
    }

    private bool VaporChamberReadAvailable => !vaporChamberReadFailed
        && vaporChamberAvailability?.Get(vaporChamberAvailabilityKey) != false;

    private void SettleVaporChamberAvailability(bool available)
    {
        vaporChamberReadFailed |= !available;
        vaporChamberAvailability?.Settle(vaporChamberAvailabilityKey, available);
    }

    // Only a GET that the MCU could have received settles LED38 availability. A
    // shared-gate refusal, queued cancellation, exhausted transaction IDs or an
    // already unusable session never sent it, so the next open probes again.
    private static bool VaporChamberReadReachedDevice(IOException exception) =>
        exception is not NativeDeviceReadException native
        || native.Diagnostic?.Phase is not (HidSessionFailurePhase.SharedGateAdmission or HidSessionFailurePhase.Guard)
            && native.Failure is not (HidSessionFailureKind.QueuedCanceled or HidSessionFailureKind.Canceled
                or HidSessionFailureKind.Exhausted or HidSessionFailureKind.Disposed
                or HidSessionFailureKind.Poisoned);

    // Optional open-time probe, run once for each admitted identity before the port
    // is published. Shipped keyboard effects never depend on it: a rejected GET
    // leaves later frames keyboard-only, and a GET that poisons this session is
    // rethrown so the factory drains it and opens one fresh session without it.
    internal async Task InitializeRz090529VaporChamberAvailabilityAsync(CancellationToken cancellationToken)
    {
        if (!SupportsRz090529VaporChamberFrames(model) || vaporChamberAvailability is null)
        {
            return;
        }
        await operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (vaporChamberAvailability.Get(vaporChamberAvailabilityKey) is not null)
            {
                return;
            }
            if (!exactHostStillPresent())
            {
                throw new CapabilityUnavailableException("The exact Blade 18 is no longer verified.");
            }
            _ = await ReadReviewedVaporChamberEffectAsync(cancellationToken).ConfigureAwait(false);
            SettleVaporChamberAvailability(true);
        }
        catch (IOException exception)
        {
            // A wait canceled at the shared command gate is cancellation, as for
            // the Blade 18 layout probe.
            cancellationToken.ThrowIfCancellationRequested();
            bool answered = VaporChamberReadReachedDevice(exception);
            if (answered)
            {
                SettleVaporChamberAvailability(false);
            }
            if (session.Disposition != RazerCommandSessionDisposition.Healthy)
            {
                throw;
            }
            RecordQuery("VaporChamberEffect", "Unavailable",
                failure: answered ? "VaporChamberFramesUnavailable" : "VaporChamberProbeDeferred");
        }
        finally
        {
            operationGate.Release();
        }
    }

    // Reads LED38 for a host-frame decision. When OpenBlade selected LED38 the read
    // is required to return it. Otherwise LED38 is optional: a failed read on a
    // usable session leaves this owner keyboard-only, and a poisoned session still
    // fails the request before any write. Only a device answer keeps later
    // requests from reading again. A read that never reached the MCU (shared-gate
    // refusal, queued cancellation) leaves just this request keyboard-only, so a
    // running renderer is replaced normally instead of being faulted.
    private async Task<(LightingSettings? Settings, byte Mode)?> ReadVaporChamberForFramesAsync(
        bool required, CancellationToken token)
    {
        try
        {
            return await ReadReviewedVaporChamberEffectAsync(token).ConfigureAwait(false);
        }
        catch (IOException exception) when (!token.IsCancellationRequested)
        {
            if (required)
            {
                throw;
            }
            bool answered = VaporChamberReadReachedDevice(exception);
            if (answered)
            {
                SettleVaporChamberAvailability(false);
            }
            if (session.Disposition != RazerCommandSessionDisposition.Healthy)
            {
                throw;
            }
            RecordQuery("VaporChamberEffect", "Unavailable",
                failure: answered ? "VaporChamberFramesUnavailable" : "VaporChamberReadDeferred");
            return null;
        }
    }

    // Write-ahead record that LED38 may be selected, saved before [0,38,8] can be
    // submitted. Only this record lets a later restore write LED38.
    private void RecordRz090529VaporChamberSelection()
    {
        ExperimentalLightingBaseline baseline = lightingOwnership.Baseline
            ?? throw new IOException("The pre-frame lighting baseline is unavailable.");
        if (baseline.VaporChamber is not null)
        {
            return;
        }
        ExperimentalLightingBaseline selected = baseline with
        {
            VaporChamber = lightingOwnership.VaporChamberDestination
                ?? throw new IOException("The Vapor Chamber restore destination is unavailable."),
        };
        lightingOwnership.Journal?.ReplacePendingVaporChamber(selected);
        lightingOwnership.Baseline = selected;
    }

    // LED38 is no longer OpenBlade's: it was restored, or another controller took it.
    private void ReleaseRz090529VaporChamberSelection()
    {
        if (lightingOwnership.Baseline is not { VaporChamber: not null } baseline)
        {
            return;
        }
        ExperimentalLightingBaseline released = baseline with { VaporChamber = null };
        lightingOwnership.Journal?.ReplacePendingVaporChamber(released);
        lightingOwnership.Baseline = released;
        lightingOwnership.VaporChamberFrames = false;
    }

    private async Task<(LightingSettings? Settings, byte Mode)> ReadReviewedVaporChamberEffectAsync(
        CancellationToken token)
    {
        ReadOnlyMemory<byte> payload = await ExchangeAsync("VaporChamberEffect", 0x0F, 0x82,
            [0, VaporChamberRegion, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0], false, token,
            variableResponse: true, payloadValidator: static value => value.Length == 12)
            .ConfigureAwait(false);
        LightingSettings? settings = DecodeReviewedVaporChamberEffect(payload.Span);
        RecordQuery("VaporChamberEffect", settings is null ? "Unavailable" : "Confirmed",
            settings?.Effect.ToString() ?? $"Mode{payload.Span[2]}",
            settings is null ? "EffectParametersReadbackUnavailable" : null);
        return (settings, payload.Span[2]);
    }

    // Returns null for a record that cannot be restored: Spectrum's hidden
    // gate/palette, Starlight's unreturned rec+0xA, mode 9's external
    // parameters, custom pixels and any incomplete record. A reply that does
    // not echo region 38 is a read failure, not an unrestorable record.
    private static LightingSettings? DecodeReviewedVaporChamberEffect(ReadOnlySpan<byte> payload)
    {
        if (payload.Length != 12 || payload[0] != 0 || payload[1] != VaporChamberRegion)
        {
            throw new IOException("The Vapor Chamber effect reply did not match the region 38 contract.");
        }
        // No VC setter changes record brightness; the restore destination is
        // the effect and its parameters at full nominal brightness.
        const byte brightness = 100;
        bool unusedFlags = payload[3] == 0 && payload[4] == 0;
        RgbColor primary = new(payload[6], payload[7], payload[8]);
        RgbColor secondary = new(payload[9], payload[10], payload[11]);
        return payload[2] switch
        {
            0 when !payload[3..].ContainsAnyExcept((byte)0) => LightingSettingsFactory.Off(),
            1 when unusedFlags && payload[5] == 1 && !payload[9..].ContainsAnyExcept((byte)0)
                => LightingSettingsFactory.Static(primary, brightness),
            2 when unusedFlags && payload[5] == 0 && !payload[6..].ContainsAnyExcept((byte)0)
                => LightingSettingsFactory.BreathingRandom(brightness),
            2 when unusedFlags && payload[5] == 1 && !payload[9..].ContainsAnyExcept((byte)0)
                => LightingSettingsFactory.BreathingSingle(primary, brightness),
            2 when unusedFlags && payload[5] == 2
                => LightingSettingsFactory.BreathingDual(primary, secondary, brightness),
            4 when payload[3] is 1 or 2 && payload[4] == 40 && !payload[5..].ContainsAnyExcept((byte)0)
                => LightingSettingsFactory.Wave(payload[3] == 1
                    ? LightingDirection.Leftward : LightingDirection.Rightward, brightness),
            _ => null,
        };
    }

    // Restore LED38 only when the durable record says OpenBlade may have selected
    // it and it still reads custom mode 8. From mode 8 every restorable setter
    // clears the custom-ownership flag and bypasses its same-mode shortcuts
    // (KI8110 1.07.00 set table 0x96B2), so no bridge effect is needed. Any other
    // mode means another controller replaced the frame; leave it unchanged. The
    // selection record is removed only after a confirmed restore or a skip.
    private async Task RestoreRz090529VaporChamberAsync(ExperimentalLightingBaseline baseline,
        byte? currentMode, CancellationToken token, Action markSubmitted)
    {
        // Only RZ09-0529 has a reviewed LED38 contract; the journal rejects an
        // LED38 record for any other product.
        if (!SupportsRz090529VaporChamberFrames(model) || baseline.VaporChamber is not { } destination)
        {
            return;
        }
        byte mode = currentMode ?? (await ReadReviewedVaporChamberEffectAsync(token).ConfigureAwait(false)).Mode;
        if (mode != 8)
        {
            policy.Record("VaporChamberEffect", "Restore", "Skipped",
                requested: destination.Effect.ToString(), confirmed: $"Mode{mode}",
                failure: "ExternallyChanged");
            ReleaseRz090529VaporChamberSelection();
            return;
        }
        RequireWriteAdmission();
        await ExchangeAsync("VaporChamberEffect", 0x0F, 0x02,
            CreateReviewedMcuEffectArguments(destination, VaporChamberRegion), true, token,
            variableResponse: true, payloadValidator: static value => value.Length <= RazerReport.MaximumPayloadLength,
            requireEcho: false).ConfigureAwait(false);
        markSubmitted();
        (LightingSettings? actual, _) = await ReadReviewedVaporChamberEffectAsync(token).ConfigureAwait(false);
        if (actual is null || !KeyboardSettingsMatch(actual, destination))
        {
            throw new IOException("Independent Vapor Chamber readback did not confirm restoration.");
        }
        policy.Record("VaporChamberEffect", "Restore", "Confirmed", true,
            requested: destination.Effect.ToString(), confirmed: actual.Effect.ToString());
        ReleaseRz090529VaporChamberSelection();
    }
}
