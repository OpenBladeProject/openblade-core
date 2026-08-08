namespace OpenBlade.Tray;

public static class WindowsPopupSlidePolicy
{
    public const double DurationMilliseconds = 250;

    public static int ResolvePosition(
        int start,
        int end,
        TimeSpan elapsed,
        bool hiding)
    {
        double elapsedMilliseconds = elapsed.TotalMilliseconds;
        if (!double.IsFinite(elapsedMilliseconds))
        {
            throw new ArgumentOutOfRangeException(nameof(elapsed));
        }

        double progress = Math.Clamp(
            elapsedMilliseconds / DurationMilliseconds,
            0,
            1);
        double easedProgress = hiding
            ? progress == 0
                ? 0
                : Math.Pow(2, 10 * progress - 10)
            : progress == 1
                ? 1
                : 1 - Math.Pow(2, -10 * progress);
        return checked((int)Math.Round(
            start + ((double)end - start) * easedProgress));
    }

    public static bool IsComplete(TimeSpan elapsed) =>
        elapsed >= TimeSpan.FromMilliseconds(DurationMilliseconds);
}
