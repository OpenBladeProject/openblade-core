using System.Security.Cryptography;
using OpenBlade.Contracts;

namespace OpenBlade.Protocol;

/// <summary>Projects the saved logical canvas onto the exact 02E0 MCU map.
/// Source arrays own key addresses. Sampling a regional-only spatial cell is
/// an OpenBlade rendering choice and never establishes an input binding.</summary>
internal static class Pid02E0RegionalFrameProjection
{
    private static readonly Projection[] Layouts = Enumerable.Range(0, 256)
        .Select(id => CreateProjection((byte)id)).ToArray();

    internal static Pid02E0MatrixFrame Project(Pid02E0MatrixFrame canvas, byte layoutId)
    {
        ArgumentNullException.ThrowIfNull(canvas);
        Projection map = Layouts[layoutId];
        var colors = new MatrixRgb[Pid02E0MatrixFrame.SlotCount];
        for (int slot = 0; slot < colors.Length; slot++)
        {
            int source = map.CanvasSlots[slot];
            if (source < 0 && !canvas.LogicalKeyColors)
            {
                source = map.SpatialSamples[slot];
            }
            if (source >= 0)
            {
                colors[slot] = canvas[source];
            }
        }
        return new Pid02E0MatrixFrame(colors, canvas.LogicalKeyColors);
    }

    // A distinct color per logical slot binds source routing, actual regional
    // holes and complete report encoding. A uniform answer alone misses aliases.
    internal static string SemanticFingerprint()
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var colors = new MatrixRgb[Pid02E0MatrixFrame.SlotCount];
        for (int slot = 0; slot < colors.Length; slot++)
        {
            if (!AmbientLightingFrame.IsHole(slot))
            {
                colors[slot] = new MatrixRgb((byte)(slot + 1), (byte)(255 - slot), (byte)(slot ^ 0x5A));
            }
        }
        var canvas = new Pid02E0MatrixFrame(colors);
        for (int layout = 0; layout < 256; layout++)
        {
            byte[] report = Pid02E0MatrixReportCodec.EncodeFeatureReportBuffer(
                new Pid02E0MatrixReport(0x5A, Project(canvas, (byte)layout)));
            hash.AppendData(report);
        }
        return Convert.ToHexString(hash.GetHashAndReset());
    }

    private static Projection CreateProjection(byte layoutId)
    {
        int[] sources = Enumerable.Repeat(-1, Pid02E0MatrixFrame.SlotCount).ToArray();
        for (byte canvas = 0; canvas < sources.Length; canvas++)
        {
            // The serialized canvas does not contain supplementary regional keys.
            if (!AmbientLightingFrame.IsHole(canvas)
                && Blade16KeyboardLightingLayout.TryMapCanvasSlot(canvas, layoutId, out byte physical))
            {
                sources[physical] = canvas;
            }
        }
        int[] samples = Enumerable.Repeat(-1, sources.Length).ToArray();
        for (int target = 0; target < samples.Length; target++)
        {
            if (!Blade16KeyboardLightingLayout.IsKeySlot(target, layoutId) || sources[target] >= 0)
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
        return new(sources, samples);
    }

    private sealed record Projection(int[] CanvasSlots, int[] SpatialSamples);
}
