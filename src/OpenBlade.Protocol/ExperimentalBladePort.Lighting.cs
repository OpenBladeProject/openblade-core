using System.Text.Json.Serialization;
using OpenBlade.Contracts;

namespace OpenBlade.Protocol;

// VaporChamber is the restorable RZ09-0529 LED38 record, present only while
// OpenBlade may have selected LED38 custom mode: it is saved before [0,38,8] can
// be submitted and removed once LED38 is restored or another controller took it.
// Omitted when null so keyboard-only journals keep schema 1.
internal sealed record ExperimentalLightingBaseline(
    LightingSettings Settings,
    byte RawBrightness,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    LightingSettings? VaporChamber = null);

internal sealed class ExperimentalLightingOwnershipState
{
    internal int Ownership;
    internal ExperimentalLightingBaseline? Baseline;
    internal LightingSettings? Intent;
    // The current RZ09-0529 host frames also drive LED38 row 6. Uniform owners set
    // it before their first sample, which re-reads LED38 before selecting it.
    internal bool VaporChamberFrames;
    // The first LED38 record seen during this keyboard baseline's lifetime, before
    // OpenBlade touched LED38 (null when it was unrestorable). LED38 is taken only
    // while it still equals this record, so another controller's change wins.
    internal bool VaporChamberObserved;
    internal LightingSettings? VaporChamberDestination;
    // RZ09-0301/0421/0423/0427: OpenBlade may have selected custom mode 8 for
    // this baseline, so its frame can still be shown even when 0F/82 reports the
    // saved effect (a GPIO edge keeps the custom flag). Set before every [0,5,8]
    // and for a journal loaded at startup; a fault keeps it. Only a verified
    // restore through the flag-clearing 03/0A setter clears it.
    internal bool McuCustomFrameMayRemain;
    internal int UniformFrameActivated;
    internal long UniformFrameTimestamp;
    internal ExperimentalLightingBaselineJournal? Journal;
    internal bool JournalInvalid;
}

internal sealed partial class ExperimentalBladePort
{
    private IReadOnlyList<RgbColor>? keyboardMirrorFrame;
    private int keyboardMirrorColumns;
    private int keyboardMirrorRows;
    private bool keyboardMirrorTransitionPending;
    public LightingRendererOwnershipState OwnershipState =>
        (LightingRendererOwnershipState)Volatile.Read(ref lightingOwnership.Ownership);

    public bool RequiresLightingRendererOwnership(LightingSettings settings) =>
        SupportsBlade18Animation(model) && IsBlade18Animation(settings)
        || IsExperimentalGridRequest(settings)
        || settings.Effect is LightingEffect.AmbientAwareness or LightingEffect.AudioMeter
        || settings.Effect == LightingEffect.Static && SupportsUniformFrame(model);

    internal static bool SupportsExperimentalLighting(ExperimentalBladeModel model) =>
        model.ProductId is 0x029F or 0x029D or 0x02C5 or 0x0255 or 0x02A0
            || SupportsIndependentLighting(model);

    internal static bool SupportsExperimentalKeyboardBrightness(
        ExperimentalBladeModel model,
        bool enabled) => enabled && SupportsExperimentalLighting(model);

    internal static IReadOnlySet<LightingEffect> CreateExperimentalLightingEffects(
        ExperimentalBladeModel model,
        bool enabled)
    {
        if (!enabled || !SupportsExperimentalLighting(model) || SupportsOnlyIndependentLighting(model))
        {
            return new HashSet<LightingEffect>();
        }

        var effects = new HashSet<LightingEffect>
        {
            LightingEffect.Off,
            LightingEffect.Spectrum,
            LightingEffect.Breathing,
        };
        if (SupportsReviewedMcuLighting(model) && !UsesLegacyReviewedMcuEffectSetter(model))
        {
            // These exact images have an unreadable Spectrum policy/overlay
            // condition; a matching mode byte cannot establish its destination.
            effects.Remove(LightingEffect.Spectrum);
        }
        if (model.ProductId != 0x0255)
        {
            effects.Add(LightingEffect.Wave);
        }
        if (SupportsUniformFrame(model) || model.ProductId is 0x0245 or 0x0255 or 0x028B or 0x028C or 0x02B8 or 0x02C7)
        {
            effects.Add(LightingEffect.Static);
        }
        if (SupportsExperimentalUniformFrames(model, enabled: true))
        {
            effects.Add(LightingEffect.AmbientAwareness);
            effects.Add(LightingEffect.AudioMeter);
        }
        if (SupportsBlade18Animation(model))
        {
            effects.Add(LightingEffect.Fire);
            effects.Add(LightingEffect.Starlight);
            effects.Add(LightingEffect.Reactive);
            effects.Add(LightingEffect.Ripple);
            effects.Add(LightingEffect.Tidal);
            effects.Add(LightingEffect.Sequence);
            effects.Add(LightingEffect.Wheel);
        }
        if (SupportsExperimentalGridAnimation(model))
        {
            effects.UnionWith([LightingEffect.Fire, LightingEffect.Wheel,
                LightingEffect.Starlight, LightingEffect.Tidal]);
        }
        if (model.ProductId == 0x02C7)
        {
            // Per-key sequences use the exact 114-slot keyboard canvas and
            // remain keyboard-only; Spectrum stays on the admitted software renderer.
            effects.UnionWith([LightingEffect.Spectrum, LightingEffect.Reactive,
                LightingEffect.Ripple, LightingEffect.Sequence]);
        }
        if (model.ProductId == 0x02C5)
        {
            // PI4110's independently reviewed HID-to-row/column maps, using
            // only bindings common to every firmware-selected regional table.
            effects.UnionWith([LightingEffect.Reactive, LightingEffect.Ripple, LightingEffect.Sequence]);
        }
        return effects;
    }

    internal static IReadOnlySet<LightingEffect> CreateObservedExperimentalLightingEffects(
        ExperimentalBladeModel model)
    {
        if (SupportsExperimentalGridAnimation(model) && model.ProductId != 0x02C7
            && !SupportsReviewedMcuKeyboardFrames(model))
        {
            // Read-only discovery from each exact OpenRazer class's METHODS.
            // This newly allocated set is used only in ObservedLightingEffects.
            // Writable admission is separate: 02C5 uses its reviewed MCU map,
            // while the two older models keep these key effects observed-only.
            return new HashSet<LightingEffect>
            {
                LightingEffect.Off, LightingEffect.Static, LightingEffect.Spectrum,
                LightingEffect.Breathing, LightingEffect.Wave, LightingEffect.Starlight,
                LightingEffect.Reactive, LightingEffect.Ripple,
            };
        }
        if (SupportsIndependentLighting(model))
        {
            // Exact-product UI catalogs establish discovery only. Writable effects
            // are admitted separately from each model's reviewed MCU state contract.
            return new HashSet<LightingEffect>
            {
                LightingEffect.AmbientAwareness, LightingEffect.AudioMeter,
                LightingEffect.Breathing, LightingEffect.Fire, LightingEffect.Reactive,
                LightingEffect.Ripple, LightingEffect.Spectrum, LightingEffect.Starlight,
                LightingEffect.Static, LightingEffect.Wave, LightingEffect.Wheel, LightingEffect.Tidal,
            };
        }
        var observedEffects = new HashSet<LightingEffect>(CreateExperimentalLightingEffects(model, enabled: true));
        observedEffects.Remove(LightingEffect.Sequence);
        if (model.ProductId == 0x02A0)
        {
            // Product 672's pinned quick-effect catalog is discovery evidence,
            // not admission for spatial frames or unimplemented firmware commands.
            observedEffects.UnionWith([
                LightingEffect.AmbientAwareness, LightingEffect.AudioMeter,
                LightingEffect.Fire, LightingEffect.Reactive, LightingEffect.Ripple,
                LightingEffect.Starlight, LightingEffect.Wheel, LightingEffect.Tidal]);
        }
        return observedEffects;
    }

