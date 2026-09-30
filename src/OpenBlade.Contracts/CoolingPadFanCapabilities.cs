namespace OpenBlade.Contracts;

/// <summary>
/// Observations for the exact cooling pad, not host admission or permission to write.
/// The vendor configured range has not been physically validated across all values.
/// </summary>
public sealed class CoolingPadFanCapabilities
{
    private CoolingPadFanCapabilities() { }

    public static CoolingPadFanCapabilities Observed { get; } = new();

    public ushort VendorId => 0x1532;
    public ushort ProductId => 0x0F43;
    public ushort Revision => 0x0200;
    public DeviceFeatureAccess HostDemandAccess => DeviceFeatureAccess.ObservedOnly;
    public DeviceNumericRange VendorConfiguredDemandRpm { get; } = new(500, 3200, 50);
    public bool FullDemandRangePhysicallyValidated => false;
    public bool MeasuredRpmReadbackSupported => false;
    public bool ActiveFanStateReadbackSupported => false;
    public bool OwnershipReadbackSupported => false;
}

/// <summary>
/// A configured host demand in vendor RPM units, never a measured fan speed.
/// A valid value does not establish write admission or actual fan state.
/// </summary>
public readonly record struct CoolingPadConfiguredRpmDemand
{
    [System.Text.Json.Serialization.JsonConstructor]
    public CoolingPadConfiguredRpmDemand(int rpm)
    {
        if (!CoolingPadFanCapabilities.Observed.VendorConfiguredDemandRpm.Supports(rpm))
        {
            throw new ArgumentOutOfRangeException(nameof(rpm));
        }

        Rpm = rpm;
    }

    public int Rpm { get; }

    // Default structs bypass the constructor and must not be encoded.
    public bool IsWellFormed =>
        CoolingPadFanCapabilities.Observed.VendorConfiguredDemandRpm.Supports(Rpm);
}
