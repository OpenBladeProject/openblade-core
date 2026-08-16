using System;
using System.Globalization;
using WixToolset.BootstrapperApplicationApi;

namespace OpenBlade.Installer.UI;

internal static class Program
{
    private static int Main()
    {
        if (string.Equals(
                Environment.GetEnvironmentVariable("OPENBLADE_PSEUDO_LOCALIZATION"),
                "1",
                StringComparison.Ordinal))
        {
            CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("qps-ploc");
        }
        ManagedBootstrapperApplication.Run(new OpenBladeBootstrapperApplication());
        return 0;
    }
}
