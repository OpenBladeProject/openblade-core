using OpenBlade.Contracts;

namespace OpenBlade.Protocol;

/// <summary>Bounded per-key playback over the already admitted product-672 frame transport.</summary>
internal sealed class Blade18ImportedSequenceEffect
{
    private readonly ImportedSequenceTimeline timeline;
    private readonly bool loop;
    private readonly TimeSpan finalFrameStarts;

    internal Blade18ImportedSequenceEffect(SequenceLightingParameters parameters, byte? region)
    {
        if (parameters.LayoutId != KeyboardLightingLayoutIds.Rz09_0484)
        {
            throw new ArgumentException("The sequence does not target the Blade 18 layout.", nameof(parameters));
        }
        KeyboardLightingSequence decoded = KeyboardLightingSequenceCodec.Decode(parameters.TimelineBase64, parameters.LayoutId);
        foreach (KeyboardLightingSequenceFrame frame in decoded.Frames)
        {
            for (int slot = 0; slot < frame.Slots.Count; slot++)
            {
                if (frame.Slots[slot] != default
                    && !Blade18KeyboardLightingLayout.TryGetKeyPosition(region, slot, out _, out _))
                {
                    throw new CapabilityUnavailableException("The preset colors a key outside this keyboard's layout.");
                }
            }
        }
        timeline = new ImportedSequenceTimeline(parameters);
        loop = parameters.Loop;
        finalFrameStarts = TimeSpan.FromMilliseconds(decoded.Frames
            .Take(decoded.Frames.Count - 1).Sum(frame => (long)frame.DurationMilliseconds));
    }

    internal TimeSpan? GetNextFrameDelay(TimeSpan elapsed)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(elapsed, TimeSpan.Zero);
        return !loop && elapsed >= finalFrameStarts ? null : timeline.GetNextFrameDelay(elapsed);
    }

    internal Blade18LightingFrame RenderFrame(TimeSpan elapsed) =>
        new(timeline.FrameAt(elapsed).Slots.Take(Blade18LightingFrame.SlotCount).ToArray());
}
