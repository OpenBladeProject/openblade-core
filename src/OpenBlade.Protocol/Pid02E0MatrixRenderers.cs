namespace OpenBlade.Protocol;

/// <summary>
/// Deterministic spatial renderers for the matrix shapes proven by capture.
/// Animation scheduling remains explicit: the captures prove the frame colors
/// but do not isolate Synapse's wall-clock-to-color/intensity curves.
/// </summary>
internal static class Pid02E0MatrixRenderers
{
    internal static Pid02E0MatrixFrame RenderStatic(MatrixRgb color) =>
        Pid02E0MatrixLayout.RenderUniform(color);

    /// <summary>
    /// Renders one caller-supplied sample of the captured uniform Spectrum
    /// stream. Keeping the sample explicit avoids inventing an unverified
    /// phase curve while making frame generation fully deterministic.
    /// </summary>
    internal static Pid02E0MatrixFrame RenderSpectrumSample(MatrixRgb hostColorSample) =>
        Pid02E0MatrixLayout.RenderUniform(hostColorSample);

    /// <summary>
    /// Renders one deterministic Breathing sample. Intensity zero is black and
    /// intensity 255 is the unscaled configured color.
    /// </summary>
    internal static Pid02E0MatrixFrame RenderBreathingSample(
        MatrixRgb configuredColor,
        byte intensity) =>
        Pid02E0MatrixLayout.RenderUniform(configuredColor.Scale(intensity));
}

/// <summary>
/// Per-slot Reactive frame state. Physical-key captures prove the admitted
/// main/function-row slots and the linear Short, Medium, and Long decay curves.
/// </summary>
internal sealed class Pid02E0ReactiveFrame
{
    private readonly Pid02E0MatrixFrame matrix;

    internal Pid02E0ReactiveFrame(ReadOnlySpan<MatrixRgb> slots)
    {
        if (slots.Length != Pid02E0MatrixFrame.SlotCount)
        {
            throw new ArgumentOutOfRangeException(
                nameof(slots),
                slots.Length,
                $"A Reactive frame must contain exactly {Pid02E0MatrixFrame.SlotCount} RGB slots.");
        }

        foreach (int hole in Pid02E0MatrixLayout.CapturedHoleSlots)
        {
            if (slots[hole] != MatrixRgb.Black)
            {
                throw new ArgumentException(
                    $"Captured layout-hole slot {hole} must remain black.",
                    nameof(slots));
            }
        }

        matrix = new Pid02E0MatrixFrame(slots);
    }

    internal MatrixRgb this[int slot] => matrix[slot];

    internal Pid02E0MatrixFrame ToMatrixFrame() =>
        new(matrix.ToArray());

    internal static Pid02E0ReactiveFrame BlackIdle() =>
        new(new MatrixRgb[Pid02E0MatrixFrame.SlotCount]);
}
