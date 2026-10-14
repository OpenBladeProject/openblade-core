using OpenBlade.Contracts;

namespace OpenBlade.Protocol;

internal sealed partial class ExperimentalBladePort
{
    // Whole-backlight firmware effects from the pinned 02A0 upstream path. No
    // matrix layout, Driver-mode command, renderer or setter replay is involved.
    public LightingRendererOwnershipState OwnershipState => LightingRendererOwnershipState.None;

    public async Task<LightingSettings?> ReadStateAsync(CancellationToken cancellationToken)
    {
        RequireLightingModel();
        await operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return (await ReadKeyboardLightingAsync(cancellationToken).ConfigureAwait(false)).Settings;
        }
        finally
        {
            operationGate.Release();
        }
    }

    public async Task<LogoLightingMode?> ReadLogoStateAsync(CancellationToken cancellationToken)
    {
        RequireLightingModel();
        await operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await ReadLogoAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            operationGate.Release();
        }
    }

    private void RequireLightingModel()
    {
        if (model.ProductId != 0x02A0)
        {
            throw new CapabilityUnavailableException("This model has no experimental lighting implementation.");
        }
    }

    private async Task<byte> ReadBrightnessAsync(CancellationToken token)
    {
        ReadOnlyMemory<byte> payload = await ExchangeAsync("KeyboardBrightness", 0x03, 0x83,
            [1, 5, 0], false, token).ConfigureAwait(false);
        if (!LightingBrightnessProtocol.TryDecodeQuery(payload.Span, out byte raw))
        {
            RecordQuery("KeyboardBrightness", "UnsupportedState", failure: "BrightnessSelector");
            throw new IOException("Experimental keyboard brightness returned an unexpected selector.");
        }
        RecordQuery("KeyboardBrightness", "Confirmed", $"Raw={raw};Percent={LightingBrightnessProtocol.RawToPercent(raw)}");
        return raw;
    }

    private async Task<(LightingSettings Settings, byte Raw)> ReadKeyboardLightingAsync(CancellationToken token)
    {
        byte raw = await ReadBrightnessAsync(token).ConfigureAwait(false);
        LightingEffect effect = await ReadEffectAsync(token).ConfigureAwait(false);
        byte percent = checked((byte)LightingBrightnessProtocol.RawToPercent(raw));
        if (effect != LightingEffect.Off && percent == 0)
        {
            RecordQuery("KeyboardLighting", "UnsupportedState", $"Effect={effect};Raw={raw};Percent=0",
                "UnrepresentableZeroBrightnessEffect");
            throw new IOException("The reported effect at zero brightness cannot form a confirmed lighting setting.");
        }
        LightingSettings settings = effect switch
        {
            LightingEffect.Off => LightingSettingsFactory.Off(),
            LightingEffect.Spectrum => LightingSettingsFactory.SpectrumCycling(percent),
            LightingEffect.Breathing => LightingSettingsFactory.BreathingRandom(percent),
            LightingEffect.Wave => LightingSettingsFactory.Wave(LightingDirection.Rightward, percent),
            _ => throw new IOException("Experimental keyboard effect is not representable."),
        };
        return (settings, raw);
    }

    private async Task<LightingEffect> ReadEffectAsync(CancellationToken token)
    {
        ReadOnlyMemory<byte> payload = await ExchangeAsync("KeyboardLighting", 0x0F, 0x82,
            [1, 5, 0], false, token, variableResponse: true,
            payloadValidator: static value => value.Length is >= 3 and <= RazerReport.MaximumPayloadLength).ConfigureAwait(false);
        ReadOnlySpan<byte> bytes = payload.Span;
        byte mode = bytes[2];
        int parameterStart = mode == 4 ? 4 : 3;
        bool parametersKnown = mode != 4 || bytes.Length >= 4 && bytes[3] == 1;
        if (bytes[0] != 1 || bytes[1] != 5 || !parametersKnown
            || bytes.Length > parameterStart && bytes[parameterStart..].ContainsAnyExcept((byte)0)
            || mode is not (0 or 2 or 3 or 4))
        {
            RecordQuery("KeyboardLighting", "UnsupportedState",
                $"PayloadLength={bytes.Length};Store={bytes[0]};Region={bytes[1]};Effect={mode};Parameter={(bytes.Length > 3 ? bytes[3].ToString() : "Unavailable")}",
                "UnrepresentableEffectReadback");
            throw new IOException("Experimental effect readback does not describe an admitted effect and parameters.");
        }
        LightingEffect effect = mode switch
        {
            0 => LightingEffect.Off,
            2 => LightingEffect.Breathing,
            3 => LightingEffect.Spectrum,
            4 => LightingEffect.Wave,
            _ => throw new IOException("Experimental effect readback is unsupported."),
        };
        RecordQuery("KeyboardLighting", "Confirmed", $"{effect};PayloadLength={bytes.Length};Parameter={(mode == 4 ? "Rightward/Wire1" : "None")}");
        return effect;
    }

    private async Task<LogoLightingMode> ReadLogoAsync(CancellationToken token)
    {
        ReadOnlyMemory<byte> state = await ExchangeAsync("LogoLighting", 0x03, 0x80,
            [1, 4, 0], false, token).ConfigureAwait(false);
        if (!LogoLightingProtocol.TryDecodeState(state.Span, out bool enabled))
        {
            RecordQuery("LogoLighting", "UnsupportedState", failure: "LogoPowerSelectorOrValue");
            throw new IOException("Experimental logo power readback is unsupported.");
        }
        if (!enabled)
        {
            RecordQuery("LogoLighting", "Confirmed", "Off");
            return LogoLightingMode.Off;
        }
        ReadOnlyMemory<byte> effect = await ExchangeAsync("LogoLighting", 0x03, 0x82,
            [1, 4, 0], false, token).ConfigureAwait(false);
        if (!LogoLightingProtocol.TryDecodeMode(state.Span, effect.Span, out LogoLightingMode mode))
        {
            RecordQuery("LogoLighting", "UnsupportedState", failure: "LogoEffectSelectorOrValue");
            throw new IOException("Experimental logo effect readback is unsupported.");
        }
        RecordQuery("LogoLighting", "Confirmed", mode.ToString());
        return mode;
    }

    public async Task SetLightingAsync(LightingSettings settings, CancellationToken cancellationToken)
    {
        RequireLightingModel();
        if (!LightingSettingsValidation.IsWellFormed(settings)
            || settings.Effect is not (LightingEffect.Off or LightingEffect.Spectrum or LightingEffect.Breathing or LightingEffect.Wave)
            || settings.Effect == LightingEffect.Breathing && settings.Parameters is not BreathingLightingParameters { ColorMode: BreathingColorMode.Random }
            || settings.Effect == LightingEffect.Wave && settings.Parameters is not WaveLightingParameters { Direction: LightingDirection.Rightward })
        {
            throw new CapabilityUnavailableException("Only Off, Spectrum, random Breathing and the upstream Wave direction are admitted.");
        }
        await operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        bool submitted = false;
        byte raw = checked((byte)((settings.Brightness * 255 + 50) / 100));
        string requested = $"{settings.Effect};Raw={raw};Logo={settings.LogoMode?.ToString() ?? "Unchanged"}";
        try
        {
            RequireWriteAdmission();
            (LightingSettings baseline, byte baselineRaw) = await ReadKeyboardLightingAsync(cancellationToken).ConfigureAwait(false);
            LogoLightingMode? baselineLogo = settings.LogoMode is null
                ? null : await ReadLogoAsync(cancellationToken).ConfigureAwait(false);
            policy.Record("KeyboardLighting", "Baseline", "Confirmed", requested: requested,
                confirmed: $"{baseline.Effect};Raw={baselineRaw};Logo={baselineLogo?.ToString() ?? "Unchanged"}");
            async Task ApplyBrightnessAsync()
            {
                if (baselineRaw == raw)
                {
                    return;
                }
                RequireWriteAdmission();
                await ExchangeAsync("KeyboardBrightness", 0x03, 0x03, [1, 5, raw], true, cancellationToken).ConfigureAwait(false);
                submitted = true;
                if (await ReadBrightnessAsync(cancellationToken).ConfigureAwait(false) != raw)
                {
                    throw new IOException("Independent keyboard brightness readback did not confirm the requested raw value.");
                }
            }
            // Raise brightness before selecting a lit effect; select Off before
            // lowering brightness to zero. A partial operation remains representable.
            if (settings.Effect != LightingEffect.Off)
            {
                await ApplyBrightnessAsync().ConfigureAwait(false);
            }
            if (baseline.Effect != settings.Effect)
            {
                byte[] arguments = settings.Effect switch
                {
                    LightingEffect.Off => [1, 5, 0],
                    LightingEffect.Breathing => [1, 5, 2],
                    LightingEffect.Spectrum => [1, 5, 3],
                    LightingEffect.Wave => [1, 5, 4, 1],
                    _ => throw new CapabilityUnavailableException("This effect has no experimental setter."),
                };
                RequireWriteAdmission();
                await ExchangeAsync("KeyboardLighting", 0x0F, 0x02, arguments, true, cancellationToken,
                    variableResponse: true,
                    payloadValidator: static value => value.Length <= RazerReport.MaximumPayloadLength,
                    requireEcho: false).ConfigureAwait(false);
                submitted = true;
                if (await ReadEffectAsync(cancellationToken).ConfigureAwait(false) != settings.Effect)
                {
                    throw new IOException("Independent keyboard effect readback did not confirm the requested effect.");
                }
            }
            if (settings.Effect == LightingEffect.Off)
            {
                await ApplyBrightnessAsync().ConfigureAwait(false);
            }
            if (settings.LogoMode is { } logo && logo != baselineLogo)
            {
                if (logo != LogoLightingMode.Off)
                {
                    RequireWriteAdmission();
                    await ExchangeAsync("LogoLighting", 0x03, 0x02, LogoLightingProtocol.SetEffectArguments(logo),
                        true, cancellationToken).ConfigureAwait(false);
                    submitted = true;
                }
                RequireWriteAdmission();
                await ExchangeAsync("LogoLighting", 0x03, 0x00, LogoLightingProtocol.SetStateArguments(logo),
                    true, cancellationToken).ConfigureAwait(false);
                submitted = true;
                if (await ReadLogoAsync(cancellationToken).ConfigureAwait(false) != logo)
                {
                    throw new IOException("Independent logo readback did not confirm the requested mode.");
                }
            }
            (LightingSettings confirmed, byte confirmedRaw) = await ReadKeyboardLightingAsync(cancellationToken).ConfigureAwait(false);
            if (confirmed.Effect != settings.Effect || confirmedRaw != raw)
            {
                throw new IOException("Independent final lighting readback did not confirm the requested state.");
            }
            policy.Record("KeyboardLighting", "Readback", "Confirmed", submitted, requested, requested);
        }
        catch (Exception exception)
        {
            throw MapWriteFailure("KeyboardLighting", submitted, exception);
        }
        finally
        {
            operationGate.Release();
        }
    }

    // The native step sequence for 02A0 is not documented. Absolute percentages
    // use the verified register family; native Fn keys remain firmware-owned.
    public Task<byte> StepBrightnessAsync(KeyboardBrightnessDirection direction, CancellationToken token) =>
        Task.FromException<byte>(new CapabilityUnavailableException("The native brightness step is not admitted for this experimental model."));
    public Task PulseReactiveKeyAsync(ReactiveKeyPulse pulse, CancellationToken token) => Unavailable();
    public Task PushAmbientLightingFrameAsync(AmbientLightingFrame frame, CancellationToken token) => Unavailable();
    public Task SetPlaybackMuteIndicatorAsync(bool muted, CancellationToken token) => Unavailable();
    public Task<LightingRendererOwnershipState> RelinquishRendererAsync(CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        return Task.FromResult(LightingRendererOwnershipState.None);
    }
    public Task<LightingRendererOwnershipState> InvalidateAsync() => CloseLightingAsync();
    public Task<LightingRendererOwnershipState> InvalidateForLowPowerAsync() => CloseLightingAsync();

    private async Task<LightingRendererOwnershipState> CloseLightingAsync()
    {
        // No renderer or Driver mode is owned. Closing retires the HID actor without a firmware write.
        await DisposeAsync().ConfigureAwait(false);
        return LightingRendererOwnershipState.None;
    }
}