    internal static IReadOnlySet<LogoLightingMode> CreateExperimentalLogoModes(
        ExperimentalBladeModel model,
        bool enabled) => enabled && SupportsExperimentalLogo(model)
            ? new HashSet<LogoLightingMode>(Enum.GetValues<LogoLightingMode>())
            : new HashSet<LogoLightingMode>();

    internal static bool SupportsExperimentalBreathingColorSelection(
        ExperimentalBladeModel model) => model.ProductId == 0x02A0
            || SupportsExperimentalGridAnimation(model) || SupportsReviewedMcuLighting(model);

    internal static bool SupportsExperimentalWaveDirectionSelection(
        ExperimentalBladeModel model) => model.ProductId == 0x02A0
            || SupportsExperimentalGridAnimation(model) || SupportsReviewedMcuLighting(model);

    internal static bool SupportsExperimentalUniformFrames(
        ExperimentalBladeModel model,
        bool enabled) => enabled && (SupportsUniformFrame(model) || model.ProductId == 0x02C7
            || SupportsReviewedMcuKeyboardFrames(model));

    internal static IReadOnlySet<BreathingColorMode> CreateExperimentalBreathingColorModes(
        ExperimentalBladeModel model,
        bool enabled) => !enabled
            ? new HashSet<BreathingColorMode>()
            : model.ProductId == 0x02A0 || SupportsExperimentalGridAnimation(model) || SupportsReviewedMcuLighting(model)
                ? new HashSet<BreathingColorMode>(Enum.GetValues<BreathingColorMode>())
                : model.ProductId == 0x0255
                ? new HashSet<BreathingColorMode>
            {
                BreathingColorMode.Random,
                BreathingColorMode.Single,
            }
                : new HashSet<BreathingColorMode> { BreathingColorMode.Random };

    private static bool SupportsExperimentalLogo(ExperimentalBladeModel model) =>
        model.ProductId is 0x029F or 0x029D or 0x02C5 or 0x02A0
            || SupportsSourceReviewedLogo(model);

    private static bool SupportsUniformFrame(ExperimentalBladeModel model) =>
        model.ProductId is 0x029F or 0x029D or 0x02C5 or 0x02A0;

    public async Task<LightingSettings?> ReadStateAsync(CancellationToken cancellationToken)
    {
        RequireLightingModel();
        if (SupportsOnlyIndependentLighting(model))
        {
            // Its old matrix setter has no reviewed corresponding effect getter.
            return null;
        }
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
        if (!SupportsExperimentalLighting(model) || SupportsOnlyIndependentLighting(model))
        {
            return (null, null);
        }

        // A volatile 6-by-16 frame has no getter. A newly opened recovery port
        // cannot distinguish it from the persisted built-in effect underneath.
        LightingSettings? lighting = SupportsUniformFrame(model)
            ? null
            : (await ReadKeyboardLightingAsync(
                cancellationToken, allowConfirmedDark: true).ConfigureAwait(false)).Settings;
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
        byte storage = SupportsReviewedMcuLighting(model) ? (byte)0 : (byte)1;
        ReadOnlyMemory<byte> payload = await ExchangeAsync("KeyboardBrightness", 0x03, 0x83,
            [storage, 5, 0], false, token).ConfigureAwait(false);
        if (payload.Length != 3 || payload.Span[0] != storage || payload.Span[1] != 5)
        {
            session.RetainQueryFailure(0x03, 0x83, FirmwareQueryOutcome.ContentRejected);
            RecordQuery("KeyboardBrightness", "UnsupportedState", failure: "BrightnessSelector");
            throw new IOException("Experimental keyboard brightness returned an unexpected selector.");
        }
        byte raw = payload.Span[2];
        RecordQuery("KeyboardBrightness", "Confirmed",
            $"Raw={raw};Percent={LightingBrightnessProtocol.RawToPercent(raw)}");
        return raw;
    }

