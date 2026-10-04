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
        using var proof = CaptureGetter();
        return proof?.IsCurrent() == true ? proof.Identity : null;
    }

    internal static CpuGetterUsbContinuity? CaptureGetter()
    {
        if (!OperatingSystem.IsWindows()) { return null; }
        IntPtr deviceSet = SetupDiGetClassDevs(IntPtr.Zero, "USB", IntPtr.Zero, 0x02 | 0x04);
        if (deviceSet == new IntPtr(-1)) { throw new Win32Exception(Marshal.GetLastWin32Error()); }
        bool retained = false;
        try
        {
            int exactDevices = 0;
            uint exactNode = 0;
            string? exactInstance = null;
            for (uint index = 0; index < 4096; index++)
            {
                var info = new SpDevInfoData { Size = (uint)Marshal.SizeOf<SpDevInfoData>() };
                if (!SetupDiEnumDeviceInfo(deviceSet, index, ref info))
                {
                    int failure = Marshal.GetLastWin32Error();
                    if (failure != 259) { throw new Win32Exception(failure); }
                    if (exactDevices != 1 || exactInstance is null) { return null; }
                    var proof = new CpuGetterUsbContinuity(() => ReadNode(exactNode), exactInstance,
                        () => { if (!SetupDiDestroyDeviceInfoList(deviceSet)) { throw new Win32Exception(Marshal.GetLastWin32Error()); } });
                    retained = true;
                    return proof;
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
                    exactNode = info.DeviceInstance; exactInstance = instanceId.ToString();
                }
            }
            // Incomplete or ambiguous enumeration never establishes identity.
            return null;
        }
        finally { if (!retained) { _ = SetupDiDestroyDeviceInfoList(deviceSet); } }
    }

    private static CpuGetterUsbNode ReadNode(uint node)
    {
        var id = new StringBuilder(1024);
        if (CM_Get_Device_ID(node, id, id.Capacity, 0) != 0
            || CM_Get_DevNode_Status(out uint status, out uint problem, node, 0) != 0)
        { return new(null, 0, 1); }
        return new(id.ToString(), status, problem);
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
    [DllImport("cfgmgr32.dll", EntryPoint = "CM_Get_Device_IDW", CharSet = CharSet.Unicode)]
    private static extern uint CM_Get_Device_ID(uint deviceInstance, StringBuilder id, int length, uint flags);
    [DllImport("cfgmgr32.dll")]
    private static extern uint CM_Get_DevNode_Status(out uint status, out uint problem, uint deviceInstance, uint flags);
}

internal readonly record struct CpuGetterUsbNode(string? InstanceId, uint Status, uint Problem);

// Keeps the exact enumerated devnode/instance rather than re-enumerating all USB
// devices. The originating getter still requires full unique-device revalidation.
internal sealed class CpuGetterUsbContinuity(Func<CpuGetterUsbNode> read, string instance, Action release) : IDisposable
{
    private int revoked, disposed;
    internal DeviceIdentity Identity { get; } = new("RZ09-0528", 0x1532, 0x02C6, "Unknown");
    internal bool IsCurrent()
    {
        if (Volatile.Read(ref revoked) != 0 || Volatile.Read(ref disposed) != 0) { return false; }
        try
        {
            var current = read();
            if (Rz090528CpuFreshUsbIdentity.IsExactUsbInstance(instance)
                && string.Equals(current.InstanceId, instance, StringComparison.OrdinalIgnoreCase)
                && current.Problem == 0 && (current.Status & 0x08) != 0
                && Volatile.Read(ref disposed) == 0 && Volatile.Read(ref revoked) == 0) { return true; }
        }
        catch { }
        Interlocked.Exchange(ref revoked, 1); return false;
    }
    public void Dispose()
    {
        Interlocked.Exchange(ref revoked, 1);
        if (Interlocked.Exchange(ref disposed, 1) == 0) { release(); }
    }
}
