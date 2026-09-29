using System.Runtime.InteropServices;

namespace OpenBlade.Protocol;

internal enum SavedCpuFirmwareReadStatus
{
    UnsupportedIdentity,
    Unavailable,
    InvalidData,
    MirrorMismatch,
    Observed,
}

internal sealed record SavedCpuFirmwareReadResult(
    SavedCpuFirmwareReadStatus Status,
    Rz090528SavedCpuConfiguration? Current,
    Rz090528SavedCpuConfiguration? Last,
    int CurrentError,
    int LastError)
{
    // Only complete, agreeing observations are eligible as a restoration
    // baseline. This says nothing about write admission or effective CPU state.
    public bool HasRestoreBaseline => Status == SavedCpuFirmwareReadStatus.Observed
        && Current is not null && Last is not null;
}

internal sealed record CpuFirmwareVariableRead(byte[]? Bytes, uint Attributes, int Error);

internal interface ICpuFirmwareVariableReader
{
    CpuFirmwareVariableRead Read(string name, string vendorGuid, int maximumBytes);
}

/// <summary>
/// Reads the two exact saved AOD variables only. The caller owns privilege
/// configuration; this reader never enables privileges or mutates firmware.
/// The observed 800-byte decoder is restricted to BIOS 2.02, independently of
/// the general production firmware-compatibility policy.
/// </summary>
internal sealed class Rz090528SavedCpuFirmwareReader
{
    internal const string VariableName = "AodSetupStx";
    internal const string MirrorName = "AodSetupStxLast";
    internal const string VendorGuid = "{5ED15DC0-EDEF-4161-9151-6014C4CC630C}";
    internal const int MaximumBytes = 32 * 1024;
    private readonly ICpuFirmwareVariableReader reader;

    internal Rz090528SavedCpuFirmwareReader()
        : this(new WindowsCpuFirmwareVariableReader()) { }

    internal Rz090528SavedCpuFirmwareReader(ICpuFirmwareVariableReader reader) =>
        this.reader = reader ?? throw new ArgumentNullException(nameof(reader));

    internal SavedCpuFirmwareReadResult Read(DeviceIdentity identity)
    {
        if (identity is null || identity.Model != "RZ09-0528"
            || identity.VendorId != 0x1532 || identity.ProductId != 0x02C6
            || identity.BiosVersion != "2.02")
        {
            return new(SavedCpuFirmwareReadStatus.UnsupportedIdentity, null, null, 0, 0);
        }

        CpuFirmwareVariableRead currentRead = reader.Read(VariableName, VendorGuid, MaximumBytes);
        Rz090528SavedCpuConfiguration? current = Decode(currentRead);
        CpuFirmwareVariableRead lastRead = reader.Read(MirrorName, VendorGuid, MaximumBytes);
        Rz090528SavedCpuConfiguration? last = Decode(lastRead);
        SavedCpuFirmwareReadStatus status;
        if (currentRead.Error != 0 || lastRead.Error != 0)
        {
            status = SavedCpuFirmwareReadStatus.Unavailable;
        }
        else if (current is null || last is null)
        {
            status = SavedCpuFirmwareReadStatus.InvalidData;
        }
        else
        {
            status = current.CopyBytes().AsSpan().SequenceEqual(last.CopyBytes())
                ? SavedCpuFirmwareReadStatus.Observed
                : SavedCpuFirmwareReadStatus.MirrorMismatch;
        }

        return new(status, current, last, currentRead.Error, lastRead.Error);
    }

    private static Rz090528SavedCpuConfiguration? Decode(CpuFirmwareVariableRead read) =>
        read.Error == 0 && read.Attributes == 7
            && read.Bytes is { Length: Rz090528CpuFirmwareCodec.SavedConfigurationLength }
                ? Rz090528CpuFirmwareCodec.DecodeSaved(read.Bytes)
                : null;
}

internal sealed class WindowsCpuFirmwareVariableReader : ICpuFirmwareVariableReader
{
    public CpuFirmwareVariableRead Read(string name, string vendorGuid, int maximumBytes)
    {
        // Keep the adapter constrained even if a future caller bypasses the
        // high-level reader. No variable enumeration or arbitrary name access.
        if (name is not (Rz090528SavedCpuFirmwareReader.VariableName or Rz090528SavedCpuFirmwareReader.MirrorName)
            || vendorGuid != Rz090528SavedCpuFirmwareReader.VendorGuid
            || maximumBytes != Rz090528SavedCpuFirmwareReader.MaximumBytes)
        {
            throw new ArgumentException("Only the exact observed saved CPU variables can be read.");
        }

        byte[] buffer = new byte[maximumBytes];
        uint length = GetFirmwareEnvironmentVariableExW(name, vendorGuid, buffer,
            checked((uint)buffer.Length), out uint attributes);
        if (length == 0)
        {
            // Includes ERROR_PRIVILEGE_NOT_HELD (1314); no false zero baseline.
            int error = Marshal.GetLastWin32Error();
            return new(null, attributes, error == 0 ? 13 : error);
        }

        return length > buffer.Length
            ? new(null, attributes, 122)
            : new(buffer.AsSpan(0, checked((int)length)).ToArray(), attributes, 0);
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, ExactSpelling = true)]
    private static extern uint GetFirmwareEnvironmentVariableExW(
        string name, string vendorGuid, [Out] byte[] buffer, uint size, out uint attributes);
}
