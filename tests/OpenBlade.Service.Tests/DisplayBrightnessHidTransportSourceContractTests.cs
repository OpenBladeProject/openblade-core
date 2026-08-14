namespace OpenBlade.Service.Tests;

public sealed class DisplayBrightnessHidTransportSourceContractTests
{
    [Fact]
    public void CachedPathWrite_RevalidatesExactRootWhileHandleRemainsOpen()
    {
        string source = File.ReadAllText(Path.Combine(
            FindRepositoryRoot(),
            "src",
            "OpenBlade.Service",
            "WindowsDisplayBrightnessHidTransport.cs"));
        int writerStart = source.IndexOf(
            "private static DisplayBrightnessHidWriteResult WriteUsageToPath(",
            StringComparison.Ordinal);
        Assert.True(writerStart >= 0, "The cached-path writer is missing.");

        int openHandle = source.IndexOf(
            "using SafeFileHandle handle = CreateFile(",
            writerStart,
            StringComparison.Ordinal);
        Assert.True(openHandle > writerStart, "The HID path must be opened before validation.");

        int exactRootCheck = source.IndexOf(
            "HasExactRootAncestorForPath(path, out string? identityFailure)",
            openHandle,
            StringComparison.Ordinal);
        Assert.True(
            exactRootCheck > openHandle,
            "Every cached-path write must freshly validate the exact root ancestor.");

        int featureWrite = source.IndexOf(
            "DeviceIoControlSetFeature(",
            exactRootCheck,
            StringComparison.Ordinal);

        Assert.True(
            featureWrite > exactRootCheck,
            "The feature write must happen only after exact-root validation.");
        Assert.Contains("SetupDiOpenDeviceInterface(", source, StringComparison.Ordinal);
        Assert.Contains("GetDevicePath(deviceSet, ref interfaceData, ref deviceInfo)",
            source,
            StringComparison.Ordinal);
    }

    private static string FindRepositoryRoot()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "OpenBlade.slnx")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("Could not locate the OpenBlade repository root.");
    }
}
