using OpenSynapse.Contracts;

namespace OpenSynapse.Contracts.Tests;

public sealed class TrayShutdownEndpointTests
{
    [Fact]
    public void ForExecutablePath_IsStableAcrossPathCasing()
    {
        string first = TrayShutdownEndpoint.ForExecutablePath(@"C:\Program Files\OpenSynapse\OpenSynapse.Tray.exe");
        string second = TrayShutdownEndpoint.ForExecutablePath(@"c:\program files\opensynapse\opensynapse.tray.exe");

        Assert.Equal(first, second);
        Assert.StartsWith(@"Local\OpenSynapse.Tray.Shutdown.", first, StringComparison.Ordinal);
    }

    [Fact]
    public void ForExecutablePath_DiffersForPortableCopy()
    {
        Assert.NotEqual(
            TrayShutdownEndpoint.ForExecutablePath(@"C:\Program Files\OpenSynapse\OpenSynapse.Tray.exe"),
            TrayShutdownEndpoint.ForExecutablePath(@"D:\Portable\OpenSynapse.Tray.exe"));
    }
}
