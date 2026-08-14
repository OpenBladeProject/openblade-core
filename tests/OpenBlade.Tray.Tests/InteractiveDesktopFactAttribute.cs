namespace OpenBlade.Tray.Tests;

[AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
internal sealed class InteractiveDesktopFactAttribute : FactAttribute
{
    internal const string SkipVariable =
        "OPENBLADE_SKIP_INTERACTIVE_DESKTOP_TESTS";

    public InteractiveDesktopFactAttribute()
    {
        if (InteractiveDesktopTestPolicy.ShouldSkip(
                Environment.GetEnvironmentVariable(SkipVariable)))
        {
            Skip = $"Requires an interactive Windows desktop; set {SkipVariable}=0 to run.";
        }
    }
}

internal static class InteractiveDesktopTestPolicy
{
    public static bool ShouldSkip(string? value) =>
        string.Equals(value, "1", StringComparison.Ordinal);
}
