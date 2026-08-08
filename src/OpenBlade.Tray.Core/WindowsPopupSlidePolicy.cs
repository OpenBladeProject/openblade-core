namespace OpenBlade.Tray;

public static class WindowsPopupSlidePolicy
{
    public const double DurationMilliseconds = 200;

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
        double easedProgress = progress * progress * (3 - (2 * progress));
        return checked((int)Math.Round(
            start + ((double)end - start) * easedProgress));
    }

    public static bool IsComplete(TimeSpan elapsed) =>
        elapsed >= TimeSpan.FromMilliseconds(DurationMilliseconds);
}
