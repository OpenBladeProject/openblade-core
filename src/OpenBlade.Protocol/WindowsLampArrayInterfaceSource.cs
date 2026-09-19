using System.ComponentModel;
using System.Runtime.InteropServices;

namespace OpenBlade.Protocol;

internal sealed record LampArrayEndpoint(ushort? VendorId, ushort? ProductId);

internal sealed partial class WindowsLampArrayInterfaceSource
{
    private const uint DigcfPresent = 0x00000002;
    private const uint DigcfDeviceInterface = 0x00000010;
    private const uint SpintActive = 0x00000001;
    private const uint DevpropTypeUInt16 = 0x00000005;
    private const int ErrorNoMoreItems = 259;

    private static readonly Guid HidInterfaceClass =
        new("4D1E55B2-F16F-11CF-88CB-001111000030");

    private static readonly Guid HidPropertySet =
        new("CBF38310-4A17-4310-A1EB-247F0B67593B");

    private static readonly DevicePropertyKey UsagePageKey = new(HidPropertySet, 2);
    private static readonly DevicePropertyKey UsageIdKey = new(HidPropertySet, 3);
    private static readonly DevicePropertyKey VendorIdKey = new(HidPropertySet, 5);
    private static readonly DevicePropertyKey ProductIdKey = new(HidPropertySet, 6);

    internal IReadOnlyList<LampArrayEndpoint> Observe()
    {
        Guid hidInterfaceClass = HidInterfaceClass;
        IntPtr deviceInfoSet = SetupDiGetClassDevs(
            ref hidInterfaceClass,
            null,
            IntPtr.Zero,
            DigcfPresent | DigcfDeviceInterface);
        if (deviceInfoSet == new IntPtr(-1))
        {
            throw CreateWin32Exception("SetupDiGetClassDevs", Marshal.GetLastPInvokeError());
        }

        Exception? observationFailure = null;
        try
        {
            List<LampArrayEndpoint> endpoints = [];
            for (uint index = 0; ; index++)
            {
                var interfaceData = new DeviceInterfaceData
                {
                    Size = (uint)Marshal.SizeOf<DeviceInterfaceData>()
                };

                if (!SetupDiEnumDeviceInterfaces(
                        deviceInfoSet,
                        IntPtr.Zero,
                        ref hidInterfaceClass,
                        index,
                        ref interfaceData))
                {
                    int error = Marshal.GetLastPInvokeError();
                    if (error == ErrorNoMoreItems)
                    {
                        break;
                    }

                    throw CreateWin32Exception(
                        $"SetupDiEnumDeviceInterfaces at interface index {index}",
                        error);
                }

                if ((interfaceData.Flags & SpintActive) == 0)
                {
                    continue;
                }

                ushort usagePage = ReadRequiredUInt16(
                    deviceInfoSet,
                    ref interfaceData,
                    UsagePageKey,
                    "HID usage page",
                    index);
                ushort usageId = ReadRequiredUInt16(
                    deviceInfoSet,
                    ref interfaceData,
                    UsageIdKey,
                    "HID usage ID",
                    index);
                if (usagePage != 0x0059 || usageId != 0x0001)
                {
                    continue;
                }

                endpoints.Add(new LampArrayEndpoint(
                    ReadOptionalUInt16(deviceInfoSet, ref interfaceData, VendorIdKey),
                    ReadOptionalUInt16(deviceInfoSet, ref interfaceData, ProductIdKey)));
            }

            return endpoints.ToArray();
        }
        catch (Exception exception)
        {
            observationFailure = exception;
            throw;
        }
        finally
        {
            if (!SetupDiDestroyDeviceInfoList(deviceInfoSet) && observationFailure is null)
            {
                throw CreateWin32Exception(
                    "SetupDiDestroyDeviceInfoList",
                    Marshal.GetLastPInvokeError());
            }
        }
    }

    private static unsafe ushort ReadRequiredUInt16(
        IntPtr deviceInfoSet,
        ref DeviceInterfaceData interfaceData,
        DevicePropertyKey key,
        string propertyName,
        uint interfaceIndex)
    {
        ushort value = 0;
        if (!SetupDiGetDeviceInterfaceProperty(
                deviceInfoSet,
                ref interfaceData,
                in key,
                out uint propertyType,
                (byte*)&value,
                sizeof(ushort),
                out uint requiredSize,
                0))
        {
            throw CreateWin32Exception(
                $"Reading {propertyName} at interface index {interfaceIndex}",
                Marshal.GetLastPInvokeError());
        }

        if (propertyType != DevpropTypeUInt16 || requiredSize != sizeof(ushort))
        {
            throw new InvalidDataException(
                $"{propertyName} at interface index {interfaceIndex} returned " +
                $"type 0x{propertyType:X8} and size {requiredSize}; expected UInt16 and size 2.");
        }

        return value;
    }

    private static unsafe ushort? ReadOptionalUInt16(
        IntPtr deviceInfoSet,
        ref DeviceInterfaceData interfaceData,
        DevicePropertyKey key)
    {
        ushort value = 0;
        if (!SetupDiGetDeviceInterfaceProperty(
                deviceInfoSet,
                ref interfaceData,
                in key,
                out uint propertyType,
                (byte*)&value,
                sizeof(ushort),
                out uint requiredSize,
                0))
        {
            return null;
        }

        return propertyType == DevpropTypeUInt16 && requiredSize == sizeof(ushort)
            ? value
            : null;
    }

    private static Win32Exception CreateWin32Exception(string operation, int error) =>
        new(error, $"{operation} failed with Win32 error {error}.");

    [StructLayout(LayoutKind.Sequential)]
    private struct DeviceInterfaceData
    {
        internal uint Size;
        internal Guid InterfaceClassGuid;
        internal uint Flags;
        internal UIntPtr Reserved;
    }

    [StructLayout(LayoutKind.Sequential)]
    private readonly struct DevicePropertyKey
    {
        internal DevicePropertyKey(Guid formatId, uint propertyId)
        {
            FormatId = formatId;
            PropertyId = propertyId;
        }

        internal readonly Guid FormatId;
        internal readonly uint PropertyId;
    }

    [LibraryImport(
        "setupapi.dll",
        EntryPoint = "SetupDiGetClassDevsW",
        SetLastError = true,
        StringMarshalling = StringMarshalling.Utf16)]
    private static partial IntPtr SetupDiGetClassDevs(
        ref Guid classGuid,
        string? enumerator,
        IntPtr parentWindow,
        uint flags);

    [LibraryImport("setupapi.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetupDiEnumDeviceInterfaces(
        IntPtr deviceInfoSet,
        IntPtr deviceInfoData,
        ref Guid interfaceClassGuid,
        uint memberIndex,
        ref DeviceInterfaceData deviceInterfaceData);

    [LibraryImport(
        "setupapi.dll",
        EntryPoint = "SetupDiGetDeviceInterfacePropertyW",
        SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static unsafe partial bool SetupDiGetDeviceInterfaceProperty(
        IntPtr deviceInfoSet,
        ref DeviceInterfaceData deviceInterfaceData,
        in DevicePropertyKey propertyKey,
        out uint propertyType,
        byte* propertyBuffer,
        uint propertyBufferSize,
        out uint requiredSize,
        uint flags);

    [LibraryImport("setupapi.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetupDiDestroyDeviceInfoList(IntPtr deviceInfoSet);
}
