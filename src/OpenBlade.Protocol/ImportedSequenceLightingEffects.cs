using OpenBlade.Contracts;

namespace OpenBlade.Protocol;

internal sealed class Pid02E0ImportedSequenceEffect : IPid02E0LightingEffect
{
    private readonly ImportedSequenceTimeline timeline;

    internal Pid02E0ImportedSequenceEffect(SequenceLightingParameters parameters)
    {
        ArgumentNullException.ThrowIfNull(parameters);
        if (!string.Equals(
            parameters.LayoutId,
            KeyboardLightingLayoutIds.Rz09_0581,
            StringComparison.Ordinal))
        {
            throw new ArgumentException(
                "The imported animation does not target the RZ09-0581 layout.",
                nameof(parameters));
        }

        timeline = new ImportedSequenceTimeline(parameters);
    }

    public TimeSpan? GetNextFrameDelay(TimeSpan elapsed) =>
        timeline.GetNextFrameDelay(elapsed);

    public Pid02E0MatrixFrame RenderFrame(TimeSpan elapsed)
    {
        IReadOnlyList<RgbColor> source = timeline.FrameAt(elapsed).Slots;
        var slots = new MatrixRgb[Pid02E0MatrixFrame.SlotCount];
        for (int slot = 0; slot < slots.Length; slot++)
        {
            RgbColor color = source[slot];
            slots[slot] = new MatrixRgb(color.Red, color.Green, color.Blue);
        }
        return new Pid02E0MatrixFrame(slots);
    }
}

internal sealed class Rz090528ImportedSequenceEffect : IRz090528LightingEffect
{
    private readonly ImportedSequenceTimeline timeline;

    internal Rz090528ImportedSequenceEffect(SequenceLightingParameters parameters)
    {
        ArgumentNullException.ThrowIfNull(parameters);
        if (!string.Equals(
            parameters.LayoutId,
            KeyboardLightingLayoutIds.Rz09_0528,
            StringComparison.Ordinal))
        {
            throw new ArgumentException(
                "The imported animation does not target the RZ09-0528 layout.",
                nameof(parameters));
        }

        timeline = new ImportedSequenceTimeline(parameters);
    }

    public bool RequiresContinuousRendering => timeline.RequiresContinuousRendering;

    public TimeSpan? GetNextFrameDelay(TimeSpan elapsed) =>
        timeline.GetNextFrameDelay(elapsed);

    public Rz090528MatrixFrame RenderFrame(TimeSpan elapsed) =>
        new(timeline.FrameAt(elapsed).Slots.ToArray());
}

internal sealed class ImportedSequenceTimeline
{
    private readonly KeyboardLightingSequence sequence;
    private readonly bool loop;
    private readonly long totalMilliseconds;

    internal ImportedSequenceTimeline(SequenceLightingParameters parameters)
    {
        sequence = KeyboardLightingSequenceCodec.Decode(parameters.TimelineBase64);
        loop = parameters.Loop;
        totalMilliseconds = sequence.Frames.Sum(
            frame => (long)frame.DurationMilliseconds);
    }

    internal bool RequiresContinuousRendering => sequence.Frames.Count > 1;

    internal KeyboardLightingSequenceFrame FrameAt(TimeSpan elapsed)
    {
        long position = PositionMilliseconds(elapsed);
        long cursor = 0;
        foreach (KeyboardLightingSequenceFrame frame in sequence.Frames)
        {
            cursor += frame.DurationMilliseconds;
            if (position < cursor)
            {
                return frame;
            }
        }

        return sequence.Frames[^1];
    }

    internal TimeSpan? GetNextFrameDelay(TimeSpan elapsed)
    {
        if (elapsed < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(elapsed));
        }
        if (sequence.Frames.Count == 1 || !loop && elapsed.TotalMilliseconds >= totalMilliseconds)
        {
            return null;
        }

        long position = PositionMilliseconds(elapsed);
        long cursor = 0;
        foreach (KeyboardLightingSequenceFrame frame in sequence.Frames)
        {
            cursor += frame.DurationMilliseconds;
            if (position < cursor)
            {
                return TimeSpan.FromMilliseconds(Math.Max(1, cursor - position));
            }
        }

        return TimeSpan.FromMilliseconds(1);
    }

    private long PositionMilliseconds(TimeSpan elapsed)
    {
        if (elapsed < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(elapsed));
        }

        long milliseconds = (long)Math.Floor(elapsed.TotalMilliseconds);
        return loop ? milliseconds % totalMilliseconds : Math.Min(milliseconds, totalMilliseconds);
    }
}
