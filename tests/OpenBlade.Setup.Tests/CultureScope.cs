using System.Globalization;

namespace OpenBlade.Setup.Tests;

internal sealed class CultureScope : IDisposable
{
    private readonly CultureInfo priorCulture = CultureInfo.CurrentCulture;
    private readonly CultureInfo priorUiCulture = CultureInfo.CurrentUICulture;

    public CultureScope(string cultureName)
    {
        CultureInfo culture = CultureInfo.GetCultureInfo(cultureName);
        CultureInfo.CurrentCulture = culture;
        CultureInfo.CurrentUICulture = culture;
    }

    public void Dispose()
    {
        CultureInfo.CurrentCulture = priorCulture;
        CultureInfo.CurrentUICulture = priorUiCulture;
    }
}
