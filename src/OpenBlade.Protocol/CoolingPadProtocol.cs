using OpenBlade.Contracts;

namespace OpenBlade.Protocol;

/// <summary>
/// Product 3907 vendor middleware: thermal modules 63472/91655 and rzDevice25 matrix commands.
/// Commands describe configured state, never measured fan speed. See docs/cooling-pad.md.
/// </summary>
internal static class CoolingPadProtocol
{
    internal const int LedCount = 18;
    internal const int MaximumConfiguredRpm = 3200;

    /// <summary>
    /// Firmware 1.10.00 (class 0D handler, image 0x3228) keeps one selection per owner:
    /// selector 0 is the firmware-owned preset and 1 the Synapse-owned preset. The GET echoes the
    /// selector and returns that owner's last selection, with its speed only for Low/Medium/High.
    /// The running preset is whichever owner set it last; no command reports which that was.
    /// </summary>
    internal static RazerCommandSpec FanQuery(CoolingPadFanOwnership owner = CoolingPadFanOwnership.Firmware) =>
        Enum.IsDefined(owner)
            ? new(0x0D, 0x90, [(byte)owner, 0, 0], 3, p => TryDecodeFan(p.Span, owner, out _)) { AllowVariableResponseLength = true }
            : throw new ArgumentOutOfRangeException(nameof(owner));
    /// <summary>
    /// Image 0x3190 reports the ramping fan speed, and the target only once the two are within
    /// 50 RPM. It cannot confirm a newly requested target.
    /// </summary>
    internal static RazerCommandSpec TargetQuery() => new(0x0D, 0x81, [1, 5, 0], 3,
        p => p.Span is [1, 5, <= 64]);
    internal static RazerCommandSpec DeviceModeQuery() => new(0, 0x84, [0, 0], 2,
        p => p.Span is [0 or 3, 0]);
    internal static RazerCommandSpec DeviceModeSet(CoolingPadDeviceMode mode) =>
        Enum.IsDefined(mode) ? Echo(0, 4, [(byte)mode, 0]) : throw new ArgumentOutOfRangeException(nameof(mode));

    internal static bool TryDecodeFan(ReadOnlySpan<byte> p, CoolingPadFanOwnership owner, out CoolingPadFanConfiguration? state)
    {
        state = null;
        // Low/Medium/High (0..2) reply with three bytes including the speed; other presets with two.
        if (p.Length is not (2 or 3) || p[0] != (byte)owner || p[1] > 6 || (p[1] < 3) != (p.Length == 3)
            || p.Length == 3 && p[2] > 64)
        { return false; }
        state = new(owner, (CoolingPadFanPreset)p[1], p.Length == 3 ? p[2] * 50 : 0);
        return true;
    }

    internal static RazerCommandSpec FanSet(CoolingPadFanConfiguration state)
    {
        ArgumentNullException.ThrowIfNull(state);
        // Image 0x3290: the Synapse owner accepts only Low/Medium/High; those three need a 500..3200 RPM
        // speed. Other presets ignore the speed byte and read back without one.
        bool speedPreset = state.Preset is CoolingPadFanPreset.Low or CoolingPadFanPreset.Medium or CoolingPadFanPreset.High;
        if (!Enum.IsDefined(state.Ownership) || !Enum.IsDefined(state.Preset)
            || state.Ownership == CoolingPadFanOwnership.Synapse && !speedPreset
            || (speedPreset ? state.ConfiguredRpm is < 500 or > MaximumConfiguredRpm : state.ConfiguredRpm != 0)
            || state.ConfiguredRpm % 50 != 0)
        { throw new ArgumentOutOfRangeException(nameof(state)); }
        return Echo(0x0D, 0x10, [(byte)state.Ownership, (byte)state.Preset, (byte)(state.ConfiguredRpm / 50)]);
    }

    internal static RazerCommandSpec DemandSet(int rpm)
    {
        ValidateTarget(rpm);
        return Echo(0x0D, 1, [1, 5, (byte)(rpm / 50)]);
    }

