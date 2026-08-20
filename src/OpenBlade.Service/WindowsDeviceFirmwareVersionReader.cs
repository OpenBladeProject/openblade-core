using System.ComponentModel;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Security;
using System.Text;
using OpenBlade.Protocol;

namespace OpenBlade.Service;

internal sealed record WindowsPnpFirmwareCandidate(
    string DeviceInstanceId,
    string FirmwareVersion,
    IReadOnlyList<string>? HardwareIds = null,
    string? Service = null,
    string? ParentDeviceInstanceId = null);

internal sealed record WindowsHidFirmwareCandidate(
    ushort VendorId,
    ushort ProductId,
    ushort UsagePage,
    ushort Usage,
    ushort VersionNumber);

internal static class WindowsDeviceFirmwareVersionReader
{
    internal const string ThunderboltRootRouterId =
        @"USB4\ROOT_DEVICE_ROUTER&VID_8086&PID_5781";
    internal const string ThunderboltRootRouterHardwareId =
        @"USB4\ROOT_DEVICE_ROUTER&VID_8086&PID_5781&REV_0084";
    internal const string ThunderboltParentDeviceId =
        @"PCI\VEN_8086&DEV_5781&SUBSYS_30101A58&REV_84&USB4_MS_CM";
    internal const string Usb4DeviceRouterService = "Usb4DeviceRouter";
    internal const ushort TouchpadVendorId = 0x093A;
    internal const ushort TouchpadProductId = 0x3001;
    internal const ushort TouchpadUsagePage = 0x000D;
    internal const ushort TouchpadUsage = 0x0005;

    private const uint DigcfPresent = 0x00000002;
    private const uint DigcfAllClasses = 0x00000004;
    private const int ErrorInsufficientBuffer = 122;
    private const int ErrorNoMoreItems = 259;
    private const uint DevpropTypeString = 0x00000012;
    private const uint DevpropTypeStringList = 0x00002012;
    private static readonly IntPtr InvalidHandleValue = new(-1);
    private static readonly DevPropKey FirmwareVersionKey = new(
        new Guid("540b947e-8b40-45bc-a8a2-6a0b894cbda2"),
        18);
    private static readonly DevPropKey HardwareIdsKey = new(
        new Guid("a45c254e-df1c-4efd-8020-67d146a850e0"),
        3);
    private static readonly DevPropKey ServiceKey = new(
        new Guid("a45c254e-df1c-4efd-8020-67d146a850e0"),
        6);
    private static readonly DevPropKey ParentKey = new(
        new Guid("4340a6c5-93fa-4706-972c-7b648008a5a7"),
        8);

    public static string? ReadThunderboltFirmwareVersion()
    {
        if (!OperatingSystem.IsWindows())
        {
            return null;
        }

        try
        {
            return SelectThunderboltFirmwareVersion(EnumerateUsb4FirmwareCandidates());
        }
        catch (Exception exception) when (IsSafeReadFailure(exception))
        {
            return null;
        }
    }

    public static string? ReadTouchpadFirmwareVersion()
    {
        if (!OperatingSystem.IsWindows())
        {
            return null;
        }

        try
        {
            return SelectTouchpadFirmwareVersion(
                WindowsHidEnumerator.FindCollections(TouchpadVendorId, TouchpadProductId)
                    .Select(candidate => new WindowsHidFirmwareCandidate(
                        candidate.VendorId,
                        candidate.ProductId,
                        candidate.UsagePage,
                        candidate.Usage,
                        candidate.VersionNumber)));
        }
        catch (Exception exception) when (IsSafeReadFailure(exception))
        {
            return null;
        }
    }

    internal static string? SelectThunderboltFirmwareVersion(
        IEnumerable<WindowsPnpFirmwareCandidate> candidates)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        WindowsPnpFirmwareCandidate[] exactCandidates = candidates
            .Where(candidate => candidate.DeviceInstanceId.StartsWith(
                $"{ThunderboltRootRouterId}\\",
                StringComparison.OrdinalIgnoreCase)
                && candidate.HardwareIds?.Any(hardwareId => string.Equals(
                    hardwareId,
                    ThunderboltRootRouterHardwareId,
                    StringComparison.OrdinalIgnoreCase)) == true
                && string.Equals(
                    candidate.Service,
                    Usb4DeviceRouterService,
                    StringComparison.OrdinalIgnoreCase)
                && candidate.ParentDeviceInstanceId?.StartsWith(
                    $"{ThunderboltParentDeviceId}\\",
                    StringComparison.OrdinalIgnoreCase) == true)
            .ToArray();
        if (exactCandidates.Length != 1)
        {
            return null;
        }