    private async Task<(LightingSettings? Settings, byte Raw, ReadOnlyMemory<byte> EffectPayload)> ReadKeyboardLightingAsync(
        CancellationToken token,
        bool allowConfirmedDark = false)
    {
        byte raw = await ReadBrightnessAsync(token).ConfigureAwait(false);
        if (SupportsReviewedMcuLighting(model))
        {
            return await ReadReviewedMcuKeyboardStateAsync(raw, allowConfirmedDark, token).ConfigureAwait(false);
        }
        (LightingEffect effect, ReadOnlyMemory<byte> effectPayload) =
            await ReadEffectAsync(token, allowAlternateWaveDirection: true).ConfigureAwait(false);
        byte percent = checked((byte)LightingBrightnessProtocol.RawToPercent(raw));
        if (effect != LightingEffect.Off && percent == 0)
        {
            if (allowConfirmedDark && raw == 0)
            {
                // A dark-wake failure may leave a selected built-in effect at
                // confirmed zero brightness. Keep the public setting unknown;
                // recovery and a new explicit request need no guessed Off state.
                // Host-frame requests still require a representable restoration baseline.
                RecordQuery("KeyboardLighting", "Unavailable",
                    $"Effect={effect};Raw=0;Percent=0", "ConfirmedDarkEffect");
                return (null, raw, effectPayload);
            }
            session.RetainQueryFailure(0x03, 0x83, FirmwareQueryOutcome.ContentRejected);
            session.RetainQueryFailure(0x0F, 0x82, FirmwareQueryOutcome.ContentRejected);
            RecordQuery("KeyboardLighting", "UnsupportedState", $"Effect={effect};Raw={raw};Percent=0",
                "UnrepresentableZeroBrightnessEffect");
            throw new DeviceStateContentRejectedException(
                "The reported effect at zero brightness cannot form a confirmed lighting setting.",
                DeviceStateQuery.KeyboardLightingCombination,
                DeviceStateContentRejection.FormatPayloads([1, 5, raw], effectPayload.Span));
        }

        if (effect == LightingEffect.Wave
            && (effectPayload.Length == 3 || effectPayload.Span[3] == 2))
        {
            // The pinned blade-control getter reads only the effect ID from
            // 0F/82 [1,5,0]. A compact Wave reply establishes no direction.
            // Keep the complete setting unknown rather than invent Rightward.
            // Wire 2 is the reviewed opposite direction, but our built-in
            // restoration setter admits wire 1 only. It is a valid observation,
            // not a restorable frame baseline or confirmation of Rightward.
            bool compact = effectPayload.Length == 3;
            RecordQuery("KeyboardLighting", "Unavailable",
                $"Effect=Wave;Raw={raw};Direction={(compact ? "Unavailable" : "Alternate/Wire2")}",
                compact ? "WaveDirectionReadbackUnavailable" : "WaveDirectionNotRestorable");
            return (null, raw, effectPayload);
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
        return (settings, raw, effectPayload);
    }

    private async Task<(LightingEffect Effect, ReadOnlyMemory<byte> Payload)> ReadEffectAsync(
        CancellationToken token,
        bool allowAlternateWaveDirection = false)
    {
        if (SupportsReviewedMcuLighting(model))
        {
            return await ReadReviewedMcuEffectAsync(token).ConfigureAwait(false);
        }

        // PI4110 MCU 1.04.00 (0x8826..0x8828) and PI490 MCU 1.04.00
        // (0x97AA..0x97AC) write Wave direction at payload+3 without expanding
        // header DataSize. Request that byte only for these reviewed contracts.
        // See docs/compact-wave-readback.md and its pinned firmware proofs.
        byte[] arguments = model.ProductId is 0x02C5 or 0x029D ? [1, 5, 0, 0] : [1, 5, 0];
        ReadOnlyMemory<byte> payload = await ExchangeAsync("KeyboardLighting", 0x0F, 0x82,
            arguments, false, token, variableResponse: true,
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
        bool parametersKnown = mode != 4 || bytes.Length == 3 || bytes[3] == 1
            || allowAlternateWaveDirection && bytes[3] == 2;
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
        string parameter = mode != 4 ? "None" : bytes.Length == 3 ? "Unavailable"
            : bytes[3] == 1 ? "Rightward/Wire1" : "Alternate/Wire2";
        RecordQuery("KeyboardLighting", "Confirmed",
            $"{effect};PayloadLength={bytes.Length};Parameter={parameter}");
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
        byte storage = LogoStorageSelector(model);
        ReadOnlyMemory<byte> state = await ExchangeAsync("LogoLighting", 0x03, 0x80,
            LogoLightingProtocol.QueryArguments(storage), false, token).ConfigureAwait(false);
        if (state.Span[0] != storage || state.Span[1] != LogoLightingProtocol.LogoLedId)
        {
            session.RetainQueryFailure(0x03, 0x80, FirmwareQueryOutcome.ContentRejected);
            RecordQuery("LogoLighting", "InconsistentState", failure: "LogoPowerSelector");
            throw new IOException("Experimental logo power returned an unexpected selector.");
        }
        if (!LogoLightingProtocol.TryDecodeState(state.Span, storage, out bool enabled))
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
            LogoLightingProtocol.QueryArguments(storage), false, token).ConfigureAwait(false);
        if (effect.Span[0] != storage || effect.Span[1] != LogoLightingProtocol.LogoLedId)
        {
            session.RetainQueryFailure(0x03, 0x82, FirmwareQueryOutcome.ContentRejected);
            RecordQuery("LogoLighting", "InconsistentState", failure: "LogoEffectSelector");
            throw new IOException("Experimental logo effect returned an unexpected selector.");
        }
        if (!LogoLightingProtocol.TryDecodeMode(
            state.Span,
            effect.Span,
            storage,
            out LogoLightingMode mode))
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
        recoveryWriteGate.ThrowIfBlocked();
        RequireLightingModel();
        if (!IsSupportedLightingSetting(settings))
        {
            throw new CapabilityUnavailableException(
                "The requested lighting setting is not admitted for this experimental model.");
        }

        ExperimentalGridLightingEffect? gridEffect = IsExperimentalGridRequest(settings)
            ? new(settings, model.ProductId == 0x02C7) : null;
        bool animationRequest = gridEffect is not null
            || SupportsBlade18Animation(model) && IsBlade18Animation(settings);
        if (animationRequest && gridEffect is null && session is not IRazerSetFeatureCommandSession)
        {
            throw new CapabilityUnavailableException("This model has no admitted animation stream transport.");
        }
        bool blade18UniformRequest = model.ProductId == 0x02A0
            && settings.Effect is LightingEffect.AmbientAwareness or LightingEffect.AudioMeter;
        bool hostFrameRequest = RequiresLightingRendererOwnership(settings);
        if (blade18UniformRequest && session is not IRazerSetFeatureCommandSession)
        {
            throw new CapabilityUnavailableException("This model has no admitted uniform frame transport.");
        }
        Blade18SpatialLightingEffect? spatialEffect = animationRequest && gridEffect is null
            && IsBlade18SpatialEffect(settings.Effect)
                ? new Blade18SpatialLightingEffect(settings, blade18KeyboardLayoutId) : null;
        Blade18ImportedSequenceEffect? sequenceEffect = settings.Parameters is SequenceLightingParameters sequence
            && model.ProductId == 0x02A0 ? new(sequence, blade18KeyboardLayoutId) : null;
        Blade18StandardLightingEffect? standardEffect = animationRequest && gridEffect is null
            && settings.Effect is LightingEffect.Breathing or LightingEffect.Wave
                ? new(settings, blade18KeyboardLayoutId) : null;
        Blade18LightingFrame? firstAnimationFrame = animationRequest && gridEffect is null
            ? spatialEffect?.RenderFrame(TimeSpan.Zero)
                ?? sequenceEffect?.RenderFrame(TimeSpan.Zero)
                ?? standardEffect?.RenderFrame(TimeSpan.Zero)
                ?? Blade18LightingAnimation.Render(settings, TimeSpan.Zero) : null;
        bool reconciledFaultedBaseline = false;
        await operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            RequireLightingJournalAdmission(hostFrameRequest: false);
            reconciledFaultedBaseline = await FaultedReviewedMcuBaselineStillReportedAsync(
                hostFrameRequest, cancellationToken).ConfigureAwait(false);
            RequireLightingJournalAdmission(hostFrameRequest, reconciledFaultedBaseline);
        }
        catch
        {
            operationGate.Release();
            throw;
        }
        bool mirrorCommitted = false;
        keyboardMirrorTransitionPending = true;
        keyboardMirrorFrame = null;
        KeyboardLightingFrameStream.Clear(this);
        bool stoppedAnimation = animationCancellation is not null;
        bool submitted = false;
        bool frameWriteAttempted = false;
        bool frameActivationAttempted = false;
        bool capturedBaselineForFrameAttempt = false;
        ExperimentalLightingBaselineJournal? newlyPendingJournal = null;
        LightingRendererOwnershipState priorOwnership = OwnershipState;
        ExperimentalLightingBaseline? priorBaseline = lightingOwnership.Baseline;
        LightingSettings? priorIntent = lightingOwnership.Intent;
        int priorUniformFrameActivated = lightingOwnership.UniformFrameActivated;
        long priorUniformFrameTimestamp = lightingOwnership.UniformFrameTimestamp;
        bool priorVaporChamberFrames = lightingOwnership.VaporChamberFrames;
        // Reactive pulses and per-key sequences never address LED38.
        bool includesVaporChamber = SupportsRz090529VaporChamberFrames(model)
            && (gridEffect?.IncludesVaporChamber
                ?? settings.Effect is LightingEffect.AmbientAwareness or LightingEffect.AudioMeter);
        bool hostOwnershipAcquired = false;
        bool replacingOwnedFrame = IsOwnedFrameOwnership(priorOwnership);
        bool replacingFaultedFrame = priorOwnership == LightingRendererOwnershipState.Faulted;
        // A fault normally invalidates the former baseline. A reconciled one is
        // still the journaled destination, so the new owner keeps it.
        bool recaptureFaultedBaseline = replacingFaultedFrame && !reconciledFaultedBaseline;
        bool startingHostFrameFromFault = replacingFaultedFrame
            && (animationRequest || settings.Effect is LightingEffect.AmbientAwareness
                or LightingEffect.AudioMeter);
        // The coordinator projects a combined positive brightness onto the effect
        // write. Use the independent field's raw encoding so its confirmation
        // does not need to correct an already applied keyboard brightness.
        bool combinedBrightness = SupportsReviewedMcuLighting(model)
            && settings.ControlsKeyboardEffect
            && settings.IndependentControls is
            {
                Brightness: > 0 and var percent,
                LogoMode: null,
                VaporChamberLightingEnabled: null,
                TouchpadStatusIndicatorEnabled: null,
            }
            && percent == settings.Brightness;
        byte raw = combinedBrightness
            ? LightingBrightnessProtocol.PercentToRaw(settings.Brightness)
            : checked((byte)((settings.Brightness * 255 + 50) / 100));
        string requested = $"{settings.Effect};Raw={raw};Logo={settings.LogoMode?.ToString() ?? "Unchanged"}";
        bool selectionCheckDeferred = false;
        try
        {
            if (replacingOwnedFrame && hostFrameRequest && SupportsReviewedMcuKeyboardFrames(model)
                && lightingOwnership.Baseline is not null)
            {
                // Replace frames only while this owner still holds the keyboard:
                // custom mode 8 once selected, otherwise the journaled baseline
                // effect. Another controller's change stops without a write.
                // Check before draining the renderer, so a read refused before it
                // reaches the device leaves the current owner running unchanged.
                try
                {
                    RequireWriteAdmission();
                    if (ReviewedMcuCustomFrameSelected())
                    {
                        await RequireReviewedMcuCustomFrameSelectionAsync(cancellationToken).ConfigureAwait(false);
                    }
                    else
                    {
                        await RequireReviewedMcuBaselineStillShownAsync(cancellationToken).ConfigureAwait(false);
                    }
                }
                catch (BladeWriteException deferred)
                    when (deferred.FailurePhase == BladeWriteFailurePhase.LightingSelectionRead)
                {
                    selectionCheckDeferred = true;
                    throw;
                }
                catch
                {
                    await StopAnimationCoreAsync().ConfigureAwait(false);
                    throw;
                }
            }
            await StopAnimationCoreAsync().ConfigureAwait(false);
            RequireWriteAdmission();
            LightingSettings? baseline;
            byte baselineRaw;
            ReadOnlyMemory<byte> baselineEffectPayload = default;
            if (replacingOwnedFrame)
            {
                baseline = lightingOwnership.Intent
                    ?? throw new IOException("Owned static lighting intent is unavailable.");
                baselineRaw = await ReadBrightnessAsync(cancellationToken).ConfigureAwait(false);
                if (SupportsReviewedMcuLighting(model) && !hostFrameRequest)
                {
                    // Ambient/audio ownership can precede its first sampled
                    // frame. Choose any required color bridge from actual MCU
                    // state, not from the renderer's requested effect.
                    baselineEffectPayload = await ReadReviewedMcuEffectPayloadAsync(cancellationToken)
                        .ConfigureAwait(false);
                    baseline = DecodeReviewedMcuEffect(baselineEffectPayload.Span,
                        checked((byte)LightingBrightnessProtocol.RawToPercent(baselineRaw)));
                }
            }
            else if (replacingFaultedFrame && !startingHostFrameFromFault)
            {
                baselineRaw = await ReadBrightnessAsync(cancellationToken).ConfigureAwait(false);
                if (SupportsReviewedMcuLighting(model))
                {
                    baselineEffectPayload = await ReadReviewedMcuEffectPayloadAsync(cancellationToken)
                        .ConfigureAwait(false);
                    baseline = DecodeReviewedMcuEffect(baselineEffectPayload.Span,
                        checked((byte)LightingBrightnessProtocol.RawToPercent(baselineRaw)));
                }
                else
                {
                    baseline = lightingOwnership.Baseline?.Settings
                        ?? throw new IOException("The confirmed pre-frame lighting baseline is unavailable.");
                }
            }
            else
            {
                (baseline, baselineRaw, baselineEffectPayload) = await ReadKeyboardLightingAsync(
                    cancellationToken, allowConfirmedDark: !hostFrameRequest).ConfigureAwait(false);
            }
            if (hostFrameRequest && baseline is null)
            {
                // Host rendering must restore the original complete effect;
                // compact Wave readback cannot supply its direction.
                throw new CapabilityUnavailableException(
                    "A readable pre-frame lighting baseline is required for host rendering.");
            }
            // RZ09-0529 host frames may also drive LED38. Read it only for an
            // effect that uses it, or to return an LED38 that OpenBlade selected.
            // An optional read that fails on a usable session leaves this owner
            // keyboard-only; an unrestorable record does the same.
            bool vaporChamberSelected = lightingOwnership.Baseline?.VaporChamber is not null
                && !replacingFaultedFrame;
            (LightingSettings? Settings, byte Mode)? vaporChamberState = hostFrameRequest
                && SupportsRz090529VaporChamberFrames(model)
                && (vaporChamberSelected || includesVaporChamber && VaporChamberReadAvailable)
                    ? await ReadVaporChamberForFramesAsync(vaporChamberSelected, cancellationToken)
                        .ConfigureAwait(false)
                    : null;
            if (hostFrameRequest && SupportsRz090529VaporChamberFrames(model))
            {
                if (priorBaseline is null || recaptureFaultedBaseline)
                {
                    lightingOwnership.VaporChamberObserved = false;
                    lightingOwnership.VaporChamberDestination = null;
                }
                if (vaporChamberState is { } observed && !lightingOwnership.VaporChamberObserved)
                {
                    // Nothing has touched LED38 yet in this baseline's lifetime.
                    lightingOwnership.VaporChamberObserved = true;
                    lightingOwnership.VaporChamberDestination = observed.Settings;
                }
            }
            LogoLightingMode? baselineLogo = settings.LogoMode is null
                ? null
                : await ReadLogoAsync(cancellationToken).ConfigureAwait(false);
            byte originalBaselineRaw = baselineRaw;
            ExperimentalLightingBaseline CaptureFrameBaseline()
            {
                // Keyboard only. LED38 enters the record solely through its
                // write-ahead selection record, just before [0,38,8].
                ExperimentalLightingBaseline captured = new(
                    baseline ?? throw new IOException(
                        "A readable pre-frame lighting baseline is required for host rendering."),
                    originalBaselineRaw);
                // A journal admits only a complete built-in destination. Spectrum
                // reads as a mode without its hidden policy, so refuse it before
                // any write instead of failing while the journal is saved. The
                // 2026-11-19 MCU frame reviews require this baseline shape as well.
                if ((lightingOwnership.Journal is not null || SupportsReviewedMcuKeyboardFrames(model))
                    && !ExperimentalLightingBaselineJournal.IsRestorable(captured))
                {
                    throw new CapabilityUnavailableException(
                        "Host lighting can restore only Off, Static, Breathing or Wave. Choose one of them first.");
                }
                return captured;
            }
            if (hostFrameRequest && SupportsReviewedMcuLighting(model)
                && lightingOwnership.Journal is { } journal)
            {
                // A faulted owner's baseline is replaced by the fresh reading;
                // it reaches here only when that reading equals the journal.
                // A reconciled journal stays pending with its original baseline.
                if (reconciledFaultedBaseline)
                {
                    journal.AdoptReconciledPending(lightingOwnership.Baseline!);
                }
                ExperimentalLightingBaseline saved = recaptureFaultedBaseline
                    ? CaptureFrameBaseline() : lightingOwnership.Baseline ?? CaptureFrameBaseline();
                journal.SavePending(saved);
                lightingOwnership.Baseline = saved;
                if (priorBaseline is null)
                {
                    newlyPendingJournal = journal;
                }
            }
            bool wakingFromDark = baselineRaw == 0 && raw != 0;
            policy.Record("KeyboardLighting", "Baseline", baseline is null ? "Unavailable" : "Confirmed", requested: requested,
                confirmed: $"{baseline?.Effect.ToString() ?? "Unavailable"};Raw={baselineRaw};Logo={baselineLogo?.ToString() ?? "Unchanged"}");

            async Task ApplyBrightnessAsync()
            {
                if (baselineRaw == raw)
                {
                    return;
                }
                RequireWriteAdmission();
                await ExchangeAsync("KeyboardBrightness", 0x03, 0x03,
                    [SupportsReviewedMcuLighting(model) ? (byte)0 : (byte)1, 5, raw], true, cancellationToken).ConfigureAwait(false);
                submitted = true;
                if (await ReadBrightnessAsync(cancellationToken).ConfigureAwait(false) != raw)
                {
                    throw new IOException(
                        "Independent keyboard brightness readback did not confirm the requested raw value.");
                }
                baselineRaw = raw;
            }

            if (settings.Effect != LightingEffect.Off && !wakingFromDark
                && !UsesLegacyReviewedMcuEffectSetter(model))
            {
                await ApplyBrightnessAsync().ConfigureAwait(false);
            }

            bool setOnlyFrame = false;
            if (animationRequest)
            {
                lightingOwnership.Baseline = recaptureFaultedBaseline
                    ? CaptureFrameBaseline()
                    : lightingOwnership.Baseline ?? CaptureFrameBaseline();
                lightingOwnership.Intent = settings with { LogoMode = null };
                SetLightingOwnership(settings.Effect switch
                {
                    LightingEffect.Fire => LightingRendererOwnershipState.Fire,
                    LightingEffect.Starlight => LightingRendererOwnershipState.Starlight,
                    LightingEffect.Reactive => LightingRendererOwnershipState.Reactive,
                    LightingEffect.Ripple => LightingRendererOwnershipState.Ripple,
                    LightingEffect.Tidal => LightingRendererOwnershipState.Tidal,
                    LightingEffect.Sequence => LightingRendererOwnershipState.Sequence,
                    LightingEffect.Breathing => LightingRendererOwnershipState.Breathing,
                    LightingEffect.Wave => LightingRendererOwnershipState.Wave,
                    LightingEffect.Spectrum => LightingRendererOwnershipState.Spectrum,
                    _ => LightingRendererOwnershipState.Wheel,
                });
                hostOwnershipAcquired = true;
                frameWriteAttempted = true;
                if (gridEffect is not null)
                {
                    bool vaporChamber = await SelectRz090529VaporChamberFramesAsync(vaporChamberState,
                        includesVaporChamber, cancellationToken, () => submitted = true).ConfigureAwait(false);
                    await SubmitExperimentalGridFrameAsync(gridEffect.RenderFrame(TimeSpan.Zero),
                        vaporChamber ? gridEffect.RenderVaporChamber(TimeSpan.Zero) : null,
                        activate: true, cancellationToken, () => submitted = true).ConfigureAwait(false);
                }
                else
                {
                    await SubmitBlade18FrameAsync(firstAnimationFrame!, activate: true,
                        cancellationToken).ConfigureAwait(false);
                }
                submitted = true;
                policy.RecordAnimationFrameApplied(settings.Effect);
                setOnlyFrame = true;
            }
            else if (settings.Effect is LightingEffect.AmbientAwareness
                or LightingEffect.AudioMeter)
            {
                if (!SupportsExperimentalUniformFrames(model, enabled: true))
                {
                    throw new CapabilityUnavailableException(
                        "Uniform host-rendered lighting is not admitted for this model.");
                }
                if (recaptureFaultedBaseline || lightingOwnership.Baseline is null)
                {
                    // A readable effect after a fault is the only safe baseline
                    // for this new ownership. Never retain the former owner's
                    // stale restore destination. A replaced live owner keeps
                    // its journaled baseline: its intent is not a destination.
                    lightingOwnership.Baseline = CaptureFrameBaseline();
                }
                // A replaced live owner on the reviewed MCU images already selected
                // mode 8. The pre-drain check confirmed it, so the first sample is
                // monitored like any later one instead of re-selecting.
                bool inheritedCustomSelection = SupportsReviewedMcuKeyboardFrames(model)
                    && replacingOwnedFrame && !replacingFaultedFrame && ReviewedMcuCustomFrameSelected();
                lightingOwnership.Intent = settings with { LogoMode = null };
                SetLightingOwnership(settings.Effect == LightingEffect.AmbientAwareness
                    ? LightingRendererOwnershipState.AmbientAwareness
                    : LightingRendererOwnershipState.AudioMeter);
                lightingOwnership.UniformFrameActivated = inheritedCustomSelection ? 1 : 0;
                lightingOwnership.UniformFrameTimestamp = 0;
                hostOwnershipAcquired = true;
                await SelectRz090529VaporChamberFramesAsync(vaporChamberState, includesVaporChamber,
                    cancellationToken, () => submitted = true).ConfigureAwait(false);
                if (wakingFromDark)
                {
                    // A sampled effect starts black until its first session sample,
                    // just like the validated renderers. Hide stale firmware colors.
                    frameWriteAttempted = true;
                    await WriteUniformFrameCoreAsync(
                        new RgbColor(0, 0, 0), cancellationToken, () => submitted = true,
                        verifyVaporChamber: false)
                        .ConfigureAwait(false);
                }
                setOnlyFrame = true;
            }
            else if (settings.Effect == LightingEffect.Static && SupportsUniformFrame(model))
            {
                ExperimentalLightingBaseline frameBaseline = lightingOwnership.Baseline
                    ?? CaptureFrameBaseline();
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
                        ExperimentalBladeLightingFrame.CreateUniformRow(row, settings.Color,
                            UsesPaddedExperimentalLightingRows(model)),
                        true, cancellationToken, variableResponse: true,
                        payloadValidator: static value =>
                            value.Length <= RazerReport.MaximumPayloadLength,
                        requireEcho: false,
                        fixedTransactionId: ExperimentalLightingFrameTransactionId(model)).ConfigureAwait(false);
                    submitted = true;
                }
                RequireWriteAdmission();
                frameActivationAttempted = true;
                await ExchangeAsync("KeyboardStaticFrame", 0x03, 0x0A,
                    [5, 0], true, cancellationToken, variableResponse: true,
                    payloadValidator: static value =>
                        value.Length <= RazerReport.MaximumPayloadLength,
                    requireEcho: false,
                    fixedTransactionId: ExperimentalLightingFrameTransactionId(model)).ConfigureAwait(false);
                submitted = true;
                lightingOwnership.Baseline = frameBaseline;
                lightingOwnership.Intent = settings with { LogoMode = null };
                SetLightingOwnership(LightingRendererOwnershipState.Static);
                setOnlyFrame = true;
            }
            else if (SupportsReviewedMcuLighting(model))
            {
                if (!UsesLegacyReviewedMcuEffectSetter(model)
                    && settings.Effect == LightingEffect.Off && baseline?.Effect == LightingEffect.Off)
                {
                    // Keep the necessary overlay-clearing bridge dark. This
                    // moves the requested zero-brightness write earlier, adding
                    // no intermediate brightness target or additional write.
                    await ApplyBrightnessAsync().ConfigureAwait(false);
                }
                await SetReviewedMcuBuiltInEffectAsync(settings, baseline, baselineEffectPayload,
                    cancellationToken, () => submitted = true).ConfigureAwait(false);
                if (UsesLegacyReviewedMcuEffectSetter(model))
                {
                    // The exact legacy effect command copies saved lighting
                    // state, including brightness, into the active bank. Read
                    // that actual result before applying the requested brightness.
                    baselineRaw = await ReadBrightnessAsync(cancellationToken).ConfigureAwait(false);
                }
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
                || baseline?.Effect != settings.Effect
                || model.ProductId == 0x0255
                    && settings.Parameters is BreathingLightingParameters
                    {
                        ColorMode: BreathingColorMode.Random,
                    }
                    && baseline?.Parameters is not BreathingLightingParameters
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

            if (wakingFromDark && model.ProductId == 0x0255
                && settings.Effect is LightingEffect.Static or LightingEffect.Breathing)
            {
                // This model exposes readable color parameters. Confirm them while
                // still dark; the combined getter cannot represent a lit effect at raw zero.
                // Generic breathing already confirmed its effect above. Direct color
                // setters only acknowledged, so they still need that separate read.
                if (settings.Effect == LightingEffect.Static
                    || settings.Parameters is BreathingLightingParameters
                    { ColorMode: BreathingColorMode.Single })
                {
                    (LightingEffect confirmedEffect, _) = await ReadEffectAsync(cancellationToken)
                        .ConfigureAwait(false);
                    if (confirmedEffect != settings.Effect)
                    {
                        throw new IOException(
                            "Independent keyboard effect readback did not confirm the requested effect.");
                    }
                }
                if (settings.Effect == LightingEffect.Static)
                {
                    if (await ReadSingleZoneStaticColorAsync(cancellationToken)
                        .ConfigureAwait(false) != settings.Color)
                    {
                        throw new IOException(
                            "Independent keyboard color readback did not confirm the requested color.");
                    }
                }
                else
                {
                    // Brightness only supplies the typed parser's container here;
                    // compare the independently read parameters while raw stays zero.
                    LightingSettings confirmedBreathing = await ReadBreathingSettingsAsync(
                        settings.Brightness, cancellationToken).ConfigureAwait(false);
                    if (!Equals(confirmedBreathing.Parameters, settings.Parameters))
                    {
                        throw new IOException(
                            "Independent keyboard breathing readback did not confirm the requested parameters.");
                    }
                }
            }

            // Stage the selected content while brightness is still confirmed zero.
            // Existing visible transitions retain their established ordering.
            if (settings.Effect == LightingEffect.Off || wakingFromDark
                || UsesLegacyReviewedMcuEffectSetter(model))
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
                if (settings.Effect == LightingEffect.Static)
                {
                    RecordKeyboardMirrorFrame([settings.Color], 1, 1);
                }
                if (animationRequest)
                {
                    StartAnimationCore(lightingOwnership.Intent!, spatialEffect, sequenceEffect, standardEffect, gridEffect);
                }
                policy.Record("KeyboardLighting", "Readback", "Unavailable", submitted,
                    requested, "OwnedFrameIntent", "FrameColorReadbackUnavailable");
                mirrorCommitted = true;
                return;
            }

