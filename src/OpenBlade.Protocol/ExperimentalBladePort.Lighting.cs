using OpenBlade.Contracts;

namespace OpenBlade.Protocol;

internal sealed record ExperimentalLightingBaseline(
    LightingSettings Settings,
    byte RawBrightness);

internal sealed class ExperimentalLightingOwnershipState
{
    internal int Ownership;
    internal ExperimentalLightingBaseline? Baseline;
    internal LightingSettings? Intent;
}

internal sealed partial class ExperimentalBladePort
{
    public LightingRendererOwnershipState OwnershipState =>
        (LightingRendererOwnershipState)Volatile.Read(ref lightingOwnership.Ownership);

    internal static bool SupportsExperimentalLighting(ExperimentalBladeModel model) =>
        model.ProductId is 0x029F or 0x029D or 0x02C5 or 0x0255 or 0x02A0;

    internal static bool SupportsExperimentalKeyboardBrightness(
        ExperimentalBladeModel model,
        bool enabled) => enabled && SupportsExperimentalLighting(model);

    internal static IReadOnlySet<LightingEffect> CreateExperimentalLightingEffects(
        ExperimentalBladeModel model,
        bool enabled)
    {
        if (!enabled || !SupportsExperimentalLighting(model))
        {
            return new HashSet<LightingEffect>();
        }

        var effects = new HashSet<LightingEffect>
        {
            LightingEffect.Off,
            LightingEffect.Spectrum,
            LightingEffect.Breathing,
        };
        if (model.ProductId != 0x0255)
        {
            effects.Add(LightingEffect.Wave);
        }
        if (model.ProductId is 0x029F or 0x0255 or 0x02A0)
        {
            effects.Add(LightingEffect.Static);
        }
        if (model.ProductId == 0x029F)
        {
            effects.Add(LightingEffect.AmbientAwareness);
            effects.Add(LightingEffect.AudioMeter);
        }
        return effects;
    }

    internal static IReadOnlySet<LightingEffect> CreateObservedExperimentalLightingEffects(
        ExperimentalBladeModel model)
    {
        var effects = new HashSet<LightingEffect>(CreateExperimentalLightingEffects(model, enabled: true));
        if (model.ProductId == 0x02A0)
        {
            // Product 672's pinned quick-effect catalog is discovery evidence,
            // not admission for spatial frames or unimplemented firmware commands.
            effects.UnionWith([
                LightingEffect.AmbientAwareness, LightingEffect.AudioMeter,
                LightingEffect.Fire, LightingEffect.Reactive, LightingEffect.Ripple,
                LightingEffect.Starlight, LightingEffect.Wheel, LightingEffect.Tidal]);
        }
        return effects;
    }

    internal static IReadOnlySet<LogoLightingMode> CreateExperimentalLogoModes(
        ExperimentalBladeModel model,
        bool enabled) => enabled && SupportsExperimentalLogo(model)
            ? new HashSet<LogoLightingMode>(Enum.GetValues<LogoLightingMode>())
            : new HashSet<LogoLightingMode>();

    internal static bool SupportsExperimentalBreathingColorSelection(
        ExperimentalBladeModel model) => false;

    internal static bool SupportsExperimentalWaveDirectionSelection(
        ExperimentalBladeModel model) => false;

    internal static bool SupportsExperimentalUniformFrames(
        ExperimentalBladeModel model,
        bool enabled) => enabled && model.ProductId == 0x029F;

    internal static IReadOnlySet<BreathingColorMode> CreateExperimentalBreathingColorModes(
        ExperimentalBladeModel model,
        bool enabled) => !enabled
            ? new HashSet<BreathingColorMode>()
            : model.ProductId == 0x0255
                ? new HashSet<BreathingColorMode>
            {
                BreathingColorMode.Random,
                BreathingColorMode.Single,
            }
                : new HashSet<BreathingColorMode> { BreathingColorMode.Random };

    private static bool SupportsExperimentalLogo(ExperimentalBladeModel model) =>
        model.ProductId is 0x029F or 0x029D or 0x02C5 or 0x02A0;

    private static bool SupportsUniformFrame(ExperimentalBladeModel model) =>
        model.ProductId is 0x029F or 0x02A0;

