using OpenBlade.Contracts;

namespace OpenBlade.Protocol;

/// <summary>A bounded snapshot; publishing replaces the previous frame and never queues work.</summary>
public sealed record CoolingPadMirrorFrame(long Sequence, IReadOnlyList<CoolingPadColor> Colors);

public static class KeyboardLightingFrameStream
{
    private static CoolingPadMirrorFrame? latest;
    private static long sequence;
    private static readonly object Sync = new();
    private static readonly object DefaultOwner = new();
    private static object? currentOwner;
    public static CoolingPadMirrorFrame? Latest => Volatile.Read(ref latest);

    internal static void Publish(IReadOnlyList<RgbColor> colors, int columns, int rows) => Publish(colors, columns, rows, DefaultOwner);

    internal static void Publish(IReadOnlyList<RgbColor> colors, int columns, int rows, object owner)
    {
        if (columns < 1 || rows < 1 || colors.Count != columns * rows) { return; }
        var strip = new CoolingPadColor[CoolingPadProtocol.LedCount];
        for (int i = 0; i < strip.Length; i++)
        {
            int start = i * columns / strip.Length;
            int end = Math.Max(start + 1, (i + 1) * columns / strip.Length);
            int r = 0, g = 0, b = 0, count = 0;
            for (int y = 0; y < rows; y++)
            {
                for (int x = start; x < end && x < columns; x++)
                { var c = colors[y * columns + x]; r += c.Red; g += c.Green; b += c.Blue; count++; }
            }
            strip[i] = new((byte)(r / count), (byte)(g / count), (byte)(b / count));
        }
        lock (Sync)
        {
            currentOwner = owner;
            Volatile.Write(ref latest, new(++sequence, Array.AsReadOnly(strip)));
        }
    }

    internal static void PublishUniform(RgbColor color) => Publish([color], 1, 1);
    internal static void PublishUniform(RgbColor color, object owner) => Publish([color], 1, 1, owner);

    internal static void Clear(object owner)
    {
        lock (Sync)
        {
            if (!ReferenceEquals(currentOwner, owner)) { return; }
            currentOwner = null;
            Volatile.Write(ref latest, null);
        }
    }
}