        string value = exactCandidates[0].FirmwareVersion.Trim();
        return Version.TryParse(value, out _)
            ? value
            : null;
    }

    internal static string? SelectTouchpadFirmwareVersion(
        IEnumerable<WindowsHidFirmwareCandidate> interfaces)
    {
        ArgumentNullException.ThrowIfNull(interfaces);
        WindowsHidFirmwareCandidate[] exactInterfaces = interfaces
            .Where(candidate =>
                candidate.VendorId == TouchpadVendorId
                && candidate.ProductId == TouchpadProductId
                && candidate.UsagePage == TouchpadUsagePage
                && candidate.Usage == TouchpadUsage)
            .ToArray();
        if (exactInterfaces.Length != 1 || exactInterfaces[0].VersionNumber == 0)
        {
            return null;
        }

        return TryFormatBcdVersion(exactInterfaces[0].VersionNumber, out string? version)
            ? version
            : null;
    }

    internal static bool TryFormatBcdVersion(ushort value, out string? version)
    {
        byte major = (byte)(value >> 8);
        byte minor = (byte)value;
        if (!TryDecodeBcdByte(major, out int decodedMajor)
            || !TryDecodeBcdByte(minor, out int decodedMinor))
        {
            version = null;
            return false;
        }

        version = string.Create(
            CultureInfo.InvariantCulture,
            $"{decodedMajor}.{decodedMinor:D2}");
        return true;
    }

    private static bool TryDecodeBcdByte(byte value, out int decoded)
    {
        int high = value >> 4;
        int low = value & 0x0F;
        if (high > 9 || low > 9)
        {
            decoded = 0;
            return false;
        }

        decoded = (high * 10) + low;
        return true;
    }

    private static IReadOnlyList<WindowsPnpFirmwareCandidate>
        EnumerateUsb4FirmwareCandidates()
    {
        IntPtr deviceSet = SetupDiGetClassDevs(
            IntPtr.Zero,
            "USB4",
            IntPtr.Zero,
            DigcfPresent | DigcfAllClasses);
        if (deviceSet == InvalidHandleValue)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error());
        }

        try
        {
            List<WindowsPnpFirmwareCandidate> candidates = [];
            for (uint index = 0; ; index++)
            {
                SpDevInfoData deviceInfo = new()
                {
                    Size = (uint)Marshal.SizeOf<SpDevInfoData>(),
                };
                if (!SetupDiEnumDeviceInfo(deviceSet, index, ref deviceInfo))
                {
                    int error = Marshal.GetLastWin32Error();
                    if (error == ErrorNoMoreItems)
                    {
                        break;
                    }

                    throw new Win32Exception(error);
                }

                string? deviceInstanceId = ReadDeviceInstanceId(deviceSet, ref deviceInfo);
                if (deviceInstanceId is null
                    || !deviceInstanceId.StartsWith(
                        $"{ThunderboltRootRouterId}\\",
                        StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                string? firmwareVersion = ReadStringProperty(
                    deviceSet,
                    ref deviceInfo,
                    FirmwareVersionKey);
                IReadOnlyList<string> hardwareIds = ReadStringListProperty(
                    deviceSet,
                    ref deviceInfo,
                    HardwareIdsKey);
                string? service = ReadStringProperty(
                    deviceSet,
                    ref deviceInfo,
                    ServiceKey);
                string? parentDeviceInstanceId = ReadStringProperty(
                    deviceSet,
                    ref deviceInfo,
                    ParentKey);
                if (!string.IsNullOrWhiteSpace(firmwareVersion)
                    && hardwareIds.Count > 0
                    && !string.IsNullOrWhiteSpace(service)
                    && !string.IsNullOrWhiteSpace(parentDeviceInstanceId))
                {
                    candidates.Add(new(
                        deviceInstanceId,
                        firmwareVersion,
                        hardwareIds,
                        service,
                        parentDeviceInstanceId));
                }
            }

            return candidates;
        }
        finally
        {
            _ = SetupDiDestroyDeviceInfoList(deviceSet);
        }
    }

    private static string? ReadDeviceInstanceId(
        IntPtr deviceSet,
        ref SpDevInfoData deviceInfo)
    {
        var buffer = new StringBuilder(1024);
        return SetupDiGetDeviceInstanceId(
            deviceSet,
            ref deviceInfo,
            buffer,
            buffer.Capacity,
            out _)
            ? buffer.ToString()
            : null;
    }

    private static string? ReadStringProperty(
        IntPtr deviceSet,
        ref SpDevInfoData deviceInfo,
        DevPropKey key)
    {
        (IntPtr Buffer, uint Size)? property = ReadProperty(
            deviceSet,
            ref deviceInfo,
            key,
            DevpropTypeString);
        if (property is not { } value)
        {
            return null;
        }

        try
        {
            return Marshal.PtrToStringUni(value.Buffer)?.TrimEnd('\0');
        }
        finally
        {
            Marshal.FreeHGlobal(value.Buffer);
        }
    }

    private static IReadOnlyList<string> ReadStringListProperty(
        IntPtr deviceSet,
        ref SpDevInfoData deviceInfo,
        DevPropKey key)
    {
        (IntPtr Buffer, uint Size)? property = ReadProperty(
            deviceSet,
            ref deviceInfo,
            key,
            DevpropTypeStringList);
        if (property is not { } value)
        {
            return [];
        }

        try
        {
            string text = Marshal.PtrToStringUni(
                value.Buffer,
                checked((int)value.Size / sizeof(char))) ?? string.Empty;
            return text
                .Split('\0', StringSplitOptions.RemoveEmptyEntries)
                .Where(item => !string.IsNullOrWhiteSpace(item))
                .Select(item => item.Trim())
                .ToArray();
        }
        finally
        {
            Marshal.FreeHGlobal(value.Buffer);
        }
    }

    private static (IntPtr Buffer, uint Size)? ReadProperty(
        IntPtr deviceSet,
        ref SpDevInfoData deviceInfo,
        DevPropKey key,
        uint expectedPropertyType)
    {
        DevPropKey propertyKey = key;
        _ = SetupDiGetDeviceProperty(
            deviceSet,
            ref deviceInfo,
            ref propertyKey,
            out uint propertyType,
            IntPtr.Zero,
            0,
            out uint requiredSize,
            0);
        int error = Marshal.GetLastWin32Error();
        if (requiredSize == 0 || error != ErrorInsufficientBuffer)
        {
            return null;
        }

        IntPtr buffer = Marshal.AllocHGlobal(checked((int)requiredSize));
        if (SetupDiGetDeviceProperty(
                deviceSet,
                ref deviceInfo,
                ref propertyKey,
                out propertyType,
                buffer,
                requiredSize,
                out _,
                0)
            && propertyType == expectedPropertyType)
        {
            return (buffer, requiredSize);
        }

        Marshal.FreeHGlobal(buffer);
        return null;
    }

    private static bool IsSafeReadFailure(Exception exception) => exception is
        IOException or UnauthorizedAccessException or SecurityException or Win32Exception;

    [StructLayout(LayoutKind.Sequential)]
    private struct SpDevInfoData
    {
        public uint Size;
        public Guid ClassGuid;
        public uint DeviceInstance;
        public UIntPtr Reserved;
    }

    [StructLayout(LayoutKind.Sequential)]
    private readonly struct DevPropKey(Guid formatId, uint propertyId)
    {
        public readonly Guid FormatId = formatId;
        public readonly uint PropertyId = propertyId;
    }

    [DllImport(
        "setupapi.dll",
        EntryPoint = "SetupDiGetClassDevsW",
        CharSet = CharSet.Unicode,
        SetLastError = true)]
    private static extern IntPtr SetupDiGetClassDevs(
        IntPtr classGuid,
        string? enumerator,
        IntPtr parentWindow,
        uint flags);

    [DllImport("setupapi.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetupDiEnumDeviceInfo(
        IntPtr deviceSet,
        uint memberIndex,
        ref SpDevInfoData deviceInfo);

    [DllImport(
        "setupapi.dll",
        EntryPoint = "SetupDiGetDeviceInstanceIdW",
        CharSet = CharSet.Unicode,
        SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetupDiGetDeviceInstanceId(
        IntPtr deviceSet,
        ref SpDevInfoData deviceInfo,
        StringBuilder deviceInstanceId,
        int deviceInstanceIdSize,
        out int requiredSize);

    [DllImport(
        "setupapi.dll",
        EntryPoint = "SetupDiGetDevicePropertyW",
        SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetupDiGetDeviceProperty(
        IntPtr deviceSet,
        ref SpDevInfoData deviceInfo,
        ref DevPropKey propertyKey,
        out uint propertyType,
        IntPtr propertyBuffer,
        uint propertyBufferSize,
        out uint requiredSize,
        uint flags);

    [DllImport("setupapi.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetupDiDestroyDeviceInfoList(IntPtr deviceSet);
}