            (LightingSettings? confirmed, byte confirmedRaw, ReadOnlyMemory<byte> finalEffectPayload) =
                await ReadKeyboardLightingAsync(cancellationToken).ConfigureAwait(false);
            bool waveDirectionUnavailable = confirmed is null
                && finalEffectPayload.Span is [1, 5, 4] && settings.Effect == LightingEffect.Wave;
            if ((!waveDirectionUnavailable && (confirmed is null || !KeyboardSettingsMatch(confirmed, settings)))
                || confirmedRaw != raw)
            {
                throw new IOException(
                    "Independent final lighting readback did not confirm the requested state.");
            }
            if (SupportsReviewedMcuLighting(model))
            {
                if (lightingOwnership.Baseline is { } ownedBaseline)
                {
                    // Return an owned LED38 after the confirmed keyboard
                    // destination, before the durable baseline is retired.
                    await RestoreRz090529VaporChamberAsync(ownedBaseline, null, cancellationToken,
                        () => submitted = true).ConfigureAwait(false);
                }
                ClearOwnedFrame();
            }
            policy.Record("KeyboardLighting", "Readback", waveDirectionUnavailable ? "Unavailable" : "Confirmed", submitted,
                requested, waveDirectionUnavailable ? $"Wave;Raw={confirmedRaw};Direction=Unavailable" : requested,
                waveDirectionUnavailable ? "WaveDirectionReadbackUnavailable" : null);
            mirrorCommitted = true;
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
            if (newlyPendingJournal is not null && !submitted && !indeterminate)
            {
                try
                {
                    // The confirmed baseline is intact because no write was submitted.
                    newlyPendingJournal.ClearAfterVerifiedRestoration();
                    lightingOwnership.Baseline = priorBaseline;
                }
                catch (Exception cleanup) when (cleanup is IOException or InvalidDataException or UnauthorizedAccessException)
                {
                    SetLightingFaulted();
                    policy.Record("KeyboardLighting", "Baseline", "JournalClearFailed",
                        failure: cleanup.GetType().Name);
                    throw MapWriteFailure("KeyboardLighting", false, exception);
                }
            }

