namespace OpenBlade.Protocol;

internal sealed class FirmwareCompatibilityPolicy
{
    private int allowUntestedFirmware;

    public bool AllowUntestedFirmware
    {
        get => Volatile.Read(ref allowUntestedFirmware) != 0;
        set => Volatile.Write(ref allowUntestedFirmware, value ? 1 : 0);
    }
}
