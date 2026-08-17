using System.Globalization;

namespace OpenBlade.Tray.Tests;

internal sealed class CultureScope : IDisposable
{
    private readonly CultureInfo originalCulture = CultureInfo.CurrentCulture;
    private readonly CultureInfo originalUiCulture = CultureInfo.CurrentUICulture;

    public CultureScope(string cultureName)
    {
        CultureInfo selected = CultureInfo.GetCultureInfo(cultureName);
        CultureInfo.CurrentCulture = selected;
        CultureInfo.CurrentUICulture = selected;
    }

    public void Dispose()
    {
        CultureInfo.CurrentCulture = originalCulture;
        CultureInfo.CurrentUICulture = originalUiCulture;
    }
}
