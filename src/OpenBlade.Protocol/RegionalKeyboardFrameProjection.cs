namespace OpenBlade.Protocol;

internal delegate bool CanvasSlotMapper(byte canvasSlot, byte layoutId, out byte physicalSlot);

/// <summary>Shared color projection for independently reviewed 6x17 maps.
/// Callers supply their own exact-model source masks and address mappings.</summary>
internal sealed class RegionalKeyboardFrameProjection
{
    private readonly Projection[] layouts;

    internal RegionalKeyboardFrameProjection(
        IReadOnlyList<int> canvasHoles,
        CanvasSlotMapper mapCanvasSlot,
        Func<int, byte, bool> isKeySlot)
    {
        layouts = Enumerable.Range(0, 256)
            .Select(id => CreateProjection((byte)id, canvasHoles, mapCanvasSlot, isKeySlot)).ToArray();
    }

    internal T[] Project<T>(ReadOnlySpan<T> canvas, byte layoutId, bool logicalKeyColors) where T : struct
    {
        if (canvas.Length != 102)
        {
            throw new ArgumentOutOfRangeException(nameof(canvas));
        }
        Projection map = layouts[layoutId];
        var colors = new T[102];
        for (int slot = 0; slot < colors.Length; slot++)
        {
            int source = logicalKeyColors ? map.KeySlots[slot] : map.CanvasSlots[slot];
            if (source < 0 && !logicalKeyColors)
            {
                source = map.SpatialSamples[slot];
            }
            if (source >= 0)
            {
                colors[slot] = canvas[source];
            }
        }
        return colors;
    }

    private static Projection CreateProjection(byte layoutId,
        IReadOnlyList<int> canvasHoles, CanvasSlotMapper mapCanvasSlot,
        Func<int, byte, bool> isKeySlot)
    {
        int[] sources = Enumerable.Repeat(-1, 102).ToArray();
        int[] keySources = Enumerable.Repeat(-1, 102).ToArray();
        for (byte canvas = 0; canvas < sources.Length; canvas++)
        {
            if (mapCanvasSlot(canvas, layoutId, out byte physical))
            {
                if (physical >= sources.Length)
                {
                    throw new InvalidOperationException("A regional source map exceeds its frame.");
                }
                keySources[physical] = canvas;
                if (!canvasHoles.Contains(canvas))
                {
                    sources[physical] = canvas;
                }
            }
        }
        int[] samples = Enumerable.Repeat(-1, sources.Length).ToArray();
        for (int target = 0; target < samples.Length; target++)
        {
            if (!isKeySlot(target, layoutId) || sources[target] >= 0)
            {
                continue;
            }
            int nearestDistance = int.MaxValue;
            for (int candidate = 0; candidate < sources.Length; candidate++)
            {
                if (sources[candidate] < 0)
                {
                    continue;
                }
                int row = target / 17 - candidate / 17;
                int column = target % 17 - candidate % 17;
                int distance = row * row + column * column;
                if (distance < nearestDistance)
                {
                    nearestDistance = distance;
                    samples[target] = sources[candidate];
                }
            }
        }
        return new(sources, keySources, samples);
    }

    private sealed record Projection(int[] CanvasSlots, int[] KeySlots, int[] SpatialSamples);
}