    public async Task<LightingSettings?> ReadStateAsync(CancellationToken cancellationToken)
    {
        RequireLightingModel();
        await operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (IsOwnedFrameOwnership(OwnershipState)
                || OwnershipState == LightingRendererOwnershipState.Faulted)
            {
                RecordQuery("KeyboardLighting", "Unavailable",
                    confirmed: lightingOwnership.Intent is null ? null : "OwnedFrameIntent",
                    failure: "FrameColorReadbackUnavailable");
                return null;
            }
            return (await ReadKeyboardLightingAsync(cancellationToken).ConfigureAwait(false)).Settings;
        }
        finally
        {
            operationGate.Release();
        }
    }

    public async Task<LogoLightingMode?> ReadLogoStateAsync(CancellationToken cancellationToken)
    {
        RequireLogoModel();
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

    internal async Task<(LightingSettings? Lighting, LogoLightingMode? Logo)>
        ReadRecoveryLightingStateAsync(CancellationToken cancellationToken)
    {
        if (!SupportsExperimentalLighting(model))
        {
            return (null, null);
        }

        // A volatile 6-by-16 frame has no getter. A newly opened recovery port
        // cannot distinguish it from the persisted built-in effect underneath.
        LightingSettings? lighting = SupportsUniformFrame(model)
            ? null
            : (await ReadKeyboardLightingAsync(cancellationToken).ConfigureAwait(false)).Settings;
        LogoLightingMode? logo = SupportsExperimentalLogo(model)
            ? await ReadLogoAsync(cancellationToken).ConfigureAwait(false)
            : null;
        return (lighting, logo);
    }

    private void RequireLightingModel()
    {
        if (!SupportsExperimentalLighting(model))
        {
            throw new CapabilityUnavailableException(
                "This model has no experimental lighting implementation.");
        }
    }

    private void RequireLogoModel()
    {
        RequireLightingModel();
        if (!SupportsExperimentalLogo(model))
        {
            throw new CapabilityUnavailableException(
                "This model has no experimental logo lighting implementation.");
        }
    }

    private async Task<byte> ReadBrightnessAsync(CancellationToken token)
    {
        ReadOnlyMemory<byte> payload = await ExchangeAsync("KeyboardBrightness", 0x03, 0x83,
            [1, 5, 0], false, token).ConfigureAwait(false);
        if (!LightingBrightnessProtocol.TryDecodeQuery(payload.Span, out byte raw))
        {
            session.RetainQueryFailure(0x03, 0x83, FirmwareQueryOutcome.ContentRejected);
            RecordQuery("KeyboardBrightness", "UnsupportedState", failure: "BrightnessSelector");
            throw new IOException("Experimental keyboard brightness returned an unexpected selector.");
        }
        RecordQuery("KeyboardBrightness", "Confirmed",
            $"Raw={raw};Percent={LightingBrightnessProtocol.RawToPercent(raw)}");
        return raw;
    }

    private async Task<(LightingSettings Settings, byte Raw)> ReadKeyboardLightingAsync(
        CancellationToken token)
    {
        byte raw = await ReadBrightnessAsync(token).ConfigureAwait(false);
        (LightingEffect effect, ReadOnlyMemory<byte> effectPayload) =
            await ReadEffectAsync(token).ConfigureAwait(false);
        byte percent = checked((byte)LightingBrightnessProtocol.RawToPercent(raw));
        if (effect != LightingEffect.Off && percent == 0)
        {
            session.RetainQueryFailure(0x03, 0x83, FirmwareQueryOutcome.ContentRejected);
            session.RetainQueryFailure(0x0F, 0x82, FirmwareQueryOutcome.ContentRejected);
            RecordQuery("KeyboardLighting", "UnsupportedState", $"Effect={effect};Raw={raw};Percent=0",
                "UnrepresentableZeroBrightnessEffect");
            throw new DeviceStateContentRejectedException(
                "The reported effect at zero brightness cannot form a confirmed lighting setting.",
                DeviceStateQuery.KeyboardLightingCombination,
                DeviceStateContentRejection.FormatPayloads([1, 5, raw], effectPayload.Span));
        }

        LightingSettings settings = effect switch
        {
            LightingEffect.Off => LightingSettingsFactory.Off(),
            LightingEffect.Static => LightingSettingsFactory.Static(
                await ReadSingleZoneStaticColorAsync(token).ConfigureAwait(false), percent),
            LightingEffect.Spectrum => LightingSettingsFactory.SpectrumCycling(percent),
            LightingEffect.Breathing => await ReadBreathingSettingsAsync(percent, token)
                .ConfigureAwait(false),
            LightingEffect.Wave => LightingSettingsFactory.Wave(
                LightingDirection.Rightward, percent),
            _ => throw new IOException("Experimental keyboard effect is not representable."),
        };
        return (settings, raw);
    }

    private async Task<(LightingEffect Effect, ReadOnlyMemory<byte> Payload)> ReadEffectAsync(
        CancellationToken token)
    {
        ReadOnlyMemory<byte> payload = await ExchangeAsync("KeyboardLighting", 0x0F, 0x82,
            [1, 5, 0], false, token, variableResponse: true,
            payloadValidator: static value => value.Length is >= 3
                and <= RazerReport.MaximumPayloadLength).ConfigureAwait(false);
        ReadOnlySpan<byte> bytes = payload.Span;
        if (bytes[0] != 1 || bytes[1] != 5)
        {
            session.RetainQueryFailure(0x0F, 0x82, FirmwareQueryOutcome.ContentRejected);
            RecordQuery("KeyboardLighting", "InconsistentState", failure: "EffectSelector");
            throw new IOException("Experimental keyboard effect returned an unexpected selector.");
        }

        byte mode = bytes[2];
        int parameterStart = mode == 4 ? 4 : 3;
        bool parametersKnown = mode != 4 || bytes.Length >= 4 && bytes[3] == 1;
        bool supportedMode = mode is 0 or 2 or 3
            || mode == 4 && model.ProductId != 0x0255
            || mode == 1 && model.ProductId == 0x0255;
        if (!parametersKnown || !supportedMode
            || bytes.Length > parameterStart
                && bytes[parameterStart..].ContainsAnyExcept((byte)0))
        {
            RecordQuery("KeyboardLighting", "UnsupportedState",
                $"PayloadLength={bytes.Length};Store={bytes[0]};Region={bytes[1]};Effect={mode};Parameter={(bytes.Length > 3 ? bytes[3].ToString() : "Unavailable")}",
                "UnrepresentableEffectReadback");
            session.RetainQueryFailure(0x0F, 0x82, FirmwareQueryOutcome.ContentRejected);
            throw new DeviceStateContentRejectedException(
                "Experimental effect readback does not describe an admitted effect and parameters.",
                DeviceStateQuery.KeyboardLightingEffect,
                DeviceStateContentRejection.FormatPayload(bytes));
        }

        LightingEffect effect = mode switch
        {
            0 => LightingEffect.Off,
            1 => LightingEffect.Static,
            2 => LightingEffect.Breathing,
            3 => LightingEffect.Spectrum,
            4 => LightingEffect.Wave,
            _ => throw new IOException("Experimental effect readback is unsupported."),
        };
        RecordQuery("KeyboardLighting", "Confirmed",
            $"{effect};PayloadLength={bytes.Length};Parameter={(mode == 4 ? "Rightward/Wire1" : "None")}");
        return (effect, payload);
    }

    private async Task<RgbColor> ReadSingleZoneStaticColorAsync(CancellationToken token)
    {
        if (model.ProductId != 0x0255)
        {
            throw new IOException("A readable single-zone static color is not admitted for this model.");
        }
        ReadOnlyMemory<byte> payload = await ReadStandardEffectAsync(token).ConfigureAwait(false);
        ReadOnlySpan<byte> bytes = payload.Span;
        if (bytes[0] != 6 || bytes[4] != 0)
        {
            RecordQuery("KeyboardStaticColor", "UnsupportedState",
                failure: "StandardStaticColorReadback");
            throw new DeviceStateContentRejectedException(
                "Experimental static-color readback is unsupported.",
                DeviceStateQuery.KeyboardLightingEffect,
                DeviceStateContentRejection.FormatPayload(bytes));
        }
        RgbColor color = new(bytes[1], bytes[2], bytes[3]);
        RecordQuery("KeyboardStaticColor", "Confirmed",
            $"RGB={color.Red:X2}{color.Green:X2}{color.Blue:X2}");
        return color;
    }

    private async Task<LightingSettings> ReadBreathingSettingsAsync(
        byte brightness,
        CancellationToken token)
    {
        if (model.ProductId != 0x0255)
        {
            return LightingSettingsFactory.BreathingRandom(brightness);
        }

        ReadOnlyMemory<byte> payload = await ReadStandardEffectAsync(token).ConfigureAwait(false);
        ReadOnlySpan<byte> bytes = payload.Span;
        if (bytes[0] != 3)
        {
            if (bytes.ContainsAnyExcept((byte)0))
            {
                RecordQuery("KeyboardBreathingColor", "UnsupportedState",
                    failure: "UnsupportedBreathingEffectPayload");
                throw new DeviceStateContentRejectedException(
                    "Experimental breathing-color readback is unsupported.",
                    DeviceStateQuery.KeyboardLightingEffect,
                    DeviceStateContentRejection.FormatPayload(bytes));
            }
            RecordQuery("KeyboardBreathingColor", "Confirmed", "Random");
            return LightingSettingsFactory.BreathingRandom(brightness);
        }
        if (bytes[1] != 1)
        {
            RecordQuery("KeyboardBreathingColor", "UnsupportedState",
                failure: "UnsupportedBreathingColorMode");
            throw new DeviceStateContentRejectedException(
                "Experimental breathing-color readback is unsupported.",
                DeviceStateQuery.KeyboardLightingEffect,
                DeviceStateContentRejection.FormatPayload(bytes));
        }
        RgbColor color = new(bytes[2], bytes[3], bytes[4]);
        RecordQuery("KeyboardBreathingColor", "Confirmed",
            $"Single;RGB={color.Red:X2}{color.Green:X2}{color.Blue:X2}");
        return LightingSettingsFactory.BreathingSingle(color, brightness);
    }

    private Task<ReadOnlyMemory<byte>> ReadStandardEffectAsync(CancellationToken token) =>
        ExchangeAsync("KeyboardStandardEffect", 0x03, 0x8A,
            [0, 0, 0, 0, 0], false, token);

    private async Task<LogoLightingMode> ReadLogoAsync(CancellationToken token)
    {
        RequireLogoModel();
        ReadOnlyMemory<byte> state = await ExchangeAsync("LogoLighting", 0x03, 0x80,
            [1, 4, 0], false, token).ConfigureAwait(false);
        if (state.Span[0] != 1 || state.Span[1] != 4)
        {
            session.RetainQueryFailure(0x03, 0x80, FirmwareQueryOutcome.ContentRejected);
            RecordQuery("LogoLighting", "InconsistentState", failure: "LogoPowerSelector");
            throw new IOException("Experimental logo power returned an unexpected selector.");
        }
        if (!LogoLightingProtocol.TryDecodeState(state.Span, out bool enabled))
        {
            session.RetainQueryFailure(0x03, 0x80, FirmwareQueryOutcome.ContentRejected);
            RecordQuery("LogoLighting", "UnsupportedState", failure: "LogoPowerSelectorOrValue");
            throw new DeviceStateContentRejectedException(
                "Experimental logo power readback is unsupported.",
                DeviceStateQuery.LogoLightingState,
                DeviceStateContentRejection.FormatPayload(state.Span));
        }
        if (!enabled)
        {
            RecordQuery("LogoLighting", "Confirmed", "Off");
            return LogoLightingMode.Off;
        }

        ReadOnlyMemory<byte> effect = await ExchangeAsync("LogoLighting", 0x03, 0x82,
            [1, 4, 0], false, token).ConfigureAwait(false);
        if (effect.Span[0] != 1 || effect.Span[1] != 4)
        {
            session.RetainQueryFailure(0x03, 0x82, FirmwareQueryOutcome.ContentRejected);
            RecordQuery("LogoLighting", "InconsistentState", failure: "LogoEffectSelector");
            throw new IOException("Experimental logo effect returned an unexpected selector.");
        }
        if (!LogoLightingProtocol.TryDecodeMode(state.Span, effect.Span, out LogoLightingMode mode))
        {
            session.RetainQueryFailure(0x03, 0x80, FirmwareQueryOutcome.ContentRejected);
            session.RetainQueryFailure(0x03, 0x82, FirmwareQueryOutcome.ContentRejected);
            RecordQuery("LogoLighting", "UnsupportedState", failure: "LogoEffectSelectorOrValue");
            throw new DeviceStateContentRejectedException(
                "Experimental logo effect readback is unsupported.",
                DeviceStateQuery.LogoLightingEffect,
                DeviceStateContentRejection.FormatPayload(effect.Span));
        }
        RecordQuery("LogoLighting", "Confirmed", mode.ToString());
        return mode;
    }

    public async Task SetLightingAsync(
        LightingSettings settings,
        CancellationToken cancellationToken)
    {
        RequireLightingModel();
        if (!IsSupportedLightingSetting(settings))
        {
            throw new CapabilityUnavailableException(
                "The requested lighting setting is not admitted for this experimental model.");
        }

        await operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        bool submitted = false;
        bool frameWriteAttempted = false;
        bool frameActivationAttempted = false;
        bool capturedBaselineForFrameAttempt = false;
        LightingRendererOwnershipState priorOwnership = OwnershipState;
        ExperimentalLightingBaseline? priorBaseline = lightingOwnership.Baseline;
        LightingSettings? priorIntent = lightingOwnership.Intent;
        bool hostOwnershipAcquired = false;
        bool replacingOwnedFrame = IsOwnedFrameOwnership(priorOwnership);
        bool replacingFaultedFrame = priorOwnership == LightingRendererOwnershipState.Faulted;
        bool startingHostFrameFromFault = replacingFaultedFrame
            && settings.Effect is LightingEffect.AmbientAwareness
                or LightingEffect.AudioMeter;
        byte raw = checked((byte)((settings.Brightness * 255 + 50) / 100));
        string requested = $"{settings.Effect};Raw={raw};Logo={settings.LogoMode?.ToString() ?? "Unchanged"}";
        try
        {
            RequireWriteAdmission();
            LightingSettings baseline;
            byte baselineRaw;
            if (replacingOwnedFrame)
            {
                baseline = lightingOwnership.Intent
                    ?? throw new IOException("Owned static lighting intent is unavailable.");
                baselineRaw = await ReadBrightnessAsync(cancellationToken).ConfigureAwait(false);
            }
            else if (replacingFaultedFrame && !startingHostFrameFromFault)
            {
                baseline = lightingOwnership.Baseline?.Settings
                    ?? throw new IOException(
                        "The confirmed pre-frame lighting baseline is unavailable.");
                baselineRaw = await ReadBrightnessAsync(cancellationToken).ConfigureAwait(false);
            }
            else
            {
                (baseline, baselineRaw) = await ReadKeyboardLightingAsync(cancellationToken)
                    .ConfigureAwait(false);
            }
            LogoLightingMode? baselineLogo = settings.LogoMode is null
                ? null
                : await ReadLogoAsync(cancellationToken).ConfigureAwait(false);
            byte originalBaselineRaw = baselineRaw;
            policy.Record("KeyboardLighting", "Baseline", "Confirmed", requested: requested,
                confirmed: $"{baseline.Effect};Raw={baselineRaw};Logo={baselineLogo?.ToString() ?? "Unchanged"}");

            async Task ApplyBrightnessAsync()
            {
                if (baselineRaw == raw)
                {
                    return;
                }
                RequireWriteAdmission();
                await ExchangeAsync("KeyboardBrightness", 0x03, 0x03,
                    [1, 5, raw], true, cancellationToken).ConfigureAwait(false);
                submitted = true;
                if (await ReadBrightnessAsync(cancellationToken).ConfigureAwait(false) != raw)
                {
                    throw new IOException(
                        "Independent keyboard brightness readback did not confirm the requested raw value.");
                }
                baselineRaw = raw;
            }

            if (settings.Effect != LightingEffect.Off)
            {
                await ApplyBrightnessAsync().ConfigureAwait(false);
            }

            bool setOnlyFrame = false;
            if (settings.Effect is LightingEffect.AmbientAwareness
                or LightingEffect.AudioMeter)
            {
                if (!SupportsExperimentalUniformFrames(model, enabled: true))
                {
                    throw new CapabilityUnavailableException(
                        "Uniform host-rendered lighting is not admitted for this model.");
                }
                var hostBaseline = new ExperimentalLightingBaseline(
                    baseline,
                    originalBaselineRaw);
                if (replacingFaultedFrame)
                {
                    // A readable effect after a fault is the only safe baseline
                    // for this new ownership. Never retain the former owner's
                    // stale restore destination.
                    lightingOwnership.Baseline = hostBaseline;
                }
                else
                {
                    lightingOwnership.Baseline ??= hostBaseline;
                }
                lightingOwnership.Intent = settings with { LogoMode = null };
                SetLightingOwnership(settings.Effect == LightingEffect.AmbientAwareness
                    ? LightingRendererOwnershipState.AmbientAwareness
                    : LightingRendererOwnershipState.AudioMeter);
                hostOwnershipAcquired = true;
                setOnlyFrame = true;
            }
            else if (settings.Effect == LightingEffect.Static && SupportsUniformFrame(model))
            {
                ExperimentalLightingBaseline frameBaseline = lightingOwnership.Baseline
                    ?? new ExperimentalLightingBaseline(baseline, originalBaselineRaw);
                if (lightingOwnership.Baseline is null)
                {
                    // Persist the confirmed built-in baseline before the first
                    // row can become indeterminate. This is restoration intent,
                    // not custom-frame color readback.
                    lightingOwnership.Baseline = frameBaseline;
                    capturedBaselineForFrameAttempt = true;
                }
                // The upstream model reports that standby and idle fade can
                // discard volatile custom rows. Ownership and color equality
                // therefore cannot prove that the frame is still present.
                // https://github.com/sqmagellan/razer-ctl/blob/9c66e7055a0e31f5ec25c16185e66fde1f50cf4a/README.md
                for (int row = 0; row < ExperimentalBladeLightingFrame.RowCount; row++)
                {
                    RequireWriteAdmission();
                    frameWriteAttempted = true;
                    await ExchangeAsync("KeyboardStaticFrame", 0x03, 0x0B,
                        ExperimentalBladeLightingFrame.CreateUniformRow(row, settings.Color),
                        true, cancellationToken, variableResponse: true,
                        payloadValidator: static value =>
                            value.Length <= RazerReport.MaximumPayloadLength,
                        requireEcho: false).ConfigureAwait(false);
                    submitted = true;
                }
                RequireWriteAdmission();
                frameActivationAttempted = true;
                await ExchangeAsync("KeyboardStaticFrame", 0x03, 0x0A,
                    [5, 0], true, cancellationToken, variableResponse: true,
                    payloadValidator: static value =>
                        value.Length <= RazerReport.MaximumPayloadLength,
                    requireEcho: false).ConfigureAwait(false);
                submitted = true;
                lightingOwnership.Baseline = frameBaseline;
                lightingOwnership.Intent = settings with { LogoMode = null };
                SetLightingOwnership(LightingRendererOwnershipState.Static);
                setOnlyFrame = true;
            }
            else if (settings.Effect == LightingEffect.Static)
            {
                RequireWriteAdmission();
                await ExchangeAsync("KeyboardStaticColor", 0x03, 0x0A,
                    [6, settings.Color.Red, settings.Color.Green, settings.Color.Blue],
                    true, cancellationToken, variableResponse: true,
                    payloadValidator: static value =>
                        value.Length <= RazerReport.MaximumPayloadLength,
                    requireEcho: false).ConfigureAwait(false);
                submitted = true;
            }
            else if (model.ProductId == 0x0255
                && settings.Parameters is BreathingLightingParameters
                {
                    ColorMode: BreathingColorMode.Single,
                    PrimaryColor: { } breathingColor,
                })
            {
                RequireWriteAdmission();
                await ExchangeAsync("KeyboardBreathingColor", 0x03, 0x0A,
                    [3, 1, breathingColor.Red, breathingColor.Green, breathingColor.Blue],
                    true, cancellationToken, variableResponse: true,
                    payloadValidator: static value =>
                        value.Length <= RazerReport.MaximumPayloadLength,
                    requireEcho: false).ConfigureAwait(false);
                submitted = true;
            }
            else if (replacingOwnedFrame
                || replacingFaultedFrame
                || baseline.Effect != settings.Effect
                || model.ProductId == 0x0255
                    && settings.Parameters is BreathingLightingParameters
                    {
                        ColorMode: BreathingColorMode.Random,
                    }
                    && baseline.Parameters is not BreathingLightingParameters
                    {
                        ColorMode: BreathingColorMode.Random,
                    })
            {
                await SetBuiltInEffectAsync(settings, cancellationToken).ConfigureAwait(false);
                submitted = true;
                (LightingEffect confirmedEffect, _) = await ReadEffectAsync(cancellationToken)
                    .ConfigureAwait(false);
                if (confirmedEffect != settings.Effect)
                {
                    throw new IOException(
                        "Independent keyboard effect readback did not confirm the requested effect.");
                }
                ClearOwnedFrame();
            }

            if (settings.Effect == LightingEffect.Off)
            {
                await ApplyBrightnessAsync().ConfigureAwait(false);
            }

            if (settings.LogoMode is { } logo && logo != baselineLogo)
            {
                await SetLogoAsync(logo, cancellationToken, () => submitted = true)
                    .ConfigureAwait(false);
            }

            if (setOnlyFrame)
            {
                policy.Record("KeyboardLighting", "Readback", "Unavailable", submitted,
                    requested, "OwnedStaticIntent", "FrameColorReadbackUnavailable");
                return;
            }

            (LightingSettings confirmed, byte confirmedRaw) =
                await ReadKeyboardLightingAsync(cancellationToken).ConfigureAwait(false);
            if (!KeyboardSettingsMatch(confirmed, settings) || confirmedRaw != raw)
            {
                throw new IOException(
                    "Independent final lighting readback did not confirm the requested state.");
            }
            policy.Record("KeyboardLighting", "Readback", "Confirmed", submitted,
                requested, requested);
        }
        catch (DeviceStateContentRejectedException)
            when (startingHostFrameFromFault && !submitted)
        {
            throw new CapabilityUnavailableException(
                "Choose and confirm a built-in lighting effect before restarting host-rendered lighting.");
        }
        catch (Exception exception)
        {
            bool indeterminate = exception is BladeWriteException
            { SubmissionState: WriteSubmissionState.Indeterminate };
            if ((replacingOwnedFrame && submitted)
                || (hostOwnershipAcquired && (submitted || indeterminate))
                || frameActivationAttempted
                || frameWriteAttempted
                    && (submitted || indeterminate))
            {
                SetLightingFaulted();
            }
            else if (hostOwnershipAcquired)
            {
                lightingOwnership.Baseline = priorBaseline;
                lightingOwnership.Intent = priorIntent;
                SetLightingOwnership(priorOwnership);
            }
            else if (capturedBaselineForFrameAttempt)
            {
                lightingOwnership.Baseline = null;
            }
            throw MapWriteFailure("KeyboardLighting", submitted, exception);
        }
        finally
        {
            operationGate.Release();
        }
    }

    private bool IsSupportedLightingSetting(LightingSettings settings)
    {
        if (!LightingSettingsValidation.IsWellFormed(settings)
            || !CreateExperimentalLightingEffects(model, enabled: true)
                .Contains(settings.Effect)
            || settings.LogoMode is { } logo
                && !CreateExperimentalLogoModes(model, enabled: true).Contains(logo))
        {
            return false;
        }
        return settings.Effect switch
        {
            LightingEffect.Breathing => settings.Parameters is
                BreathingLightingParameters parameters
                && CreateExperimentalBreathingColorModes(model, enabled: true)
                    .Contains(parameters.ColorMode),
            LightingEffect.Wave => settings.Parameters is
                WaveLightingParameters { Direction: LightingDirection.Rightward },
            LightingEffect.Static => settings.Parameters is StaticLightingParameters,
            LightingEffect.AmbientAwareness => settings.Parameters is
                AmbientAwarenessLightingParameters,
            LightingEffect.AudioMeter => settings.Parameters is AudioMeterLightingParameters,
            _ => true,
        };
    }

    private async Task SetBuiltInEffectAsync(
        LightingSettings settings,
        CancellationToken cancellationToken)
    {
        byte[] arguments = settings.Effect switch
        {
            LightingEffect.Off => [1, 5, 0],
            LightingEffect.Breathing => [1, 5, 2],
            LightingEffect.Spectrum => [1, 5, 3],
            LightingEffect.Wave => [1, 5, 4, 1],
            _ => throw new CapabilityUnavailableException(
                "This effect has no experimental built-in setter."),
        };
        RequireWriteAdmission();
        await ExchangeAsync("KeyboardLighting", 0x0F, 0x02, arguments, true,
            cancellationToken, variableResponse: true,
            payloadValidator: static value =>
                value.Length <= RazerReport.MaximumPayloadLength,
            requireEcho: false).ConfigureAwait(false);
    }

    private async Task SetLogoAsync(
        LogoLightingMode logo,
        CancellationToken cancellationToken,
        Action markSubmitted)
    {
        if (logo != LogoLightingMode.Off)
        {
            RequireWriteAdmission();
            await ExchangeAsync("LogoLighting", 0x03, 0x02,
                LogoLightingProtocol.SetEffectArguments(logo), true, cancellationToken)
                .ConfigureAwait(false);
            markSubmitted();
        }
        RequireWriteAdmission();
        await ExchangeAsync("LogoLighting", 0x03, 0x00,
            LogoLightingProtocol.SetStateArguments(logo), true, cancellationToken)
            .ConfigureAwait(false);
        markSubmitted();
        if (await ReadLogoAsync(cancellationToken).ConfigureAwait(false) != logo)
        {
            throw new IOException(
                "Independent logo readback did not confirm the requested mode.");
        }
    }

    private static bool KeyboardSettingsMatch(
        LightingSettings confirmed,
        LightingSettings requested) =>
        confirmed.Effect == requested.Effect
        && confirmed.Brightness == requested.Brightness
        && confirmed.Color == requested.Color
        && Equals(confirmed.Parameters, requested.Parameters);

    private void SetLightingOwnership(LightingRendererOwnershipState value) =>
        Volatile.Write(ref lightingOwnership.Ownership, (int)value);

    private void SetLightingFaulted() =>
        SetLightingOwnership(LightingRendererOwnershipState.Faulted);

    private static bool IsOwnedFrameOwnership(LightingRendererOwnershipState value) =>
        value is LightingRendererOwnershipState.Static
            or LightingRendererOwnershipState.AmbientAwareness
            or LightingRendererOwnershipState.AudioMeter;

    private void ClearOwnedFrame()
    {
        lightingOwnership.Baseline = null;
        lightingOwnership.Intent = null;
        SetLightingOwnership(LightingRendererOwnershipState.None);
    }

    private async Task RestoreOwnedFrameAsync(
        CancellationToken cancellationToken,
        Action markSubmitted)
    {
        ExperimentalLightingBaseline baseline = lightingOwnership.Baseline
            ?? throw new IOException("The owned static frame baseline is unavailable.");
        byte currentRaw = await ReadBrightnessAsync(cancellationToken).ConfigureAwait(false);

        if (baseline.Settings.Effect != LightingEffect.Off
            && currentRaw != baseline.RawBrightness)
        {
            RequireWriteAdmission();
            await ExchangeAsync("KeyboardBrightness", 0x03, 0x03,
                [1, 5, baseline.RawBrightness], true, cancellationToken)
                .ConfigureAwait(false);
            markSubmitted();
            if (await ReadBrightnessAsync(cancellationToken).ConfigureAwait(false)
                != baseline.RawBrightness)
            {
                throw new IOException(
                    "Independent keyboard brightness readback did not confirm the restored baseline.");
            }
        }

        await SetBuiltInEffectAsync(baseline.Settings, cancellationToken).ConfigureAwait(false);
        markSubmitted();
        (LightingEffect confirmedEffect, _) = await ReadEffectAsync(cancellationToken)
            .ConfigureAwait(false);
        if (confirmedEffect != baseline.Settings.Effect)
        {
            throw new IOException(
                "Independent keyboard effect readback did not confirm the restored baseline.");
        }

        if (baseline.Settings.Effect == LightingEffect.Off
            && currentRaw != baseline.RawBrightness)
        {
            RequireWriteAdmission();
            await ExchangeAsync("KeyboardBrightness", 0x03, 0x03,
                [1, 5, baseline.RawBrightness], true, cancellationToken)
                .ConfigureAwait(false);
            markSubmitted();
            if (await ReadBrightnessAsync(cancellationToken).ConfigureAwait(false)
                != baseline.RawBrightness)
            {
                throw new IOException(
                    "Independent keyboard brightness readback did not confirm the restored baseline.");
            }
        }

        ClearOwnedFrame();
        policy.Record("KeyboardStaticFrame", "Restore", "Confirmed", true,
            requested: $"{baseline.Settings.Effect};Raw={baseline.RawBrightness}",
            confirmed: $"{baseline.Settings.Effect};Raw={baseline.RawBrightness}");
    }

    public Task<byte> StepBrightnessAsync(
        KeyboardBrightnessDirection direction,
        CancellationToken cancellationToken) =>
        Task.FromException<byte>(new CapabilityUnavailableException(
            "Firmware-native keyboard brightness stepping is not admitted for this experimental model."));

    public Task PulseReactiveKeyAsync(
        ReactiveKeyPulse pulse,
        CancellationToken cancellationToken) =>
        Task.FromException(new CapabilityUnavailableException(
            "Key-triggered lighting is not admitted for this experimental model."));

    public Task PushAmbientLightingFrameAsync(
        AmbientLightingFrame frame,
        CancellationToken cancellationToken) =>
        Task.FromException(new CapabilityUnavailableException(
            "The shared ambient frame layout is not admitted for this experimental model."));

    public async Task PushUniformLightingFrameAsync(
        UniformLightingFrame frame,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(frame);
        if (!SupportsExperimentalUniformFrames(model, enabled: true))
        {
            throw new CapabilityUnavailableException(
                "Uniform host-rendered lighting is not admitted for this model.");
        }

        await operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        bool submitted = false;
        try
        {
            if (OwnershipState is not (
                LightingRendererOwnershipState.AmbientAwareness or
                LightingRendererOwnershipState.AudioMeter)
                || lightingOwnership.Intent is null)
            {
                throw new CapabilityUnavailableException(
                    "A uniform host-rendered lighting effect does not own the keyboard.");
            }

            for (int row = 0; row < ExperimentalBladeLightingFrame.RowCount; row++)
            {
                RequireWriteAdmission();
                await ExchangeAsync("KeyboardUniformFrame", 0x03, 0x0B,
                    ExperimentalBladeLightingFrame.CreateUniformRow(row, frame.Color),
                    true, cancellationToken, variableResponse: true,
                    payloadValidator: static value =>
                        value.Length <= RazerReport.MaximumPayloadLength,
                    requireEcho: false,
                    recordAcknowledgedSubmission: false).ConfigureAwait(false);
                submitted = true;
            }
            RequireWriteAdmission();
            await ExchangeAsync("KeyboardUniformFrame", 0x03, 0x0A,
                [5, 0], true, cancellationToken, variableResponse: true,
                payloadValidator: static value =>
                    value.Length <= RazerReport.MaximumPayloadLength,
                requireEcho: false,
                recordAcknowledgedSubmission: false).ConfigureAwait(false);
            submitted = true;
            policy.RecordUniformFrameApplied(frame.Color);
        }
        catch (CapabilityUnavailableException) when (!submitted)
        {
            // A late renderer frame lost ownership before any submission.
            // Preserve the typed Blocked result and do not add failure noise.
            throw;
        }
        catch (Exception exception)
        {
            if (submitted || exception is BladeWriteException
                { SubmissionState: not WriteSubmissionState.KnownNotApplied })
            {
                SetLightingFaulted();
            }
            throw MapWriteFailure("KeyboardUniformFrame", submitted, exception);
        }
        finally
        {
            operationGate.Release();
        }
    }

    public Task SetPlaybackMuteIndicatorAsync(
        bool muted,
        CancellationToken cancellationToken) =>
        Task.FromException(new CapabilityUnavailableException(
            "Playback mute lighting is not admitted for this experimental model."));

    public async Task<LightingRendererOwnershipState> RelinquishRendererAsync(
        CancellationToken cancellationToken)
    {
        RequireLightingModel();
        await operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        bool submitted = false;
        LightingRendererOwnershipState observed = OwnershipState;
        try
        {
            if (IsOwnedFrameOwnership(observed))
            {
                RequireWriteAdmission();
                await RestoreOwnedFrameAsync(
                    cancellationToken,
                    () => submitted = true).ConfigureAwait(false);
            }
            return observed;
        }
        catch (Exception exception)
        {
            SetLightingFaulted();
            throw MapWriteFailure("KeyboardStaticFrameRestore", submitted, exception);
        }
        finally
        {
            operationGate.Release();
        }
    }

    public Task<LightingRendererOwnershipState> InvalidateAsync() =>
        CloseLightingAsync(restoreFrame: true);

    public Task<LightingRendererOwnershipState> InvalidateForLowPowerAsync() =>
        CloseLightingAsync(restoreFrame: false);

    private async Task<LightingRendererOwnershipState> CloseLightingAsync(bool restoreFrame)
    {
        await operationGate.WaitAsync().ConfigureAwait(false);
        LightingRendererOwnershipState observed = OwnershipState;
        bool submitted = false;
        Exception? failure = null;
        try
        {
            if (restoreFrame && IsOwnedFrameOwnership(observed))
            {
                RequireWriteAdmission();
                await RestoreOwnedFrameAsync(
                    CancellationToken.None,
                    () => submitted = true).ConfigureAwait(false);
            }
        }
        catch (Exception exception)
        {
            SetLightingFaulted();
            failure = MapWriteFailure("KeyboardStaticFrameRestore", submitted, exception);
        }
        finally
        {
            operationGate.Release();
            await DisposeAsync().ConfigureAwait(false);
        }

        if (failure is not null)
        {
            throw failure;
        }
        return observed;
    }
}
