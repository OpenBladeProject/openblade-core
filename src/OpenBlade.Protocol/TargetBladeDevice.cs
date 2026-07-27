using OpenBlade.Contracts;

namespace OpenBlade.Protocol;

public static class TargetBladeDevice
{
    public const string Model = RazerBladeModelCatalog.OpenBladeTargetModelNumber;
    public const string BiosVersion = "3.01";
    public const ushort VendorId = 0x1532;
    public const ushort ProductId = 0x02E0;
    public const ushort UsagePage = 0x0001;
    public const ushort Usage = 0x0002;
    internal const ushort MatrixUsagePage = 0x000C;
    internal const ushort MatrixUsage = 0x0001;
    internal const ushort MatrixFeatureReportByteLength = 375;

    public static bool Matches(ushort vendorId, ushort productId) =>
        vendorId == VendorId && productId == ProductId;

    public static bool CanCarryProtocolReport(ushort featureReportByteLength) =>
        featureReportByteLength == RazerReport.ReportLength + 1;

    internal static bool IsExactMatrixInterface(HidHandleIdentity identity) =>
        identity.VendorId == VendorId
        && identity.ProductId == ProductId
        && identity.UsagePage == MatrixUsagePage
        && identity.Usage == MatrixUsage
        && identity.FeatureReportByteLength == MatrixFeatureReportByteLength;

    internal static bool IsExactInteractiveValidationTarget(
        string? model,
        HidInterfaceInfo? interfaceInfo) =>
        string.Equals(model, Model, StringComparison.Ordinal)
        && interfaceInfo is not null
        && Matches(interfaceInfo.VendorId, interfaceInfo.ProductId)
        && interfaceInfo.UsagePage == UsagePage
        && interfaceInfo.Usage == Usage
        && CanCarryProtocolReport(interfaceInfo.FeatureReportByteLength);
}