    internal static void ValidateTarget(int rpm)
    {
        if (rpm is < 500 or > MaximumConfiguredRpm || rpm % 50 != 0)
        { throw new ArgumentOutOfRangeException(nameof(rpm)); }
    }

    internal static RazerCommandSpec RegionQuery() => new(0x0F, 0x80, new byte[80], 80,
        p => TryDecodeRegion(p.Span, out _))
    { AllowVariableResponseLength = true };

    internal static bool TryDecodeRegion(ReadOnlySpan<byte> p, out byte region)
    {
        region = 0;
        // Firmware 1.10.00 (image 0x657C) answers with one five-byte record [05,19,03,01,12]:
        // region 5, then one row at byte 3 and 18 LEDs at byte 4. Only that single strip is admitted.
        if (p.Length is < 5 or > 80 || p.Length % 5 != 0 || p[3] != 1 || p[4] != LedCount) { return false; }
        region = p[0];
        return true;
    }

    internal static RazerCommandSpec BrightnessQuery(byte region) => new(0x0F, 0x84, [1, region, 0], 3,
        p => p.Length == 3 && p.Span[0] == 1 && p.Span[1] == region);
    internal static RazerCommandSpec BrightnessSet(byte raw) => Echo(0x0F, 4, [1, 0, raw]);
    internal static RazerCommandSpec EffectQuery(byte profile = 1) => new(0x0F, 0x82, CreateEffectQueryPayload(profile), 80,
        p => IsEffectReply(p.Span, profile))
    { AllowVariableResponseLength = true };

    /// <summary>
    /// A well-formed effect record. Firmware 1.10.00 also has effects 7 and 9, which the pad's own
    /// lighting button selects; they are not decoded or admitted, so their record is accepted as
    /// unknown lighting instead of failing the whole read. Any other effect id is a bad reply.
    /// </summary>
    internal static bool IsEffectReply(ReadOnlySpan<byte> p, byte profile) =>
        TryDecodeEffect(p, profile, out _)
        || profile <= 1 && p.Length == EffectResponseLength && p[0] == 0 && p[2] is 7 or 9;

    private static byte[] CreateEffectQueryPayload(byte profile)
    {
        byte[] payload = new byte[80];
        if (profile > 1) { throw new ArgumentOutOfRangeException(nameof(profile)); }
        payload[0] = profile;
        return payload;
    }

    /// <summary>
    /// Decodes the firmware 1.10.00 effect GET (image 0x65BA): twelve bytes
    /// [0, stored region id, effect, flags, rate, count, color slot 1, color slot 2].
    /// Byte 0 is a constant zero, not the profile, so the queried profile is supplied by the caller.
    /// The firmware selects the record from the request's region, so the stored region id is not
    /// interpreted. Slots beyond the count keep earlier bytes and are not part of the effect.
    /// </summary>
    internal static bool TryDecodeEffect(ReadOnlySpan<byte> p, byte profile, out CoolingPadLightingConfiguration? effect)
    {
        effect = null;
        if (profile > 1 || p.Length != EffectResponseLength || p[0] != 0
            || p[2] is not (0 or 1 or 2 or 3 or 4 or 8) || p[5] > 2)
        { return false; }
        var colors = new CoolingPadColor[p[5]];
        for (int i = 0; i < colors.Length; i++)
        { colors[i] = new(p[6 + i * 3], p[7 + i * 3], p[8 + i * 3]); }
        effect = new(profile, 0, p[2], p[3], p[4], Array.AsReadOnly(colors));
        return true;
    }

    private const int EffectResponseLength = 12;

