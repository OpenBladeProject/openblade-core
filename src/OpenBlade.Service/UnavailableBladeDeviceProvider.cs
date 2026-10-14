using OpenBlade.Contracts;
using OpenBlade.Protocol;

namespace OpenBlade.Service;

public sealed class UnavailableBladeDeviceProvider : IVerifiedBladeDeviceProvider
{
    public FirmwareCompatibilityStatus FirmwareCompatibility =>
        FirmwareCompatibilityStatus.Unknown;

    public bool RequiresUntestedFirmwareOverride => false;

    public bool IsExperimentalDevice => false;

    public string? ExperimentalSupportSource => null;

    public void SetAllowUntestedFirmware(bool enabled)
    {
    }

    public void SetAllowExperimentalFeatures(bool enabled)
    {
    }

    public DiagnosticExperimentalSupport GetExperimentalSupportDiagnostics() =>
        DiagnosticExperimentalSupport.NotDetected;

    public bool TryGetVerifiedDevice(out IBladeDevice device)
    {
        device = null!;
        return false;
    }
}
