namespace OpenBlade.Display;

internal static class RefreshRateClassifier
{
    internal static string? Classify(
        uint pathFlags,
        double virtualHertz,
        double physicalHertz,
        IReadOnlyList<DisplayRefreshOption> options)
    {
        int physical = Normalize(physicalHertz);
        int virtualRate = Normalize(virtualHertz);
        bool dynamic = (pathFlags & WindowsDisplayNative.DisplayConfigPathBoostRefreshRate) != 0;

        if (dynamic)
        {
            return options.FirstOrDefault(option =>
                option.IsDynamic &&
                option.DesktopHertz == virtualRate &&
                option.MaximumHertz == physical)?.Key;
        }

        return options.FirstOrDefault(option =>
            !option.IsDynamic && option.DesktopHertz == virtualRate)?.Key;
    }

    internal static int Normalize(double hertz)
    {
        return checked((int)Math.Round(hertz, MidpointRounding.AwayFromZero));
    }
}
