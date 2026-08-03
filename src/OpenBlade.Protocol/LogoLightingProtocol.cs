using OpenBlade.Contracts;

namespace OpenBlade.Protocol;

internal static class LogoLightingProtocol
{
    internal const byte CommandClass = 0x03;
    internal const byte SetStateCommandId = 0x00;
    internal const byte SetEffectCommandId = 0x02;
    internal const byte QueryStateCommandId = 0x80;
    internal const byte QueryEffectCommandId = 0x82;
    internal const byte PersistentStorage = 0x01;
    internal const byte LogoLedId = 0x04;
    internal const byte Disabled = 0x00;
    internal const byte Enabled = 0x01;
    internal const byte StaticEffect = 0x00;
    internal const byte BreathingEffect = 0x02;

    internal static byte[] QueryArguments() =>
        QueryArguments(PersistentStorage);

    internal static byte[] QueryArguments(byte storage) =>
        [storage, LogoLedId, 0x00];

    internal static byte[] SetEffectArguments(LogoLightingMode mode) =>
        SetEffectArguments(mode, PersistentStorage);

    internal static byte[] SetEffectArguments(
        LogoLightingMode mode,
        byte storage) =>
        [storage, LogoLedId, EffectFor(mode)];

    internal static byte[] SetStateArguments(LogoLightingMode mode) =>
        SetStateArguments(mode, PersistentStorage);

    internal static byte[] SetStateArguments(
        LogoLightingMode mode,
        byte storage) =>
        [storage, LogoLedId, mode == LogoLightingMode.Off ? Disabled : Enabled];

    internal static bool TryDecodeState(ReadOnlySpan<byte> payload, out bool enabled)
        => TryDecodeState(payload, PersistentStorage, out enabled);

    internal static bool TryDecodeState(
        ReadOnlySpan<byte> payload,
        byte storage,
        out bool enabled)
    {
        enabled = false;
        if (!TryDecodeValue(payload, storage, out byte value)
            || value is not (Disabled or Enabled))
        {
            return false;
        }

        enabled = value == Enabled;
        return true;
    }

    internal static bool TryDecodeEffect(ReadOnlySpan<byte> payload, out byte effect) =>
        TryDecodeEffect(payload, PersistentStorage, out effect);

    internal static bool TryDecodeEffect(
        ReadOnlySpan<byte> payload,
        byte storage,
        out byte effect) =>
        TryDecodeValue(payload, storage, out effect);

    internal static bool TryDecodeMode(
        ReadOnlySpan<byte> statePayload,
        ReadOnlySpan<byte> effectPayload,
        out LogoLightingMode mode)
        => TryDecodeMode(
            statePayload,
            effectPayload,
            PersistentStorage,
            out mode);

    internal static bool TryDecodeMode(
        ReadOnlySpan<byte> statePayload,
        ReadOnlySpan<byte> effectPayload,
        byte storage,
        out LogoLightingMode mode)
    {
        mode = default;
        if (!TryDecodeState(statePayload, storage, out bool enabled)
            || !TryDecodeEffect(effectPayload, storage, out byte effect))
        {
            return false;
        }

        if (!enabled)
        {
            mode = LogoLightingMode.Off;
            return true;
        }

        mode = effect switch
        {
            StaticEffect => LogoLightingMode.Static,
            BreathingEffect => LogoLightingMode.Breathing,
            _ => default,
        };
        return effect is StaticEffect or BreathingEffect;
    }

    internal static bool Matches(ReadOnlySpan<byte> payload, ReadOnlySpan<byte> expected) =>
        payload.SequenceEqual(expected);

    private static bool TryDecodeValue(
        ReadOnlySpan<byte> payload,
        byte storage,
        out byte value)
    {
        value = 0;
        if (payload.Length != 3
            || payload[0] != storage
            || payload[1] != LogoLedId)
        {
            return false;
        }

        value = payload[2];
        return true;
    }

    private static byte EffectFor(LogoLightingMode mode) => mode switch
    {
        LogoLightingMode.Static or LogoLightingMode.Off => StaticEffect,
        LogoLightingMode.Breathing => BreathingEffect,
        _ => throw new ArgumentOutOfRangeException(nameof(mode)),
    };
}
