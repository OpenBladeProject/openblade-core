using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace OpenBlade.Capture;

internal sealed record WindowsBatteryObservationSnapshot(
    WindowsBatteryObservationResult Enumeration,
    IReadOnlyList<WindowsBatteryObservation> Batteries);

internal sealed record WindowsBatteryObservation(
    int Ordinal,
    bool? IsSystemBattery,
    WindowsBatteryObservationResult Identity,
    WindowsBatteryTemperatureObservation Temperature,
    WindowsBatteryStatusObservation Status);

internal sealed record WindowsBatteryObservationResult(
    string Outcome,
    string Stage,
    int? NativeError = null,
    uint? ReturnedBytes = null);

internal sealed record WindowsBatteryTemperatureObservation(
    WindowsBatteryObservationResult Result,
    uint? RawDeciKelvin = null,
    double? Celsius = null);

internal sealed record WindowsBatteryStatusObservation(
    WindowsBatteryObservationResult Result,
    uint? PowerStateFlags = null,
    uint? Capacity = null,
    uint? VoltageMillivolts = null,
    int? Rate = null,
    bool? CapacityAndRateAreRelative = null);

// Developer capture only. The caller isolates this synchronous reader behind a process deadline.
internal static class WindowsBatteryObservationReader
{
    private static readonly Guid BatteryInterfaceClassGuid = new("72631E54-78A4-11D0-BCF7-00AA00B7B32A");

    private const int MaxBatteries = 16;
    private const uint MaxDetailBytes = 64 * 1024;
    private const int ErrorInvalidFunction = 1;
    private const int ErrorFileNotFound = 2;
    private const int ErrorNotSupported = 50;
    private const int ErrorInsufficientBuffer = 122;
    private const int ErrorNoMoreItems = 259;
    private const int ErrorNoSuchDevice = 433;

    private const uint Present = 0x00000002;
    private const uint DeviceInterface = 0x00000010;
    private const uint GenericRead = 0x80000000;
    private const uint ShareReadWrite = 0x00000003;
    private const uint OpenExisting = 3;

    // Windows SDK poclass.h: all three requests use FILE_READ_ACCESS.
    private const uint IoctlBatteryQueryTag = 0x00294040;
    private const uint IoctlBatteryQueryInformation = 0x00294044;
    private const uint IoctlBatteryQueryStatus = 0x0029404C;
    private const uint BatterySystemBattery = 0x80000000;
    private const uint BatteryCapacityRelative = 0x40000000;
    private const int BatteryInformationLevel = 0;
    private const int BatteryTemperatureLevel = 2;

    public static WindowsBatteryObservationSnapshot Capture()
    {
        Guid batteryGuid = BatteryInterfaceClassGuid;
        IntPtr set = SetupDiGetClassDevs(ref batteryGuid, null, IntPtr.Zero, Present | DeviceInterface);
        if (set == new IntPtr(-1))
        {
            return new(new("NativeFailure", "GetClassDevices", Marshal.GetLastPInvokeError()), []);
        }

        List<WindowsBatteryObservation> batteries = [];
        try
        {
            for (int index = 0; ; index++)
            {
                DeviceInterfaceData data = new() { Size = Marshal.SizeOf<DeviceInterfaceData>() };
                if (!SetupDiEnumDeviceInterfaces(set, IntPtr.Zero, ref batteryGuid, (uint)index, ref data))
                {
                    int error = Marshal.GetLastPInvokeError();
                    WindowsBatteryObservationResult enumeration = error == ErrorNoMoreItems
                        ? new(batteries.Count == 0 ? "NoBatteryInterfaces" : "Read", "EnumerateInterfaces")
                        : new("NativeFailure", "EnumerateInterfaces", error);
                    return new(enumeration, batteries);
                }

                // Probe only for existence of another interface so exactly 16 is still a complete result.
                if (index == MaxBatteries)
                {
                    return new(new("InterfaceLimitReached", "EnumerateInterfaces"), batteries);
                }

                int ordinal = index + 1;
                WindowsBatteryObservationResult pathResult = ReadPath(set, ref data, out string? path);
                batteries.Add(path is null ? Incomplete(ordinal, pathResult) : ReadBattery(ordinal, path));
            }
        }
        finally
        {
            _ = SetupDiDestroyDeviceInfoList(set);
        }
    }

