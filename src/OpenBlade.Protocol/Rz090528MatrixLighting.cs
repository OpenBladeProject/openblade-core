using OpenBlade.Contracts;

namespace OpenBlade.Protocol;

/// <summary>
/// Exact six-row host-rendered matrix observed on RZ09-0528 / PID 02C6.
/// The slot order is protocol order and is not a claimed physical key map.
/// </summary>
internal sealed class Rz090528MatrixFrame
{
    internal const int RowCount = 6;
    internal const int ColumnCount = 17;
    internal const int SlotCount = RowCount * ColumnCount;

    private static readonly int[] CapturedHoles =
        [0, 17, 31, 34, 48, 51, 64, 65, 68, 70, 81, 82, 85, 89, 92, 93];
    private static readonly bool[] HoleMask = CreateHoleMask();
    private readonly RgbColor[] slots;

    internal Rz090528MatrixFrame(ReadOnlySpan<RgbColor> values)
    {
        if (values.Length != SlotCount)
        {
            throw new ArgumentOutOfRangeException(
                nameof(values),
                values.Length,
                $"An RZ09-0528 matrix frame must contain exactly {SlotCount} RGB slots.");
        }

        slots = values.ToArray();
    }

    internal static IReadOnlyList<int> CapturedHoleSlots => CapturedHoles;

    internal RgbColor this[int index] => slots[index];

    internal static Rz090528MatrixFrame Uniform(RgbColor color)
    {
        RgbColor[] values = new RgbColor[SlotCount];
        for (int slot = 0; slot < values.Length; slot++)
        {
            values[slot] = HoleMask[slot] ? default : color;
        }

        return new(values);
    }

    internal static bool IsHole(int slot)
    {
        if ((uint)slot >= SlotCount)
        {
            throw new ArgumentOutOfRangeException(nameof(slot));
        }

        return HoleMask[slot];
    }

    private static bool[] CreateHoleMask()
    {
        bool[] mask = new bool[SlotCount];
        foreach (int slot in CapturedHoles)
        {
            mask[slot] = true;
        }

        return mask;
    }
}

/// <summary>
/// Offline codec for the exact 55-byte PID 02C6 row payload. A complete frame
/// consists of rows 0 through 5 followed by the separately observed 03/0A
/// 0500 latch command.
/// </summary>
internal static class Rz090528MatrixRowProtocol
{
    internal const byte CommandClass = 0x03;
    internal const byte RowCommandId = 0x0B;
    internal const byte LatchCommandId = 0x0A;
    internal const int RowPayloadLength = 55;

    internal static ReadOnlyMemory<byte> LatchArguments { get; } =
        new byte[] { 0x05, 0x00 };

    internal static byte[] EncodeRow(int row, Rz090528MatrixFrame frame)
    {
        ArgumentNullException.ThrowIfNull(frame);
        if ((uint)row >= Rz090528MatrixFrame.RowCount)
        {
            throw new ArgumentOutOfRangeException(nameof(row));
        }

        byte[] payload = new byte[RowPayloadLength];
        payload[0] = 0xFF;
        payload[1] = checked((byte)row);
        payload[2] = 0x00;
        payload[3] = Rz090528MatrixFrame.ColumnCount - 1;
        int firstSlot = row * Rz090528MatrixFrame.ColumnCount;
        for (int column = 0; column < Rz090528MatrixFrame.ColumnCount; column++)
        {
            RgbColor color = frame[firstSlot + column];
            int offset = 4 + (column * 3);
            payload[offset] = color.Red;
            payload[offset + 1] = color.Green;
            payload[offset + 2] = color.Blue;
        }

        return payload;
    }
}

internal interface IRz090528LightingEffect
{
    bool RequiresContinuousRendering { get; }

    TimeSpan NextFrameDelay { get; }

    Rz090528MatrixFrame RenderFrame(TimeSpan elapsed);
}

internal sealed class Rz090528StaticEffect(RgbColor color) : IRz090528LightingEffect
{
    public bool RequiresContinuousRendering => false;

    public TimeSpan NextFrameDelay => Timeout.InfiniteTimeSpan;

    public Rz090528MatrixFrame RenderFrame(TimeSpan elapsed)
    {
        ThrowIfNegative(elapsed);
        return Rz090528MatrixFrame.Uniform(color);
    }

    private static void ThrowIfNegative(TimeSpan elapsed)
    {
        if (elapsed < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(elapsed));
        }
    }
}

internal sealed class Rz090528SpectrumEffect : IRz090528LightingEffect
{
    // The exact PID 02C6 navigation trace contains one complete uniform-color
    // cycle across 843 row-zero samples. Linear hue unwrapping estimates a
    // 34.034-second cycle; 30 fps stays below the captured host stream rate.
    internal static readonly TimeSpan CycleDuration =
        TimeSpan.FromMilliseconds(34_034);
    internal static readonly TimeSpan FrameInterval = TimeSpan.FromTicks(
        TimeSpan.TicksPerSecond / 30);

    public bool RequiresContinuousRendering => true;

    public TimeSpan NextFrameDelay => FrameInterval;

    public Rz090528MatrixFrame RenderFrame(TimeSpan elapsed)
    {
        if (elapsed < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(elapsed));
        }

        double hue = (elapsed.Ticks % CycleDuration.Ticks)
            / (double)CycleDuration.Ticks;
        return Rz090528MatrixFrame.Uniform(FromHue(hue));
    }

    private static RgbColor FromHue(double phase)
    {
        double scaled = phase * 6.0;
        int sector = (int)Math.Floor(scaled);
        double fraction = scaled - sector;
        byte rising = ToByte(fraction * 255.0);
        byte falling = ToByte((1.0 - fraction) * 255.0);
        return sector switch
        {
            0 => new(0xFF, rising, 0x00),
            1 => new(falling, 0xFF, 0x00),
            2 => new(0x00, 0xFF, rising),
            3 => new(0x00, falling, 0xFF),
            4 => new(rising, 0x00, 0xFF),
            _ => new(0xFF, 0x00, falling),
        };
    }

    private static byte ToByte(double value) =>
        checked((byte)Math.Clamp((int)Math.Round(value), 0, 255));
}
