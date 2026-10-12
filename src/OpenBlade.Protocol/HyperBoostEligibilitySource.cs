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
                    else if (targetMetadataRequired && IsKnownNonControlAuxiliary(identity))
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

    private static bool IsKnownNonControlAuxiliary(HidHandleIdentity identity) =>
        identity.VendorId == VendorId
        && identity.ProductId == ProductId
        && identity.VersionNumber == 0x0101
        && identity.FeatureReportByteLength == 0
        && (identity.UsagePage == 0x000C && identity.Usage == 0x0001
            || identity.UsagePage == 0x0001 && identity.Usage == 0x0080);
}