    private static WindowsBatteryObservationResult ReadPath(IntPtr set, ref DeviceInterfaceData data, out string? path)
    {
        path = null;
        bool sized = SetupDiGetDeviceInterfaceDetail(set, ref data, IntPtr.Zero, 0, out uint required, IntPtr.Zero);
        int error = Marshal.GetLastPInvokeError();
        int headerSize = IntPtr.Size == 8 ? 8 : 6;
        if (sized || error != ErrorInsufficientBuffer)
        {
            return new(sized ? "InvalidDetailSize" : "NativeFailure", "SizeInterfaceDetail", sized ? null : error);
        }

        if (required < headerSize || required > MaxDetailBytes || required % sizeof(char) != 0)
        {
            return new("InvalidDetailSize", "SizeInterfaceDetail");
        }

        IntPtr detail = Marshal.AllocHGlobal((int)required);
        try
        {
            Marshal.WriteInt32(detail, headerSize);
            if (!SetupDiGetDeviceInterfaceDetail(set, ref data, detail, required, out uint written, IntPtr.Zero))
            {
                return new("NativeFailure", "ReadInterfaceDetail", Marshal.GetLastPInvokeError());
            }

            if (written < headerSize || written > required || written % sizeof(char) != 0)
            {
                return new("InvalidDetailSize", "ReadInterfaceDetail", ReturnedBytes: written);
            }

            // The DevicePath field starts after the four-byte cbSize, even on x64.
            for (int offset = sizeof(int); offset < written; offset += sizeof(char))
            {
                if (Marshal.ReadInt16(detail, offset) == 0)
                {
                    string? value = Marshal.PtrToStringUni(IntPtr.Add(detail, sizeof(int)), (offset - sizeof(int)) / sizeof(char));
                    if (!string.IsNullOrWhiteSpace(value))
                    {
                        path = value;
                        return new("Read", "ReadInterfaceDetail", ReturnedBytes: written);
                    }

                    break;
                }
            }

            return new("InvalidDevicePath", "ReadInterfaceDetail", ReturnedBytes: written);
        }
        finally
        {
            Marshal.FreeHGlobal(detail);
        }
    }

