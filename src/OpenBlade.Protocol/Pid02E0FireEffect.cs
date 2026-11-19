using OpenBlade.Contracts;

namespace OpenBlade.Protocol;

/// <summary>
/// Software Fire on the exact 02E0 logical canvas. The existing write boundary
/// projects this frame through its own SO6120 regional map. No other model's
/// capture oracle, mask or transport participates in this effect.
/// </summary>
internal sealed class Pid02E0FireEffect : IPid02E0LightingEffect
{
    public TimeSpan? GetNextFrameDelay(TimeSpan elapsed)
    {
        Pid02E0LightingMath.ThrowIfNegative(elapsed);
        return Pid02E0LightingTiming.AnimatedFrameInterval;
    }

    public Pid02E0MatrixFrame RenderFrame(TimeSpan elapsed)
    {
        Pid02E0LightingMath.ThrowIfNegative(elapsed);
        var colors = new MatrixRgb[Pid02E0MatrixFrame.SlotCount];
        for (int slot = 0; slot < colors.Length; slot++)
        {
            if (!Pid02E0MatrixLayout.IsHole(slot))
            {
                colors[slot] = SoftwareFirePattern.Render(
                    slot / Blade16KeyboardLightingLayout.Columns,
                    slot % Blade16KeyboardLightingLayout.Columns,
                    elapsed);
            }
        }
        return new Pid02E0MatrixFrame(colors);
    }
}
