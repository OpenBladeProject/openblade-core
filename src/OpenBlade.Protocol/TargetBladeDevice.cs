namespace OpenBlade.Protocol;

public static class TargetBladeDevice
{
    public const string Model = "RZ09-0581";
    public const string BiosVersion = "3.01";
    public const ushort VendorId = 0x1532;
    public const ushort ProductId = 0x02E0;
    public const ushort UsagePage = 0x0001;
    public const ushort Usage = 0x0002;

    public static bool Matches(ushort vendorId, ushort productId) =>
        vendorId == VendorId && productId == ProductId;

    public static bool CanCarryProtocolReport(ushort featureReportByteLength) =>
        featureReportByteLength == RazerReport.ReportLength + 1;

    public static bool IsExactInteractiveValidationTarget(
        string? model,
        HidInterfaceInfo? interfaceInfo) =>
        string.Equals(model, Model, StringComparison.Ordinal)
        && interfaceInfo is not null
        && Matches(interfaceInfo.VendorId, interfaceInfo.ProductId)
        && interfaceInfo.UsagePage == UsagePage
        && interfaceInfo.Usage == Usage
        && CanCarryProtocolReport(interfaceInfo.FeatureReportByteLength);
}
