using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using OpenBlade.Contracts;

namespace OpenBlade.Service;

internal static class WindowsBatteryHealthReader
{
    private static readonly Guid BatteryInterfaceClassGuid =
        new("72631E54-78A4-11D0-BCF7-00AA00B7B32A");

    private const int ErrorInsufficientBuffer = 122;
    private const int ErrorNoMoreItems = 259;
    private const uint Present = 0x00000002;
    private const uint DeviceInterface = 0x00000010;
    private const uint GenericRead = 0x80000000;
    private const uint ShareRead = 0x00000001;
    private const uint ShareWrite = 0x00000002;
    private const uint OpenExisting = 3;
    private const uint BatteryCapacityRelative = 0x40000000;
    private const uint IoctlBatteryQueryTag = 0x00294040;
    private const uint IoctlBatteryQueryInformation = 0x00294044;

    public static BatteryHealthStatus? Capture()
    {
        Guid batteryGuid = BatteryInterfaceClassGuid;
        IntPtr deviceSet = SetupDiGetClassDevs(
            ref batteryGuid,
            null,
            IntPtr.Zero,
            Present | DeviceInterface);
        if (deviceSet == new IntPtr(-1))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error());
        }

        try
        {
            List<(uint DesignCapacity, uint FullChargeCapacity)> capacities = [];
            for (uint index = 0; ; index++)
            {
                if (!TryGetDevicePath(deviceSet, ref batteryGuid, index, out string? path))
                {
                    break;
                }

                if (path is not null
                    && TryReadCapacity(path, out BatteryInformation information)
                    && (information.Capabilities & BatteryCapacityRelative) == 0)
                {
                    capacities.Add((
                        information.DesignedCapacity,
                        information.FullChargedCapacity));
                }
            }

            return WindowsPowerStateProvider.MapBatteryHealth(capacities);
        }
        finally
        {
            _ = SetupDiDestroyDeviceInfoList(deviceSet);
        }
    }

    private static bool TryGetDevicePath(
        IntPtr deviceSet,
        ref Guid batteryGuid,
        uint index,
        out string? devicePath)
    {
        DeviceInterfaceData interfaceData = new()
        {
            Size = Marshal.SizeOf<DeviceInterfaceData>(),
        };
        if (!SetupDiEnumDeviceInterfaces(
            deviceSet,
            IntPtr.Zero,
            ref batteryGuid,
            index,
            ref interfaceData))
        {
            int error = Marshal.GetLastWin32Error();
            devicePath = null;
            if (error == ErrorNoMoreItems)
            {
                return false;
            }

            throw new Win32Exception(error);
        }

        bool sizeRead = SetupDiGetDeviceInterfaceDetail(
            deviceSet,
            ref interfaceData,
            IntPtr.Zero,
            0,
            out uint requiredSize,
            IntPtr.Zero);
        int sizeError = Marshal.GetLastWin32Error();
        uint minimumSize = checked((uint)(IntPtr.Size == 8 ? 8 : 6));
        if (sizeRead
            || sizeError != ErrorInsufficientBuffer
            || requiredSize < minimumSize
            || requiredSize > int.MaxValue)
        {
            throw new Win32Exception(sizeError);
        }

        IntPtr detail = Marshal.AllocHGlobal(checked((int)requiredSize));
        try
        {
            Marshal.WriteInt32(detail, IntPtr.Size == 8 ? 8 : 6);
            if (!SetupDiGetDeviceInterfaceDetail(
                deviceSet,
                ref interfaceData,
                detail,
                requiredSize,
                out _,
                IntPtr.Zero))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error());
            }

            devicePath = Marshal.PtrToStringUni(IntPtr.Add(detail, 4));
            return !string.IsNullOrWhiteSpace(devicePath);
        }
        finally
        {
            Marshal.FreeHGlobal(detail);
        }
    }

    private static bool TryReadCapacity(
        string path,
        out BatteryInformation information)
    {
        using SafeFileHandle handle = CreateFile(
            path,
            GenericRead,
            ShareRead | ShareWrite,
            IntPtr.Zero,
            OpenExisting,
            0,
            IntPtr.Zero);
        if (handle.IsInvalid)
        {
            information = default;
            return false;
        }

        uint waitMilliseconds = 0;
        if (!DeviceIoControlQueryTag(
            handle,
            IoctlBatteryQueryTag,
            ref waitMilliseconds,
            sizeof(uint),
            out uint batteryTag,
            sizeof(uint),
            out _,
            IntPtr.Zero)
            || batteryTag == 0)
        {
            information = default;
            return false;
        }

        BatteryQueryInformation query = new()
        {
            BatteryTag = batteryTag,
            InformationLevel = BatteryQueryInformationLevel.BatteryInformation,
        };
        return DeviceIoControlQueryInformation(
            handle,
            IoctlBatteryQueryInformation,
            ref query,
            checked((uint)Marshal.SizeOf<BatteryQueryInformation>()),
            out information,
            checked((uint)Marshal.SizeOf<BatteryInformation>()),
            out _,
            IntPtr.Zero);
    }

    private enum BatteryQueryInformationLevel
    {
        BatteryInformation = 0,
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DeviceInterfaceData
    {
        public int Size;
        public Guid InterfaceClassGuid;
        public int Flags;
        public UIntPtr Reserved;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BatteryQueryInformation
    {
        public uint BatteryTag;
        public BatteryQueryInformationLevel InformationLevel;
        public int AtRate;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BatteryInformation
    {
        public uint Capabilities;
        public byte Technology;
        public byte Reserved1;
        public byte Reserved2;
        public byte Reserved3;
        public byte Chemistry1;
        public byte Chemistry2;
        public byte Chemistry3;
        public byte Chemistry4;
        public uint DesignedCapacity;
        public uint FullChargedCapacity;
        public uint DefaultAlert1;
        public uint DefaultAlert2;
        public uint CriticalBias;
        public uint CycleCount;
    }

    [DllImport(
        "setupapi.dll",
        EntryPoint = "SetupDiGetClassDevsW",
        SetLastError = true,
        CharSet = CharSet.Unicode)]
    private static extern IntPtr SetupDiGetClassDevs(
        ref Guid classGuid,
        string? enumerator,
        IntPtr parentWindow,
        uint flags);

    [DllImport("setupapi.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetupDiEnumDeviceInterfaces(
        IntPtr deviceInfoSet,
        IntPtr deviceInfoData,
        ref Guid interfaceClassGuid,
        uint memberIndex,
        ref DeviceInterfaceData deviceInterfaceData);

    [DllImport(
        "setupapi.dll",
        EntryPoint = "SetupDiGetDeviceInterfaceDetailW",
        SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetupDiGetDeviceInterfaceDetail(
        IntPtr deviceInfoSet,
        ref DeviceInterfaceData deviceInterfaceData,
        IntPtr deviceInterfaceDetailData,
        uint deviceInterfaceDetailDataSize,
        out uint requiredSize,
        IntPtr deviceInfoData);

    [DllImport("setupapi.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetupDiDestroyDeviceInfoList(IntPtr deviceInfoSet);

    [DllImport(
        "kernel32.dll",
        EntryPoint = "CreateFileW",
        SetLastError = true,
        CharSet = CharSet.Unicode)]
    private static extern SafeFileHandle CreateFile(
        string fileName,
        uint desiredAccess,
        uint shareMode,
        IntPtr securityAttributes,
        uint creationDisposition,
        uint flagsAndAttributes,
        IntPtr templateFile);

    [DllImport(
        "kernel32.dll",
        EntryPoint = "DeviceIoControl",
        SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeviceIoControlQueryTag(
        SafeFileHandle device,
        uint ioControlCode,
        ref uint inputBuffer,
        uint inputBufferSize,
        out uint outputBuffer,
        uint outputBufferSize,
        out uint bytesReturned,
        IntPtr overlapped);

    [DllImport(
        "kernel32.dll",
        EntryPoint = "DeviceIoControl",
        SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeviceIoControlQueryInformation(
        SafeFileHandle device,
        uint ioControlCode,
        ref BatteryQueryInformation inputBuffer,
        uint inputBufferSize,
        out BatteryInformation outputBuffer,
        uint outputBufferSize,
        out uint bytesReturned,
        IntPtr overlapped);
}