            // A failed replacement cannot still claim an active renderer after
            // its loop was drained. Do not restart it after lost admission.
            // A deferred ownership read ran before draining and changed nothing.
            if (!selectionCheckDeferred && (stoppedAnimation || (replacingOwnedFrame && submitted)
                || (newlyPendingJournal is not null && (submitted || indeterminate))
                || (hostOwnershipAcquired && (submitted || indeterminate))
                || frameActivationAttempted
                || frameWriteAttempted
                    && (submitted || indeterminate)))
            {
                SetLightingFaulted();
            }
            else if (hostOwnershipAcquired)
            {
                // Without a submission, LED38 can only have been released because
                // another controller took it; that durable release stands.
                bool vaporChamberReleased = priorBaseline?.VaporChamber is not null
                    && lightingOwnership.Baseline?.VaporChamber is null;
                lightingOwnership.Baseline = vaporChamberReleased
                    ? priorBaseline! with { VaporChamber = null } : priorBaseline;
                lightingOwnership.Intent = priorIntent;
                lightingOwnership.UniformFrameActivated = priorUniformFrameActivated;
                lightingOwnership.UniformFrameTimestamp = priorUniformFrameTimestamp;
                lightingOwnership.VaporChamberFrames = priorVaporChamberFrames && !vaporChamberReleased;
                // The LED38 observation needs no rollback. An owner that has not observed
                // LED38 is animated, so its failed replacement faults above, or it can no
                // longer read LED38.
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
            keyboardMirrorTransitionPending = false;
            if (mirrorCommitted) { PublishKeyboardMirrorCore(); }
            else { KeyboardLightingFrameStream.Clear(this); }
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
                WaveLightingParameters { Direction: LightingDirection.Rightward }
                || (model.ProductId == 0x02A0 || SupportsExperimentalGridAnimation(model) || SupportsReviewedMcuLighting(model))
                    && settings.Parameters is WaveLightingParameters,
            LightingEffect.Sequence => settings.Parameters is SequenceLightingParameters sequence
                && (model.ProductId == 0x02A0 && sequence.LayoutId == KeyboardLightingLayoutIds.Rz09_0484
                    || model.ProductId == 0x02C5 && sequence.LayoutId == KeyboardLightingLayoutIds.Rz09_0530
                    || model.ProductId == 0x02C7 && sequence.LayoutId == KeyboardLightingLayoutIds.Rz09_0529),
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
        byte[] arguments = SupportsReviewedMcuLighting(model) ? CreateReviewedMcuEffectArguments(settings) : settings.Effect switch
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
        byte storage = LogoStorageSelector(model);
        if (logo != LogoLightingMode.Off)
        {
            RequireWriteAdmission();
            await ExchangeAsync("LogoLighting", 0x03, 0x02,
                LogoLightingProtocol.SetEffectArguments(logo, storage), true, cancellationToken)
                .ConfigureAwait(false);
            markSubmitted();
        }
        RequireWriteAdmission();
        await ExchangeAsync("LogoLighting", 0x03, 0x00,
            LogoLightingProtocol.SetStateArguments(logo, storage), true, cancellationToken)
            .ConfigureAwait(false);
        markSubmitted();
        if (await ReadLogoAsync(cancellationToken).ConfigureAwait(false) != logo)
        {
            throw new IOException(
                "Independent logo readback did not confirm the requested mode.");
        }
    }

