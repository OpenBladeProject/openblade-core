namespace OpenSynapse.Protocol;

public sealed record HidInterfaceInfo(
    string DevicePath,
    ushort VendorId,
    ushort ProductId,
    ushort UsagePage,
    ushort Usage,
    ushort FeatureReportByteLength);

public static class WindowsHidEnumerator
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

    internal static IReadOnlyList<HidInterfaceInfo> FindTargetInterfaces(
        IHidCandidateNative native)
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
                        identity.FeatureReportByteLength));
                }
                catch (Exception)
                {
                    // Diagnostic discovery skips interfaces whose metadata cannot be read.
                }
            }
        }

        return SelectProductionInterfaces(interfaces);
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