    private static WindowsBatteryObservation ReadBattery(int ordinal, string path)
    {
        using SafeFileHandle handle = CreateFile(path, GenericRead, ShareReadWrite, IntPtr.Zero, OpenExisting, 0, IntPtr.Zero);
        if (handle.IsInvalid)
        {
            return Incomplete(ordinal, new("NativeFailure", "OpenBattery", Marshal.GetLastPInvokeError()));
        }

        uint waitMilliseconds = 0;
        bool readTag = QueryTag(handle, IoctlBatteryQueryTag, ref waitMilliseconds, sizeof(uint),
            out uint tag, sizeof(uint), out uint tagBytes, IntPtr.Zero);
        int tagError = readTag ? 0 : Marshal.GetLastPInvokeError();
        WindowsBatteryObservationResult tagResult = DecodeRead("QueryTag", readTag, tagError, tagBytes, sizeof(uint));
        if (tagResult.Outcome != "Read" || tag == 0)
        {
            return Incomplete(ordinal, tagResult.Outcome == "Read" ? tagResult with { Outcome = "InvalidBatteryTag" } : tagResult);
        }

        BatteryQueryInformation query = new() { Tag = tag, Level = BatteryInformationLevel };
        uint querySize = (uint)Marshal.SizeOf<BatteryQueryInformation>();
        uint informationSize = (uint)Marshal.SizeOf<BatteryInformation>();
        bool readInformation = QueryInformation(handle, IoctlBatteryQueryInformation, ref query, querySize,
            out BatteryInformation information, informationSize, out uint informationBytes, IntPtr.Zero);
        int informationError = readInformation ? 0 : Marshal.GetLastPInvokeError();
        WindowsBatteryObservationResult identity = DecodeRead("QueryInformation", readInformation, informationError,
            informationBytes, informationSize);
        if (identity.Outcome != "Read")
        {
            return Incomplete(ordinal, identity);
        }

        if ((information.Capabilities & BatterySystemBattery) == 0)
        {
            return Incomplete(ordinal, identity, isSystemBattery: false, reason: "NotSystemBattery");
        }

        query.Level = BatteryTemperatureLevel;
        bool readTemperature = QueryTemperature(handle, IoctlBatteryQueryInformation, ref query, querySize,
            out uint temperature, sizeof(uint), out uint temperatureBytes, IntPtr.Zero);
        int temperatureError = readTemperature ? 0 : Marshal.GetLastPInvokeError();
        WindowsBatteryTemperatureObservation decodedTemperature = DecodeTemperature(readTemperature, temperatureError,
            temperatureBytes, temperature);
        if (decodedTemperature.Result.Outcome is "NativeFailure" or "BatteryTagChanged" or "UnexpectedByteCount")
        {
            return new(ordinal, true, identity, decodedTemperature, new(new("NotQueried", "QueryStatus")));
        }

        // Timeout=0 requests an immediate status read for the same battery generation.
        BatteryWaitStatus waitStatus = new() { Tag = tag };
        uint statusSize = (uint)Marshal.SizeOf<BatteryStatus>();
        bool readStatus = QueryStatus(handle, IoctlBatteryQueryStatus, ref waitStatus, (uint)Marshal.SizeOf<BatteryWaitStatus>(),
            out BatteryStatus status, statusSize, out uint statusBytes, IntPtr.Zero);
        int statusError = readStatus ? 0 : Marshal.GetLastPInvokeError();
        WindowsBatteryStatusObservation decodedStatus = DecodeStatus(readStatus, statusError, statusBytes,
            status.PowerState, status.Capacity, status.Voltage, status.Rate, (information.Capabilities & BatteryCapacityRelative) != 0);
        if (decodedStatus.Result.Outcome == "BatteryTagChanged")
        {
            // The earlier temperature must not be joined to a now-invalid battery generation.
            decodedTemperature = new(decodedStatus.Result, decodedTemperature.RawDeciKelvin);
        }

        return new(ordinal, true, identity, decodedTemperature, decodedStatus);
    }

    private static WindowsBatteryObservation Incomplete(
        int ordinal,
        WindowsBatteryObservationResult identity,
        bool? isSystemBattery = null,
        string reason = "NotQueried") =>
        new(ordinal, isSystemBattery, identity, new(new(reason, "QueryTemperature")), new(new(reason, "QueryStatus")));

    internal static WindowsBatteryTemperatureObservation DecodeTemperature(bool success, int error, uint bytes, uint raw)
    {
        WindowsBatteryObservationResult result = DecodeRead("QueryTemperature", success, error, bytes, sizeof(uint));
        if (result.Outcome != "Read")
        {
            return new(result);
        }

        double celsius = (raw / 10.0) - 273.15;
        // This is a broad plausibility check for a reported pack temperature, not a safe operating range.
        return celsius is < -80 or > 150
            ? new(result with { Outcome = "ImplausibleTemperature" }, raw)
            : new(result, raw, celsius);
    }

    internal static WindowsBatteryStatusObservation DecodeStatus(
        bool success, int error, uint bytes, uint powerState, uint capacity, uint voltage, int rate, bool relative)
    {
        WindowsBatteryObservationResult result = DecodeRead("QueryStatus", success, error, bytes, (uint)Marshal.SizeOf<BatteryStatus>());
        return result.Outcome != "Read"
            ? new(result)
            : new(result, powerState, capacity == uint.MaxValue ? null : capacity,
                voltage == uint.MaxValue ? null : voltage, rate == int.MinValue ? null : rate, relative);
    }

