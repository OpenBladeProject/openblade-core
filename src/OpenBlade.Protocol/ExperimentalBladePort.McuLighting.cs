using OpenBlade.Contracts;

namespace OpenBlade.Protocol;

internal sealed partial class ExperimentalBladePort
{
    private async Task SetReviewedMcuBuiltInEffectAsync(LightingSettings settings,
        LightingSettings? baseline, CancellationToken token, Action markSubmitted)
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
                LightingEffect.Breathing => [3, 3],
                LightingEffect.Wave => [1, 1],
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
            if (settings.Effect == LightingEffect.Off && baseline?.Effect == LightingEffect.Off)
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
        LightingEffect effect = payload.Span[2] switch
        {
            0 => LightingEffect.Off,
            1 => LightingEffect.Static,
            2 => LightingEffect.Breathing,
            3 => LightingEffect.Spectrum,
            4 => LightingEffect.Wave,
            _ => throw new IOException("The reviewed MCU returned an unsupported lighting effect."),
        };
        RecordQuery("KeyboardLighting", "Confirmed", effect.ToString());
        return (effect, payload);
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
                // Reading a complete Static baseline does not admit its setter:
                // some exact images ignore same-effect RGB changes.
                return LightingSettingsFactory.Static(primary, brightness);
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
                // Wire 2 is observed but is outside our admitted Wave setter.
                // Built-in destinations need no guessed restoration baseline.
                return payload[3] == 1
                    ? LightingSettingsFactory.Wave(LightingDirection.Rightward, brightness)
                    : null;
            default:
                throw RejectedMcuEffect(payload);
        }
    }

    private static DeviceStateContentRejectedException RejectedMcuEffect(ReadOnlySpan<byte> payload) => new(
        "The reviewed MCU lighting reply has an unknown effect or inconsistent parameters.",
        DeviceStateQuery.KeyboardLightingEffect,
        DeviceStateContentRejection.FormatPayload(payload));
}
