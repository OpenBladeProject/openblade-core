using OpenBlade.Contracts;

namespace OpenBlade.Protocol;

public interface ICoolingPadDiscoverySource
{
    CoolingPadConnectionState Observe();
}

public static class CoolingPadDiscoveryFactory
{
    public static ICoolingPadDiscoverySource CreateWindows() =>
        WindowsHidEnumerator.CreateHyperBoostEligibilitySource();
}

internal sealed class UnavailableCoolingPadDiscoverySource
    : ICoolingPadDiscoverySource
{
    public CoolingPadConnectionState Observe() =>
        CoolingPadConnectionState.Unavailable;
}

internal interface IHyperBoostEligibilitySource
{
    CoolingPadConnectionState ObserveCoolingPad();
}

internal enum CoolingPadPhysicalPresenceObservation
{
    Absent,
    Present,
    ObservationFailed,
}

internal interface ICoolingPadPhysicalPresenceSource
{
    CoolingPadPhysicalPresenceObservation Observe();
}

internal sealed class UnavailableCoolingPadPhysicalPresenceSource
    : ICoolingPadPhysicalPresenceSource
{
    public CoolingPadPhysicalPresenceObservation Observe() =>
        CoolingPadPhysicalPresenceObservation.ObservationFailed;
}

internal sealed class WindowsCoolingPadPhysicalPresenceSource(IHidCandidateNative native)
    : ICoolingPadPhysicalPresenceSource
{
    private const string TargetPathToken = "vid_1532&pid_0f43";
    private readonly IHidCandidateNative native = native
        ?? throw new ArgumentNullException(nameof(native));

    public CoolingPadPhysicalPresenceObservation Observe()
    {
        try
        {
            return native.EnumerateDevicePaths().Any(path => path.Contains(
                TargetPathToken,
                StringComparison.OrdinalIgnoreCase))
                ? CoolingPadPhysicalPresenceObservation.Present
                : CoolingPadPhysicalPresenceObservation.Absent;
        }
        catch
        {
            return CoolingPadPhysicalPresenceObservation.ObservationFailed;
        }
    }
}

internal sealed class WindowsHyperBoostEligibilitySource(IHidCandidateNative native)
    : IHyperBoostEligibilitySource, ICoolingPadDiscoverySource
{
    private readonly ICoolingPadDiscoverySource discovery =
        new WindowsCoolingPadDiscoverySource(native);
    private readonly ICoolingPadPhysicalPresenceSource physicalPresence =
        new WindowsCoolingPadPhysicalPresenceSource(native);

    public CoolingPadConnectionState ObserveCoolingPad()
    {
        CoolingPadConnectionState exact = discovery.Observe();
        if (exact != CoolingPadConnectionState.Absent)
        {
            return exact;
        }

        return physicalPresence.Observe() switch
        {
            CoolingPadPhysicalPresenceObservation.Absent =>
                CoolingPadConnectionState.Absent,
            _ => CoolingPadConnectionState.ObservationFailed,
        };
    }

    public CoolingPadConnectionState Observe() => ObserveCoolingPad();
}

internal sealed class WindowsCoolingPadDiscoverySource(IHidCandidateNative native)
    : ICoolingPadDiscoverySource
{
    private static readonly HidOpenParameters DiagnosticOpenParameters = new(
        DesiredAccess: 0,
        ShareMode: 0x00000003u,
        CreationDisposition: 3u,
        FlagsAndAttributes: 0,
        Inheritable: false);
    private const string TargetPathToken = "vid_1532&pid_0f43";

    internal const ushort VendorId = 0x1532;
    internal const ushort ProductId = 0x0F43;
    internal const ushort UsagePage = 0x000C;
    internal const ushort Usage = 0x0001;
    internal const ushort VersionNumber = 0x0200;

    private readonly IHidCandidateNative native = native
        ?? throw new ArgumentNullException(nameof(native));

    public CoolingPadConnectionState Observe()
    {
        try
        {
            int matches = 0;
            bool knownAuxiliaryPresent = false;
            foreach (string path in native.EnumerateDevicePaths())
            {
                bool targetPath = path.Contains(
                    TargetPathToken,
                    StringComparison.OrdinalIgnoreCase);
                bool targetMetadataRequired = targetPath
                    && !path.Contains("&mi_01", StringComparison.OrdinalIgnoreCase);
                HidCandidateOpenResult opened;
                try
                {
                    opened = native.TryOpen(path, DiagnosticOpenParameters);
                }
                catch
                {
                    if (targetMetadataRequired)
                    {
                        return CoolingPadConnectionState.ObservationFailed;
                    }

                    continue;
                }

                if (opened.Candidate is not { } candidate)
                {
                    if (targetMetadataRequired)
                    {
                        return CoolingPadConnectionState.ObservationFailed;
                    }

                    continue;
                }

                using (candidate)
                {
                    HidHandleIdentity identity;
                    try
                    {
                        identity = native.ReadIdentity(candidate);
                    }
                    catch
                    {
                        if (targetMetadataRequired)
                        {
                            return CoolingPadConnectionState.ObservationFailed;
                        }

                        continue;
                    }

                    if (identity.MatchesExpected(new HidHandleIdentity(
                        VendorId,
                        ProductId,
                        UsagePage,
                        Usage,
                        RazerReport.ReportLength + 1,
                        VersionNumber)))
                    {
                        matches++;
                    }
                    else if (targetMetadataRequired && IsInputOnlySibling(identity))
                    {
                        knownAuxiliaryPresent = true;
                    }
                    else if (targetMetadataRequired
                        && (identity.VendorId != VendorId
                            || identity.ProductId != ProductId
                            || identity.VersionNumber != VersionNumber))
                    {
                        return CoolingPadConnectionState.ObservationFailed;
                    }
                }
            }

            return matches switch
            {
                0 when knownAuxiliaryPresent => CoolingPadConnectionState.ObservationFailed,
                0 => CoolingPadConnectionState.Absent,
                1 => CoolingPadConnectionState.PresentExact,
                _ => CoolingPadConnectionState.Ambiguous,
            };
        }
        catch
        {
            return CoolingPadConnectionState.ObservationFailed;
        }
    }

    // Source: the signed RC21-0230 firmware updater (docs/cooling-pad.md, image SHA-256
    // 8C8EACF5...). Interface 0's report descriptor at 0x793A declares exactly one feature
    // report, unnumbered and 90 bytes; report ID 5 belongs only to the input descriptors that
    // follow it. Any other 1532:0F43 MI_00 collection, including one re-exposed by an installed
    // Razer driver stack, therefore carries no feature report and cannot be the control channel.
    // Revision and usage are deliberately not pinned: a sibling's driver-assigned values are not
    // firmware facts. A sibling with any feature report remains unknown and fails closed.
    internal static bool IsInputOnlySibling(HidHandleIdentity identity) =>
        identity.VendorId == VendorId
        && identity.ProductId == ProductId
        && identity.FeatureReportByteLength == 0;
}