    // Synapse product 672 defaults logo lighting to profile 0, and the exact
    // RZ09-0484 capture observes selector 0 for setters. Queries follow that
    // pinned register for independent readback. See docs/blade18-observer-evidence.md.
    private static byte LogoStorageSelector(ExperimentalBladeModel model) =>
        model.ProductId == 0x02A0 || SupportsSourceReviewedLogo(model)
            ? (byte)0 : LogoLightingProtocol.PersistentStorage;

    private static bool KeyboardSettingsMatch(
        LightingSettings confirmed,
        LightingSettings requested) =>
        confirmed.Effect == requested.Effect
        && confirmed.Brightness == requested.Brightness
        && confirmed.Color == requested.Color
        && Equals(confirmed.Parameters, requested.Parameters);

    private void SetLightingOwnership(LightingRendererOwnershipState value)
    {
        Volatile.Write(ref lightingOwnership.Ownership, (int)value);
        // A refused-read window belongs to one owner.
        ResetReviewedMcuSelectionDeferral();
        if (value is LightingRendererOwnershipState.None or LightingRendererOwnershipState.Faulted)
        {
            keyboardMirrorFrame = null;
            KeyboardLightingFrameStream.Clear(this);
        }
    }

    private void RecordKeyboardMirrorFrame(IReadOnlyList<RgbColor> colors, int columns, int rows)
    {
        keyboardMirrorFrame = colors;
        keyboardMirrorColumns = columns;
        keyboardMirrorRows = rows;
        PublishKeyboardMirrorCore();
    }