    /// <summary>
    /// Compares a requested effect with readback using only the fields the firmware stores
    /// (image 0x6E40). Effect 8 stores only its effect id. Other effects store flags, rate and
    /// count; color slot 1 only for Static (1); color slot 2 from every request.
    /// </summary>
    internal static bool ConfirmsEffect(CoolingPadLightingConfiguration requested, CoolingPadLightingConfiguration readback)
    {
        ArgumentNullException.ThrowIfNull(requested);
        ArgumentNullException.ThrowIfNull(readback);
        if (requested.Profile != readback.Profile || requested.Region != readback.Region || requested.Effect != readback.Effect)
        { return false; }
        if (requested.Effect == 8) { return true; }
        if (requested.Flags != readback.Flags || requested.Rate != readback.Rate
            || requested.Colors.Count != readback.Colors.Count)
        { return false; }
        return (requested.Effect != 1 || requested.Colors.Count == 0 || requested.Colors[0] == readback.Colors[0])
            && (requested.Colors.Count < 2 || requested.Colors[1] == readback.Colors[1]);
    }

    internal static RazerCommandSpec EffectSet(CoolingPadLightingConfiguration effect)
    {
        ArgumentNullException.ThrowIfNull(effect);
        if (effect.Profile is not (0 or 1) || effect.Region != 0
            || effect.Effect is not (0 or 1 or 2 or 3 or 4 or 8) || effect.Colors is null || effect.Colors.Count > 2)
        { throw new ArgumentOutOfRangeException(nameof(effect)); }
        byte[] payload = new byte[6 + effect.Colors.Count * 3];
        payload[0] = effect.Profile; payload[1] = effect.Region; payload[2] = effect.Effect;
        payload[3] = effect.Flags; payload[4] = effect.Rate; payload[5] = (byte)effect.Colors.Count;
        for (int i = 0; i < effect.Colors.Count; i++)
        {
            payload[6 + i * 3] = effect.Colors[i].R;
            payload[7 + i * 3] = effect.Colors[i].G;
            payload[8 + i * 3] = effect.Colors[i].B;
        }
        return Echo(0x0F, 2, payload);
    }

    internal static CoolingPadLightingConfiguration EffectFor(CoolingPadSettings settings) => settings.LightingMode switch
    {
        CoolingPadLightingMode.Off => new(1, 0, 0, 0, 0, []),
        CoolingPadLightingMode.Static => new(1, 0, 1, 0, 0, [settings.Color]),
        CoolingPadLightingMode.Breathing => new(1, 0, 2, 0, 0,
            settings.SecondaryColor is { } second ? [settings.Color, second] : [settings.Color]),
        CoolingPadLightingMode.Spectrum => new(1, 0, 3, 0, 0, []),
        CoolingPadLightingMode.Wave => new(1, 0, 4, (byte)settings.WaveDirection, 40, []),
        CoolingPadLightingMode.KeyboardMirror => new(0, 0, 8, 0, 0, []),
        _ => throw new ArgumentOutOfRangeException(nameof(settings)),
    };

    internal static RazerCommandSpec FrameSet(IReadOnlyList<CoolingPadColor> colors)
    {
        ArgumentNullException.ThrowIfNull(colors);
        if (colors.Count != LedCount) { throw new ArgumentException("A pad frame requires 18 colors.", nameof(colors)); }
        byte[] payload = new byte[5 + LedCount * 3];
        payload[4] = LedCount - 1;
        for (int i = 0; i < LedCount; i++)
        { payload[5 + i * 3] = colors[i].R; payload[6 + i * 3] = colors[i].G; payload[7 + i * 3] = colors[i].B; }
        return Echo(0x0F, 3, payload);
    }

    internal static byte PercentToRaw(int percent) => percent is >= 0 and <= 100
        ? (byte)(percent * 255 / 100) : throw new ArgumentOutOfRangeException(nameof(percent));
    internal static int RawToPercent(byte raw) => (raw * 100 + 254) / 255;
    private static RazerCommandSpec Echo(byte cls, byte id, byte[] data) =>
        new(cls, id, data, data.Length, p => p.IsEmpty || p.Span.SequenceEqual(data)) { AllowVariableResponseLength = true };
}
