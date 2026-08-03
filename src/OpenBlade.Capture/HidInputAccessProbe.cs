using OpenBlade.Protocol;

namespace OpenBlade.Capture;

internal sealed record HidInputAccessObservation(
    string Collection,
    ushort UsagePage,
    ushort Usage,
    ushort FeatureReportByteLength,
    bool ReadHandleOpened,
    bool IdentityReverified,
    int? Win32Error,
    string? Failure);

/// <summary>
/// Read-only diagnostic for the exact RZ09-0528 HID top-level collections.
/// It tests whether Windows permits a shared GENERIC_READ handle, but does not
/// read input reports, register Raw Input, or change device state.
/// </summary>
internal sealed class HidInputAccessProbe
{
    private const ushort VendorId = 0x1532;
    private const ushort ProductId = 0x02C6;
    private static readonly HidOpenParameters ReadOpenParameters = new(
        DesiredAccess: 0x80000000u,
        ShareMode: 0x00000003u,
        CreationDisposition: 3u,
        FlagsAndAttributes: 0x40000000u,
        Inheritable: false);

    private readonly IHidCandidateNative native;

    internal HidInputAccessProbe()
        : this(new WindowsHidNative())
    {
    }

    internal HidInputAccessProbe(IHidCandidateNative native)
    {
        ArgumentNullException.ThrowIfNull(native);
        this.native = native;
    }

    internal IReadOnlyList<HidInputAccessObservation> Run()
    {
        IReadOnlyList<HidInterfaceInfo> collections =
            WindowsHidEnumerator.FindCollections(native, VendorId, ProductId);
        List<HidInputAccessObservation> observations = [];
        foreach (HidInterfaceInfo collection in collections)
        {
            HidCandidateOpenResult opened;
            try
            {
                opened = native.TryOpen(collection.DevicePath, ReadOpenParameters);
            }
            catch
            {
                observations.Add(Create(
                    collection,
                    readHandleOpened: false,
                    identityReverified: false,
                    win32Error: null,
                    failure: "OpenFailed"));
                continue;
            }

            if (opened.Candidate is not { } candidate)
            {
                observations.Add(Create(
                    collection,
                    readHandleOpened: false,
                    identityReverified: false,
                    opened.Win32Error,
                    failure: null));
                continue;
            }

            using (candidate)
            {
                try
                {
                    HidHandleIdentity identity = native.ReadIdentity(candidate);
                    bool identityReverified =
                        identity.VendorId == VendorId
                        && identity.ProductId == ProductId
                        && identity.UsagePage == collection.UsagePage
                        && identity.Usage == collection.Usage
                        && identity.FeatureReportByteLength
                            == collection.FeatureReportByteLength;
                    observations.Add(Create(
                        collection,
                        readHandleOpened: true,
                        identityReverified,
                        win32Error: null,
                        failure: identityReverified ? null : "IdentityChanged"));
                }
                catch
                {
                    observations.Add(Create(
                        collection,
                        readHandleOpened: true,
                        identityReverified: false,
                        win32Error: null,
                        failure: "IdentityReadFailed"));
                }
            }
        }

        return observations;
    }

    private static HidInputAccessObservation Create(
        HidInterfaceInfo collection,
        bool readHandleOpened,
        bool identityReverified,
        int? win32Error,
        string? failure) =>
        new(
            SpecialKeyInputProbe.ExtractCollection(collection.DevicePath),
            collection.UsagePage,
            collection.Usage,
            collection.FeatureReportByteLength,
            readHandleOpened,
            identityReverified,
            win32Error,
            failure);
}
