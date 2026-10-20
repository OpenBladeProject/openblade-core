namespace OpenBlade.Service.Tests;

public sealed class RazerDisplayBrightnessHidTransportSourceContractTests
{
    [Fact]
    public void RazerCachedPathWrite_RevalidatesExactDeviceBeforeSubmittingIoctl()
    {
        string source = File.ReadAllText(Path.Combine(
            FindRepositoryRoot(),
            "src",
            "OpenBlade.Service",
            "WindowsRazerDisplayBrightnessHidTransport.cs"));
        int writerStart = source.IndexOf(
            "private static DisplayBrightnessHidWriteResult WriteUsageToPath(",
            StringComparison.Ordinal);
        Assert.True(writerStart >= 0, "The Razer cached-path writer is missing.");

        int openHandle = source.IndexOf(
            "using SafeFileHandle handle = CreateFile(",
            writerStart,
            StringComparison.Ordinal);
        int identityCheck = source.IndexOf(
            "ValidateExactPath(path, out RazerDisplayBrightnessDriverStack? stack, out string? identityFailure)",
            openHandle,
            StringComparison.Ordinal);
        int virtualIdentityCheck = source.IndexOf(
            "ValidateVirtualConsumerDevice(stack)",
            identityCheck,
            StringComparison.Ordinal);
        int submission = source.IndexOf(
            "DeviceIoControl(",
            virtualIdentityCheck,
            StringComparison.Ordinal);

        Assert.True(openHandle > writerStart, "The path must be opened before validation.");
        Assert.True(
            identityCheck > openHandle,
            "Every Razer write must revalidate the exact device while its handle remains open.");
        Assert.True(
            virtualIdentityCheck > identityCheck,
            "Every Razer write must revalidate the virtual Consumer Control branch.");
        Assert.True(
            submission > virtualIdentityCheck,
            "The Razer IOCTL must be submitted only after both device branches are validated.");
        Assert.Contains("SetupDiEnumDeviceInterfaces(", source, StringComparison.Ordinal);
        Assert.Contains("HasExactAncestor", source, StringComparison.Ordinal);
        Assert.Contains("ControlServiceName", source, StringComparison.Ordinal);
        int controlAdmission = source.IndexOf("private static bool IsExactControlDevice(", StringComparison.Ordinal);
        int commonTrust = source.IndexOf("WindowsRazerDriverApiDiscovery.Capture(", controlAdmission, StringComparison.Ordinal);
        int consumerAdmission = source.IndexOf("private static DisplayBrightnessHidWriteResult ValidateVirtualConsumerDevice(", StringComparison.Ordinal);
        int consumerTrust = source.IndexOf("WindowsRazerDriverApiDiscovery.Capture(", consumerAdmission, StringComparison.Ordinal);
        Assert.True(commonTrust > controlAdmission);
        Assert.True(consumerTrust > consumerAdmission && consumerTrust < writerStart);
        Assert.DoesNotContain("GetExpectedHashStatus", source, StringComparison.Ordinal);
    }

    [Fact]
    public void RazerResolver_RequiresPresentVirtualConsumerBranch()
    {
        string source = File.ReadAllText(Path.Combine(
            FindRepositoryRoot(),
            "src",
            "OpenBlade.Service",
            "WindowsRazerDisplayBrightnessHidTransport.cs"));
        int resolverStart = source.IndexOf(
            "ResolveDevicePath(stack);",
            StringComparison.Ordinal);
        int controlIdentity = source.IndexOf(
            "IsExactControlDevice(deviceSet, ref deviceInfo, stack, out string? failure)",
            resolverStart,
            StringComparison.Ordinal);
        int virtualIdentity = source.IndexOf(
            "ValidateVirtualConsumerDevice(stack)",
            controlIdentity,
            StringComparison.Ordinal);
        int acceptedPath = source.IndexOf(
            "DisplayBrightnessHidWriteStatus.Applied));",
            virtualIdentity,
            StringComparison.Ordinal);

        Assert.True(controlIdentity > resolverStart);
        Assert.True(virtualIdentity > controlIdentity);
        Assert.True(acceptedPath > virtualIdentity);
        Assert.Contains("SetupDiEnumDeviceInfo(", source, StringComparison.Ordinal);
        Assert.Contains("VirtualConsumerServiceName", source, StringComparison.Ordinal);
        Assert.Contains("VirtualConsumerAncestorPrefix", source, StringComparison.Ordinal);
    }

    [Fact]
    public void RazerResolver_PreservesDeviceIdentityInspectionFailures()
    {
        string source = File.ReadAllText(Path.Combine(
            FindRepositoryRoot(),
            "src",
            "OpenBlade.Service",
            "WindowsRazerDisplayBrightnessHidTransport.cs"));
        int rejectedIdentity = source.IndexOf(
            "if (!IsExactControlDevice(deviceSet, ref deviceInfo, stack, out string? failure))",
            StringComparison.Ordinal);
        int preserveFailure = source.IndexOf(
            "inspectionFailure = failure;",
            rejectedIdentity,
            StringComparison.Ordinal);
        int retryIdentity = source.IndexOf(
            "if (IsExactControlInstance(deviceInfo.DeviceInstance, stack))",
            rejectedIdentity,
            StringComparison.Ordinal);

        Assert.True(rejectedIdentity >= 0);
        Assert.True(
            preserveFailure > rejectedIdentity && preserveFailure < retryIdentity,
            "An indeterminate device identity must not be reported as unavailable.");
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
