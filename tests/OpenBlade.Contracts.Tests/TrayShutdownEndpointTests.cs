using OpenBlade.Contracts;

namespace OpenBlade.Contracts.Tests;

public sealed class TrayShutdownEndpointTests
{
    [Fact]
    public void ForExecutablePath_IsStableAcrossPathCasing()
    {
        string first = TrayShutdownEndpoint.ForExecutablePath(@"C:\Program Files\OpenBlade\OpenBlade.Tray.exe");
        string second = TrayShutdownEndpoint.ForExecutablePath(@"c:\program files\openblade\openblade.tray.exe");

        Assert.Equal(first, second);
        Assert.StartsWith(@"Local\OpenBlade.Tray.Shutdown.", first, StringComparison.Ordinal);
    }

    [Fact]
    public void ForExecutablePath_DiffersForPortableCopy()
    {
        Assert.NotEqual(
            TrayShutdownEndpoint.ForExecutablePath(@"C:\Program Files\OpenBlade\OpenBlade.Tray.exe"),
            TrayShutdownEndpoint.ForExecutablePath(@"D:\Portable\OpenBlade.Tray.exe"));
    }
}
