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

    private static byte[] CreateReviewedMcuBreathingArguments(LightingSettings settings, bool legacy)
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
        return legacy ? [3, count == 0 ? (byte)3 : count, .. colors] : [0, 5, 2, 0, 0, count, .. colors];
    }

    private static byte[] CreateReviewedMcuEffectArguments(LightingSettings settings) => settings.Effect switch
    {
        LightingEffect.Off => [0, 5, 0],
        LightingEffect.Static => [0, 5, 1, 0, 0, 1, settings.Color.Red, settings.Color.Green, settings.Color.Blue],
        LightingEffect.Breathing => CreateReviewedMcuBreathingArguments(settings, legacy: false),
        LightingEffect.Wave => [0, 5, 4, ReviewedMcuWaveDirection(settings)],
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
}