    private void PublishKeyboardMirrorCore()
    {
        if (!keyboardMirrorTransitionPending && !disposed && IsOwnedFrameOwnership(OwnershipState)
            && keyboardMirrorFrame is { } colors)
        {
            KeyboardLightingFrameStream.Publish(colors, keyboardMirrorColumns, keyboardMirrorRows, this);
        }
    }

    private void SetLightingFaulted()
    {
        lightingOwnership.UniformFrameActivated = 0;
        SetLightingOwnership(LightingRendererOwnershipState.Faulted);
    }

    private static bool IsOwnedFrameOwnership(LightingRendererOwnershipState value) =>
        value is LightingRendererOwnershipState.Static
            or LightingRendererOwnershipState.AmbientAwareness
            or LightingRendererOwnershipState.AudioMeter
            or LightingRendererOwnershipState.Fire
            or LightingRendererOwnershipState.Starlight
            or LightingRendererOwnershipState.Reactive
            or LightingRendererOwnershipState.Ripple
            or LightingRendererOwnershipState.Tidal
            or LightingRendererOwnershipState.Sequence
            or LightingRendererOwnershipState.Spectrum
            or LightingRendererOwnershipState.Breathing
            or LightingRendererOwnershipState.Wave
            or LightingRendererOwnershipState.Wheel;

    private void RequireLightingJournalAdmission(bool hostFrameRequest, bool faultedBaselineStillReported = false)
    {
        if (lightingOwnership.Journal?.Load().Invalid == true)
        {
            lightingOwnership.JournalInvalid = true;
        }
        if (lightingOwnership.JournalInvalid)
        {
            throw new CapabilityUnavailableException(
                "The lighting recovery journal is invalid or unreadable. Repair the protected OpenBlade data storage and restart the OpenBlade service.");
        }
        // A reviewed MCU that freshly reports exactly the journaled baseline can
        // hand that baseline to the new renderer; see McuKeyboardFrames.
        if (hostFrameRequest && OwnershipState == LightingRendererOwnershipState.Faulted
            && lightingOwnership.Journal is not null && lightingOwnership.Baseline is not null
            && !faultedBaselineStillReported)
        {
            throw new CapabilityUnavailableException(
                "Lighting recovery requires a confirmed built-in destination before starting another renderer.");
        }
    }

    private void ClearOwnedFrame()
    {
        if (lightingOwnership.Baseline is not null && lightingOwnership.Journal is { } journal)
        {
            // This method follows independent built-in confirmation. A failed
            // durable clear must retain the baseline and fault the owner.
            journal.ClearAfterVerifiedRestoration();
        }
        lightingOwnership.Baseline = null;
        lightingOwnership.Intent = null;
        lightingOwnership.UniformFrameActivated = 0;
        lightingOwnership.McuCustomFrameMayRemain = false;
        // The next host-frame baseline resets its own LED38 observation.
        lightingOwnership.VaporChamberFrames = false;
        SetLightingOwnership(LightingRendererOwnershipState.None);
    }

    // Decide whether these RZ09-0529 frames also drive LED38, from a read made in
    // this same operation. LED38 is taken only while it reads custom mode 8 under
    // OpenBlade's selection record, or still equals the restore destination: the
    // record first observed for this baseline. Anything else means another
    // controller changed it, so a selected LED38 is released without a write. A
    // keyboard-only effect returns an owned LED38 first.
    private async Task<bool> SelectRz090529VaporChamberFramesAsync(
        (LightingSettings? Settings, byte Mode)? current, bool includesVaporChamber,
        CancellationToken token, Action markSubmitted)
    {
        bool selected = lightingOwnership.Baseline?.VaporChamber is not null;
        bool owned = selected && current?.Mode == 8;
        LightingSettings? destination = lightingOwnership.Baseline?.VaporChamber
            ?? lightingOwnership.VaporChamberDestination;
        bool frames = includesVaporChamber && current is { } state
            && (owned || destination is not null && state.Settings is { } actual
                && KeyboardSettingsMatch(actual, destination));
        if (selected && !frames && current is { } observed)
        {
            await RestoreRz090529VaporChamberAsync(lightingOwnership.Baseline!, observed.Mode, token,
                markSubmitted).ConfigureAwait(false);
        }
        lightingOwnership.VaporChamberFrames = frames;
        return frames;
    }