    private static WindowsBatteryObservationResult DecodeRead(string stage, bool success, int error, uint bytes, uint expected)
    {
        if (success)
        {
            return new(bytes == expected ? "Read" : "UnexpectedByteCount", stage, ReturnedBytes: bytes);
        }

        string outcome = error switch
        {
            ErrorInvalidFunction or ErrorNotSupported => "Unsupported",
            ErrorFileNotFound or ErrorNoSuchDevice => stage == "QueryTag" ? "BatteryNotPresent" : "BatteryTagChanged",
            _ => "NativeFailure",
        };
        return new(outcome, stage, error, bytes);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DeviceInterfaceData
    {
        public int Size;
        public Guid InterfaceClassGuid;
        public uint Flags;
        public UIntPtr Reserved;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BatteryQueryInformation
    {
        public uint Tag;
        public int Level;
        public uint AtRate;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BatteryInformation
    {
        public uint Capabilities;
        public uint TechnologyAndReserved;
        public uint Chemistry;
        public uint DesignedCapacity;
        public uint FullChargedCapacity;
        public uint DefaultAlert1;
        public uint DefaultAlert2;
        public uint CriticalBias;
        public uint CycleCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BatteryWaitStatus
    {
        public uint Tag;
        public uint Timeout;
        public uint PowerState;
        public uint LowCapacity;
        public uint HighCapacity;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BatteryStatus
    {
        public uint PowerState;
        public uint Capacity;
        public uint Voltage;
        public int Rate;
    }

    [DllImport("setupapi.dll", EntryPoint = "SetupDiGetClassDevsW", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr SetupDiGetClassDevs(ref Guid classGuid, string? enumerator, IntPtr parentWindow, uint flags);

    [DllImport("setupapi.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetupDiEnumDeviceInterfaces(IntPtr deviceInfoSet, IntPtr deviceInfoData,
        ref Guid interfaceClassGuid, uint memberIndex, ref DeviceInterfaceData deviceInterfaceData);

    [DllImport("setupapi.dll", EntryPoint = "SetupDiGetDeviceInterfaceDetailW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetupDiGetDeviceInterfaceDetail(IntPtr deviceInfoSet, ref DeviceInterfaceData deviceInterfaceData,
        IntPtr deviceInterfaceDetailData, uint deviceInterfaceDetailDataSize, out uint requiredSize, IntPtr deviceInfoData);

    [DllImport("setupapi.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetupDiDestroyDeviceInfoList(IntPtr deviceInfoSet);

    [DllImport("kernel32.dll", EntryPoint = "CreateFileW", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern SafeFileHandle CreateFile(string fileName, uint desiredAccess, uint shareMode,
        IntPtr securityAttributes, uint creationDisposition, uint flagsAndAttributes, IntPtr templateFile);

    [DllImport("kernel32.dll", EntryPoint = "DeviceIoControl", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryTag(SafeFileHandle device, uint code, ref uint input, uint inputSize,
        out uint output, uint outputSize, out uint bytes, IntPtr overlapped);

    [DllImport("kernel32.dll", EntryPoint = "DeviceIoControl", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryInformation(SafeFileHandle device, uint code, ref BatteryQueryInformation input,
        uint inputSize, out BatteryInformation output, uint outputSize, out uint bytes, IntPtr overlapped);

    [DllImport("kernel32.dll", EntryPoint = "DeviceIoControl", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryTemperature(SafeFileHandle device, uint code, ref BatteryQueryInformation input,
        uint inputSize, out uint output, uint outputSize, out uint bytes, IntPtr overlapped);

    [DllImport("kernel32.dll", EntryPoint = "DeviceIoControl", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryStatus(SafeFileHandle device, uint code, ref BatteryWaitStatus input,
        uint inputSize, out BatteryStatus output, uint outputSize, out uint bytes, IntPtr overlapped);
}
