using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;

namespace OpenBlade.Protocol;

internal static class Rz090528CpuFreshUsbIdentity
{
    // Re-enumerate present USB PnP devnodes for every adapter admission check.
    // Service status preserves last-confirmed identity and is unsuitable as a
    // fresh-device source. This path opens no HID handle or control interface.
    internal static DeviceIdentity? Read()
    {
        if (!OperatingSystem.IsWindows()) { return null; }
        IntPtr deviceSet = SetupDiGetClassDevs(IntPtr.Zero, "USB", IntPtr.Zero, 0x02 | 0x04);
        if (deviceSet == new IntPtr(-1)) { throw new Win32Exception(Marshal.GetLastWin32Error()); }
        try
        {
            int exactDevices = 0;
            for (uint index = 0; index < 4096; index++)
            {
                var info = new SpDevInfoData { Size = (uint)Marshal.SizeOf<SpDevInfoData>() };
                if (!SetupDiEnumDeviceInfo(deviceSet, index, ref info))
                {
                    int failure = Marshal.GetLastWin32Error();
                    if (failure != 259) { throw new Win32Exception(failure); }
                    return exactDevices == 1 ? new("RZ09-0528", 0x1532, 0x02C6, "Unknown") : null;
                }
                var instanceId = new StringBuilder(1024);
                if (!SetupDiGetDeviceInstanceId(deviceSet, ref info, instanceId, instanceId.Capacity, out _))
                { throw new Win32Exception(Marshal.GetLastWin32Error()); }
                if (IsExactUsbInstance(instanceId.ToString()))
                {
                    if (CM_Get_DevNode_Status(out uint status, out uint problem, info.DeviceInstance, 0) != 0
                        || problem != 0 || (status & 0x08) == 0)
                    { return null; }
                    exactDevices++;
                }
            }
            // Incomplete or ambiguous enumeration never establishes identity.
            return null;
        }
        finally { _ = SetupDiDestroyDeviceInfoList(deviceSet); }
    }

    internal static bool IsExactUsbInstance(string instanceId)
    {
        const string prefix = "USB\\VID_1532&PID_02C6\\";
        return instanceId.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
            && instanceId.Length > prefix.Length;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SpDevInfoData
    {
        public uint Size;
        public Guid ClassGuid;
        public uint DeviceInstance;
        public UIntPtr Reserved;
    }

    [DllImport("setupapi.dll", EntryPoint = "SetupDiGetClassDevsW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr SetupDiGetClassDevs(IntPtr classGuid, string enumerator, IntPtr parentWindow, uint flags);
    [DllImport("setupapi.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetupDiEnumDeviceInfo(IntPtr deviceSet, uint index, ref SpDevInfoData info);
    [DllImport("setupapi.dll", EntryPoint = "SetupDiGetDeviceInstanceIdW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetupDiGetDeviceInstanceId(IntPtr deviceSet, ref SpDevInfoData info,
        StringBuilder instanceId, int size, out int requiredSize);
    [DllImport("setupapi.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetupDiDestroyDeviceInfoList(IntPtr deviceSet);
    [DllImport("cfgmgr32.dll")]
    private static extern uint CM_Get_DevNode_Status(out uint status, out uint problem, uint deviceInstance, uint flags);
}
