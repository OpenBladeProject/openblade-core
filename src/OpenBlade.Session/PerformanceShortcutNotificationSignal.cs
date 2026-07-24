using OpenBlade.Contracts;

namespace OpenBlade.Session;

public enum PerformanceShortcutNotificationKind : byte
{
    CurrentMode = 1,
    Failed = 2,
}

public readonly record struct PerformanceShortcutNotificationSignal(
    PerformanceShortcutNotificationKind Kind,
    PerformancePreset? Preset)
{
    private const int PresetShift = 8;

    public nuint Encode()
    {
        if (!Enum.IsDefined(Kind))
        {
            throw new ArgumentOutOfRangeException(nameof(Kind));
        }
        if (Preset is { } preset && !Enum.IsDefined(preset))
        {
            throw new ArgumentOutOfRangeException(nameof(Preset));
        }

        uint presetValue = Preset is { } value
            ? checked((uint)value + 1)
            : 0;
        return (nuint)((uint)Kind | (presetValue << PresetShift));
    }

    public static bool TryDecode(
        nuint encoded,
        out PerformanceShortcutNotificationSignal signal)
    {
        signal = default;
        if (encoded > uint.MaxValue)
        {
            return false;
        }

        uint value = (uint)encoded;
        if ((value & 0xFFFF0000u) != 0)
        {
            return false;
        }

        var kind = (PerformanceShortcutNotificationKind)(value & 0xFFu);
        if (!Enum.IsDefined(kind))
        {
            return false;
        }

        uint encodedPreset = (value >> PresetShift) & 0xFFu;
        PerformancePreset? preset = null;
        if (encodedPreset != 0)
        {
            var decodedPreset = (PerformancePreset)(encodedPreset - 1);
            if (!Enum.IsDefined(decodedPreset))
            {
                return false;
            }
            preset = decodedPreset;
        }

        if (kind == PerformanceShortcutNotificationKind.Failed && preset is not null)
        {
            return false;
        }

        signal = new PerformanceShortcutNotificationSignal(kind, preset);
        return true;
    }
}