    private async Task RestoreOwnedFrameAsync(
        CancellationToken cancellationToken,
        Action markSubmitted)
    {
        ExperimentalLightingBaseline baseline = lightingOwnership.Baseline
            ?? throw new IOException("The owned static frame baseline is unavailable.");
        RequireLightingJournalAdmission(hostFrameRequest: false);
        byte currentRaw = await ReadBrightnessAsync(cancellationToken).ConfigureAwait(false);

        if (SupportsReviewedMcuLighting(model))
        {
            // Read actual active mode, not the saved restore destination. Mode8
            // -> Off clears the keyboard overlay directly; a saved Off baseline
            // must not accidentally create a same-Off prerequisite.
            ReadOnlyMemory<byte> current = await ReadReviewedMcuEffectPayloadAsync(cancellationToken)
                .ConfigureAwait(false);
            bool effectAlreadyRestored = false;
            if (SupportsReviewedMcuKeyboardFrames(model)
                && (current.Span[2] != 8 || !ReviewedMcuCustomFrameSelected()))
            {
                // The current owner may not have selected mode 8 itself, but an
                // earlier owner of this baseline, a faulted one or one from an
                // earlier run may have. Its frame can remain under the saved effect.
                bool selected = ReviewedMcuCustomFrameSelected() || lightingOwnership.McuCustomFrameMayRemain;
                if (!ReviewedMcuEffectMatchesBaseline(current.Span, baseline))
                {
                    // The firmware or another controller changed the effect; its
                    // custom flag may still show this owner's frame. Write nothing.
                    policy.Record("KeyboardStaticFrame", "Restore", "Skipped",
                        requested: baseline.Settings.Effect.ToString(), confirmed: $"Mode{current.Span[2]}",
                        failure: "ExternallyChanged");
                    throw new IOException(selected
                        ? "The keyboard left custom lighting outside OpenBlade. Choose a lighting effect to restore it."
                        : "Another app changed the keyboard lighting, so OpenBlade left it unchanged. Choose a lighting effect to continue.");
                }
                // The reported effect is exactly the journaled destination. Only
                // when mode 8 was never selected for this baseline is no write needed.
                // After a GPIO edge the flag and staging may remain, so the
                // flag-clearing setter still completes a selected owner's restore.
                effectAlreadyRestored = !selected;
                if (selected)
                {
                    policy.Record("KeyboardStaticFrame", "Restore", "ExternallyRestoredToBaseline",
                        requested: baseline.Settings.Effect.ToString(), confirmed: $"Mode{current.Span[2]}");
                }
            }
            if (!effectAlreadyRestored)
            {
                LightingSettings? actual = DecodeReviewedMcuEffect(current.Span,
                    checked((byte)LightingBrightnessProtocol.RawToPercent(currentRaw)));
                await SetReviewedMcuBuiltInEffectAsync(baseline.Settings, actual, current,
                    cancellationToken, markSubmitted).ConfigureAwait(false);
            }
            if (!effectAlreadyRestored && UsesLegacyReviewedMcuEffectSetter(model))
            {
                // The legacy setter copies saved brightness into the active bank.
                // Restore the original value only if that copy changed it.
                currentRaw = await ReadBrightnessAsync(cancellationToken).ConfigureAwait(false);
            }
            if (currentRaw != baseline.RawBrightness)
            {
                RequireWriteAdmission();
                await ExchangeAsync("KeyboardBrightness", 0x03, 0x03,
                    [0, 5, baseline.RawBrightness], true, cancellationToken).ConfigureAwait(false);
                markSubmitted();
            }
            if (await ReadBrightnessAsync(cancellationToken).ConfigureAwait(false) != baseline.RawBrightness)
            {
                throw new IOException("Independent active brightness readback did not confirm restoration.");
            }
            await RestoreRz090529VaporChamberAsync(baseline, null, cancellationToken, markSubmitted)
                .ConfigureAwait(false);
            ClearOwnedFrame();
            return;
        }

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

    public Task PushAmbientLightingFrameAsync(
        AmbientLightingFrame frame,
        CancellationToken cancellationToken) =>
        Task.FromException(new CapabilityUnavailableException(
            "The shared ambient frame layout is not admitted for this experimental model."));

    public async Task PushUniformLightingFrameAsync(
        UniformLightingFrame frame,
        CancellationToken cancellationToken)
    {
        recoveryWriteGate.ThrowIfBlocked();
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

            await WriteUniformFrameCoreAsync(
                frame.Color, cancellationToken, () => submitted = true).ConfigureAwait(false);
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

    private async Task WriteUniformFrameCoreAsync(
        RgbColor color,
        CancellationToken cancellationToken,
        Action onSubmitted,
        bool verifyVaporChamber = true)
    {
        bool activate = lightingOwnership.UniformFrameActivated == 0;
        if (activate && verifyVaporChamber && lightingOwnership.VaporChamberFrames
            && SupportsRz090529VaporChamberFrames(model))
        {
            // The owner chose LED38 in an earlier operation. Select it only from a
            // read made under this same gate hold.
            bool selected = lightingOwnership.Baseline?.VaporChamber is not null;
            (LightingSettings? Settings, byte Mode)? current = selected || VaporChamberReadAvailable
                ? await ReadVaporChamberForFramesAsync(selected, cancellationToken).ConfigureAwait(false)
                : null;
            await SelectRz090529VaporChamberFramesAsync(current, includesVaporChamber: true,
                cancellationToken, onSubmitted).ConfigureAwait(false);
        }
        if (!activate)
        {
            // Bound all source-reviewed uniform renderers to a deliberate
            // software budget; this does not establish physical model timing.
            TimeSpan remaining = Blade18LightingAnimation.FrameInterval
                - animationTime.GetElapsedTime(lightingOwnership.UniformFrameTimestamp);
            if (remaining > TimeSpan.Zero)
            {
                await Task.Delay(remaining, animationTime, cancellationToken)
                    .ConfigureAwait(false);
            }
        }
        long submittedAt = animationTime.GetTimestamp();
        if (model.ProductId == 0x02C7)
        {
            var colors = new RgbColor[Rz090529KeyboardLightingFrame.Rows * Rz090529KeyboardLightingFrame.Columns];
            Array.Fill(colors, color);
            // LED38 keeps its own record brightness, so follow the keyboard in RGB.
            RgbColor[]? vaporChamber = lightingOwnership.VaporChamberFrames
                ? Enumerable.Repeat(Rz090529KeyboardLightingFrame.ScaleVaporChamberColor(color,
                    lightingOwnership.Intent?.Brightness ?? 100),
                    Rz090529KeyboardLightingFrame.VaporChamberColumns).ToArray()
                : null;
            await SubmitExperimentalGridFrameAsync(colors, vaporChamber, activate, cancellationToken, onSubmitted)
                .ConfigureAwait(false);
            lightingOwnership.UniformFrameActivated = 1;
            lightingOwnership.UniformFrameTimestamp = submittedAt;
            return;
        }
        if (SupportsReviewedMcuKeyboardFrames(model))
        {
            var colors = new RgbColor[ExperimentalBladeLightingFrame.RowCount * ExperimentalBladeLightingFrame.ColumnCount];
            Array.Fill(colors, color);
            await SubmitExperimentalGridFrameAsync(colors, null, activate, cancellationToken, onSubmitted)
                .ConfigureAwait(false);
            lightingOwnership.UniformFrameActivated = 1;
            lightingOwnership.UniformFrameTimestamp = submittedAt;
            return;
        }
        if (model.ProductId == 0x02A0)
        {
            var colors = new RgbColor[Blade18LightingFrame.SlotCount];
            Array.Fill(colors, color);
            await SubmitBlade18FrameAsync(
                new Blade18LightingFrame(colors),
                activate,
                cancellationToken).ConfigureAwait(false);
            onSubmitted();
            lightingOwnership.UniformFrameActivated = 1;
            lightingOwnership.UniformFrameTimestamp = submittedAt;
            return;
        }

        for (int row = 0; row < ExperimentalBladeLightingFrame.RowCount; row++)
        {
            RequireWriteAdmission();
            await ExchangeAsync("KeyboardUniformFrame", 0x03, 0x0B,
                ExperimentalBladeLightingFrame.CreateUniformRow(row, color,
                    UsesPaddedExperimentalLightingRows(model)),
                true, cancellationToken, variableResponse: true,
                payloadValidator: static value =>
                    value.Length <= RazerReport.MaximumPayloadLength,
                requireEcho: false,
                recordAcknowledgedSubmission: false,
            fixedTransactionId: ExperimentalLightingFrameTransactionId(model)).ConfigureAwait(false);
            onSubmitted();
        }
        RequireWriteAdmission();
        await ExchangeAsync("KeyboardUniformFrame", 0x03, 0x0A,
            [5, 0], true, cancellationToken, variableResponse: true,
            payloadValidator: static value =>
                value.Length <= RazerReport.MaximumPayloadLength,
            requireEcho: false,
            recordAcknowledgedSubmission: false,
                fixedTransactionId: ExperimentalLightingFrameTransactionId(model)).ConfigureAwait(false);
        onSubmitted();
        lightingOwnership.UniformFrameActivated = 1;
        lightingOwnership.UniformFrameTimestamp = submittedAt;
        RecordKeyboardMirrorFrame([color], 1, 1);
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
        if (SupportsOnlyIndependentLighting(model))
        {
            // Independent controls never acquire an effect renderer.
            return OwnershipState;
        }
        await operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        bool submitted = false;
        LightingRendererOwnershipState observed = OwnershipState;
        try
        {
            await StopAnimationCoreAsync().ConfigureAwait(false);
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
            await StopAnimationCoreAsync().ConfigureAwait(false);
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
