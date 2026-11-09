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

    internal static RazerCommandSpec FanQuery() => new(0x0D, 0x90, [0, 0, 0], 3,
        p => TryDecodeFan(p.Span, out _));
    internal static RazerCommandSpec TargetQuery() => new(0x0D, 0x81, [1, 5, 0], 3,
        p => p.Span is [1, 5, <= 64]);
    internal static RazerCommandSpec DeviceModeQuery() => new(0, 0x84, [0, 0], 2,
        p => p.Span is [0 or 3, 0]);
    internal static RazerCommandSpec DeviceModeSet(CoolingPadDeviceMode mode) =>
        Enum.IsDefined(mode) ? Echo(0, 4, [(byte)mode, 0]) : throw new ArgumentOutOfRangeException(nameof(mode));

    internal static bool TryDecodeFan(ReadOnlySpan<byte> p, out CoolingPadFanConfiguration? state)
    {
        state = null;
        if (p.Length != 3 || p[0] > 1 || p[1] > 6 || p[2] > 64) { return false; }
        state = new((CoolingPadFanOwnership)p[0], (CoolingPadFanPreset)p[1], p[2] * 50);
        return true;
    }

    internal static RazerCommandSpec FanSet(CoolingPadFanConfiguration state)
    {
        ArgumentNullException.ThrowIfNull(state);
        if (!Enum.IsDefined(state.Ownership) || !Enum.IsDefined(state.Preset)
            || state.ConfiguredRpm is < 0 or > MaximumConfiguredRpm || state.ConfiguredRpm % 50 != 0)
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
        // The source consumes records of five bytes. Only the reviewed single 18-LED strip is admitted.
        if (p.Length is < 5 or > 80 || p.Length % 5 != 0 || p[3] != LedCount || p[4] != 1) { return false; }
        region = p[0];
        return true;
    }

    internal static RazerCommandSpec BrightnessQuery(byte region) => new(0x0F, 0x84, [1, region, 0], 3,
        p => p.Length == 3 && p.Span[0] == 1 && p.Span[1] == region);
    internal static RazerCommandSpec BrightnessSet(byte raw) => Echo(0x0F, 4, [1, 0, raw]);
    internal static RazerCommandSpec EffectQuery(byte profile = 1) => new(0x0F, 0x82, CreateEffectQueryPayload(profile), 80,
        p => TryDecodeEffect(p.Span, out _) && p.Span[0] == profile)
    { AllowVariableResponseLength = true };

    private static byte[] CreateEffectQueryPayload(byte profile)
    {
        byte[] payload = new byte[80];
        if (profile > 1) { throw new ArgumentOutOfRangeException(nameof(profile)); }
        payload[0] = profile;
        return payload;
    }

    internal static bool TryDecodeEffect(ReadOnlySpan<byte> p, out CoolingPadLightingConfiguration? effect)
    {
        effect = null;
        if (p.Length is < 6 or > 80 || p[0] is not (0 or 1) || p[1] != 0 || p[2] is not (0 or 1 or 2 or 3 or 4 or 8)
            || p[5] > 2 || p.Length < 6 + p[5] * 3 || p[(6 + p[5] * 3)..].IndexOfAnyExcept((byte)0) >= 0)
        { return false; }
        var colors = new CoolingPadColor[p[5]];
        for (int i = 0; i < colors.Length; i++)
        { colors[i] = new(p[6 + i * 3], p[7 + i * 3], p[8 + i * 3]); }
        effect = new(p[0], p[1], p[2], p[3], p[4], Array.AsReadOnly(colors));
        return true;
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
