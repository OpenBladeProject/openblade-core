using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Text;

namespace OpenBlade.Service;

internal enum FanStopGpuPowerState { Unknown, PoweredOn, PoweredDown }

// Unlike workload counters, this is Windows' latest reported device D-state.
// The read-only CM_POWER_DATA property does not open NVML or the GPU device.
// https://learn.microsoft.com/windows-hardware/drivers/ddi/wdm/ns-wdm-cm_power_data_s
internal static class WindowsNvidiaGpuPowerState
{
    private static readonly Guid DisplayClass = new("4d36e968-e325-11ce-bfc1-08002be10318");

    internal static FanStopGpuPowerState Read()
    {
        Guid deviceClass = DisplayClass;
        nint set = SetupDiGetClassDevs(ref deviceClass, null, 0, 2 /* present */);
        if (set == -1) { return FanStopGpuPowerState.Unknown; }
        try
        {
            FanStopGpuPowerState result = FanStopGpuPowerState.Unknown;
            int matches = 0;
            for (uint index = 0; ; index++)
            {
                var info = new DeviceInfo { Size = Marshal.SizeOf<DeviceInfo>() };
                if (!SetupDiEnumDeviceInfo(set, index, ref info))
                { return Marshal.GetLastPInvokeError() == 259 && matches == 1 ? result : FanStopGpuPowerState.Unknown; }
                var instance = new StringBuilder(512);
                if (!SetupDiGetDeviceInstanceId(set, ref info, instance, instance.Capacity, out _))
                { return FanStopGpuPowerState.Unknown; }
                if (!instance.ToString().StartsWith(@"PCI\VEN_10DE&", StringComparison.OrdinalIgnoreCase)) { continue; }
                if (++matches != 1) { return FanStopGpuPowerState.Unknown; }
                byte[] power = new byte[56]; // CM_POWER_DATA has fourteen 32-bit fields.
                if (!SetupDiGetDeviceRegistryProperty(set, ref info, 0x1E, out uint type,
                        power, (uint)power.Length, out uint size) || size != power.Length)
                { return FanStopGpuPowerState.Unknown; }
                result = ClassifyPowerData(power, type);
            }
        }
        finally { _ = SetupDiDestroyDeviceInfoList(set); }
    }

    internal static FanStopGpuPowerState Classify(uint deviceState) => deviceState switch
    {
        1 => FanStopGpuPowerState.PoweredOn, // PowerDeviceD0
        4 => FanStopGpuPowerState.PoweredDown, // PowerDeviceD3 (no D3cold claim)
        _ => FanStopGpuPowerState.Unknown,
    };

    internal static FanStopGpuPowerState ClassifyPowerData(ReadOnlySpan<byte> data, uint type) =>
        type == 3 && data.Length == 56 && BinaryPrimitives.ReadUInt32LittleEndian(data) == data.Length
            ? Classify(BinaryPrimitives.ReadUInt32LittleEndian(data[4..])) : FanStopGpuPowerState.Unknown;

    [StructLayout(LayoutKind.Sequential)]
    private struct DeviceInfo { internal int Size; internal Guid ClassGuid; internal uint DevInst; internal nuint Reserved; }

    [DllImport("setupapi.dll", EntryPoint = "SetupDiGetClassDevsW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern nint SetupDiGetClassDevs(ref Guid deviceClass, string? enumerator, nint parent, uint flags);
    [DllImport("setupapi.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetupDiEnumDeviceInfo(nint set, uint index, ref DeviceInfo info);
    [DllImport("setupapi.dll", EntryPoint = "SetupDiGetDeviceInstanceIdW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetupDiGetDeviceInstanceId(nint set, ref DeviceInfo info, StringBuilder value, int capacity, out int required);
    [DllImport("setupapi.dll", EntryPoint = "SetupDiGetDeviceRegistryPropertyW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetupDiGetDeviceRegistryProperty(nint set, ref DeviceInfo info, uint property,
        out uint type, [Out] byte[] buffer, uint length, out uint required);
    [DllImport("setupapi.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetupDiDestroyDeviceInfoList(nint set);
}
