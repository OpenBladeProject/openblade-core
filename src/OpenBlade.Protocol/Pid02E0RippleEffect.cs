namespace OpenBlade.Protocol;

/// <summary>
/// Host-rendered Ripple effect calibrated from the RZ09-0581 / PID 02E0
/// Synapse oracle capture. Protocol slots are projected onto a 6 x 17 grid as
/// a rendering approximation; the matrix protocol does not expose physical
/// key coordinates.
/// </summary>
internal sealed class Pid02E0RippleEffect : IPid02E0KeyTriggeredLightingEffect
{
    internal static readonly TimeSpan Duration = TimeSpan.FromMilliseconds(800);

    private const int Columns = 17;
    private static readonly (double Inner, double Outer)[] CapturedBands =
    [
        (0.00, 0.00), (0.00, 1.00), (1.00, 2.24), (1.41, 3.61),
        (2.83, 4.24), (3.61, 5.39), (4.47, 6.32), (5.83, 7.28),
        (6.71, 8.25), (7.28, 9.22), (8.25, 10.44), (9.22, 11.40),
        (10.20, 13.15), (11.18, 13.34), (12.17, 14.32), (14.00, 14.32),
    ];

    private readonly MatrixRgb color;
    private readonly TimeSpan?[] lastPulseAt = new TimeSpan?[Pid02E0MatrixFrame.SlotCount];

    internal Pid02E0RippleEffect(MatrixRgb color) => this.color = color;

    public TimeSpan? GetNextFrameDelay(TimeSpan elapsed)
    {
        Pid02E0LightingMath.ThrowIfNegative(elapsed);
        return HasActivePulse(elapsed)
            ? Pid02E0LightingTiming.AnimatedFrameInterval
            : Pid02E0LightingTiming.IdleKeepaliveInterval;
    }

    public void Pulse(int slot, TimeSpan at)
    {
        Pid02E0LightingMath.ThrowIfNegative(at);
        if ((uint)slot >= Pid02E0MatrixFrame.SlotCount)
        {
            throw new ArgumentOutOfRangeException(nameof(slot));
        }

        if (Pid02E0MatrixLayout.IsHole(slot))
        {
            throw new ArgumentException(
                $"Captured layout-hole slot {slot} cannot receive a Ripple pulse.",
                nameof(slot));
        }

        lastPulseAt[slot] = at;
    }

    public Pid02E0MatrixFrame RenderFrame(TimeSpan elapsed)
    {
        Pid02E0LightingMath.ThrowIfNegative(elapsed);
        MatrixRgb[] slots = new MatrixRgb[Pid02E0MatrixFrame.SlotCount];

        for (int origin = 0; origin < lastPulseAt.Length; origin++)
        {
            TimeSpan? pulseAt = lastPulseAt[origin];
            if (pulseAt is null || pulseAt.Value > elapsed)
            {
                continue;
            }

            TimeSpan age = elapsed - pulseAt.Value;
            int step = (int)(age.Ticks / Pid02E0LightingTiming.AnimatedFrameInterval.Ticks);
            if ((uint)step >= CapturedBands.Length)
            {
                continue;
            }

            (double inner, double outer) = CapturedBands[step];
            int originRow = origin / Columns;
            int originColumn = origin % Columns;
            for (int slot = 0; slot < slots.Length; slot++)
            {
                if (Pid02E0MatrixLayout.IsHole(slot))
                {
                    continue;
                }

                int rowDelta = (slot / Columns) - originRow;
                int columnDelta = (slot % Columns) - originColumn;
                double distance = Math.Sqrt((rowDelta * rowDelta) + (columnDelta * columnDelta));
                if (distance + 1e-9 >= inner && distance <= outer + 1e-9)
                {
                    slots[slot] = color;
                }
            }
        }

        return new Pid02E0MatrixFrame(slots);
    }

    private bool HasActivePulse(TimeSpan elapsed)
    {
        foreach (TimeSpan? pulseAt in lastPulseAt)
        {
            if (pulseAt is { } value && value <= elapsed && elapsed - value < Duration)
            {
                return true;
            }
        }

        return false;
    }
}
