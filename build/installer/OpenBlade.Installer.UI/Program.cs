using WixToolset.BootstrapperApplicationApi;

namespace OpenBlade.Installer.UI;

internal static class Program
{
    private static int Main()
    {
        ManagedBootstrapperApplication.Run(new OpenBladeBootstrapperApplication());
        return 0;
    }
}
