using OpenBlade.Contracts;

namespace OpenBlade.Protocol;

internal sealed record HidInterfaceInfo(
    string DevicePath,
    ushort VendorId,
    ushort ProductId,
    ushort UsagePage,
    ushort Usage,
    ushort FeatureReportByteLength,
    ushort? VersionNumber = null);

internal static class WindowsHidEnumerator
{
    private static readonly HidOpenParameters DiagnosticOpenParameters = new(
        DesiredAccess: 0,
        ShareMode: 0x00000003u,
        CreationDisposition: 3u,
        FlagsAndAttributes: 0,
        Inheritable: false);

    public static IReadOnlyList<HidInterfaceInfo> FindTargetInterfaces()
    {
        if (!OperatingSystem.IsWindows())
        {
            return [];
        }

        return FindTargetInterfaces(new WindowsHidNative());
    }

    public static IReadOnlyList<HidInterfaceInfo> FindTargetCollections()
    {
        if (!OperatingSystem.IsWindows())
        {
            return [];
        }

        return FindTargetCollections(new WindowsHidNative());
    }

    internal static IReadOnlyList<HidInterfaceInfo> FindCollections(
        ushort vendorId,
        ushort productId)
    {
        if (!OperatingSystem.IsWindows())
        {
            return [];
        }

        return FindCollections(new WindowsHidNative(), vendorId, productId);
    }

    internal static CoolingPadConnectionState ObserveCoolingPad()
    {
        if (!OperatingSystem.IsWindows())
        {
            return CoolingPadConnectionState.Unavailable;
        }

        return new WindowsCoolingPadDiscoverySource(new WindowsHidNative()).Observe();
    }

    internal static ICoolingPadPhysicalPresenceSource
        CreateCoolingPadPhysicalPresenceSource()
    {
        if (!OperatingSystem.IsWindows())
        {
            return new UnavailableCoolingPadPhysicalPresenceSource();
        }

        return new WindowsCoolingPadPhysicalPresenceSource(new WindowsHidNative());
    }

    internal static IReadOnlyList<HidInterfaceInfo> FindTargetInterfaces(
        IHidCandidateNative native) =>
        SelectProductionInterfaces(FindTargetCollections(native));

    internal static IReadOnlyList<HidInterfaceInfo> FindTargetCollections(
        IHidCandidateNative native) =>
        FindCollections(native, TargetBladeDevice.VendorId, TargetBladeDevice.ProductId);

    internal static IReadOnlyList<HidInterfaceInfo> FindCollections(
        IHidCandidateNative native,
        ushort vendorId,
        ushort productId)
    {
        ArgumentNullException.ThrowIfNull(native);
        List<HidInterfaceInfo> interfaces = [];
        foreach (string path in native.EnumerateDevicePaths())
        {
            OpenedCandidateHandle? candidate;
            try
            {
                candidate = native.TryOpen(path, DiagnosticOpenParameters).Candidate;
            }
            catch (Exception)
            {
                continue;
            }

            if (candidate is null)
            {
                continue;
            }

            using (candidate)
            {
                try
                {
                    HidHandleIdentity identity = native.ReadIdentity(candidate);
                    interfaces.Add(new HidInterfaceInfo(
                        path,
                        identity.VendorId,
                        identity.ProductId,
                        identity.UsagePage,
                        identity.Usage,
                        identity.FeatureReportByteLength,
                        identity.VersionNumber));
                }
                catch (Exception)
                {
                    // Diagnostic discovery skips interfaces whose metadata cannot be read.
                }
            }
        }

        return interfaces
            .Where(device =>
                device.VendorId == vendorId
                && device.ProductId == productId)
            .ToArray();
    }

    public static IReadOnlyList<HidInterfaceInfo> SelectProductionInterfaces(
        IEnumerable<HidInterfaceInfo> interfaces)
    {
        ArgumentNullException.ThrowIfNull(interfaces);
        return interfaces
            .Where(device =>
                TargetBladeDevice.Matches(device.VendorId, device.ProductId)
                && TargetBladeDevice.CanCarryProtocolReport(device.FeatureReportByteLength)
                && device.UsagePage == TargetBladeDevice.UsagePage
                && device.Usage == TargetBladeDevice.Usage)
            .ToArray();
    }
}
