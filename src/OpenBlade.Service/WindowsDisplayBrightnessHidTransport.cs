using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using OpenBlade.Contracts;

namespace OpenBlade.Service;

internal sealed class WindowsDisplayBrightnessHidTransport
    : IDisplayBrightnessHidTransport
{
    internal const string RootDeviceInstanceId =
        @"ROOT\OSSBLADE_OPENBLADE_BRIGHTNESS_HID\0000";
    internal const ushort VendorUsagePage = 0xFF00;
    internal const ushort VendorUsage = 0x0001;
    internal const byte FeatureReportId = 0x02;
    internal const int FeatureReportLength = 3;
    internal static int HidCapabilitiesSize => Marshal.SizeOf<HidCaps>();

    private const uint GenericRead = 0x80000000;
    private const uint GenericWrite = 0x40000000;
    private const uint FileShareRead = 0x00000001;
    private const uint FileShareWrite = 0x00000002;
    private const uint OpenExisting = 3;
    private const uint IoctlHidSetFeature = 0x000B0191;
    private const uint IoctlHidGetFeature = 0x000B0192;
    private const uint DigcfPresent = 0x00000002;
    private const uint DigcfDeviceInterface = 0x00000010;
    private const int ErrorNoMoreItems = 259;
    private const int CrSuccess = 0;
    private static readonly IntPtr InvalidHandleValue = new(-1);
    private readonly object sync = new();
    private readonly Func<(string? Path, DisplayBrightnessHidWriteResult Result)> pathResolver;
    private readonly Func<string, DisplayBrightnessHidUsage, DisplayBrightnessHidWriteResult>
        usageWriter;
    private string? cachedDevicePath;

    public WindowsDisplayBrightnessHidTransport()
        : this(ResolveDevicePath, WriteUsageToPath)
    {
    }

    internal WindowsDisplayBrightnessHidTransport(
        Func<(string? Path, DisplayBrightnessHidWriteResult Result)> pathResolver,
        Func<string, DisplayBrightnessHidUsage, DisplayBrightnessHidWriteResult> usageWriter)
    {
        this.pathResolver = pathResolver ?? throw new ArgumentNullException(nameof(pathResolver));
        this.usageWriter = usageWriter ?? throw new ArgumentNullException(nameof(usageWriter));
    }

    public DisplayBrightnessHidWriteResult SetUsage(DisplayBrightnessHidUsage usage)
    {
        lock (sync)
        {
            try
            {
                return SetUsageCore(usage);
            }
            catch (Exception exception) when (exception is
                Win32Exception or InvalidOperationException or OverflowException)
            {
                return new DisplayBrightnessHidWriteResult(
                    DisplayBrightnessHidWriteStatus.Failed,
                    exception.Message);
            }
        }
    }

    private DisplayBrightnessHidWriteResult SetUsageCore(
        DisplayBrightnessHidUsage usage)
    {
        if (cachedDevicePath is not null)
        {
            DisplayBrightnessHidWriteResult cachedWrite = usageWriter(cachedDevicePath, usage);
            if (cachedWrite.Status != DisplayBrightnessHidWriteStatus.Unavailable)
            {
                return cachedWrite;
            }

            cachedDevicePath = null;
        }

        (string? path, DisplayBrightnessHidWriteResult resolution) = pathResolver();
        if (path is null)
        {
            return resolution;
        }

        DisplayBrightnessHidWriteResult write = usageWriter(path, usage);
        if (write.Status == DisplayBrightnessHidWriteStatus.Applied)
        {
            cachedDevicePath = path;
        }

        return write;
    }

    private static (string? Path, DisplayBrightnessHidWriteResult Result) ResolveDevicePath()
    {
        HidD_GetHidGuid(out Guid hidGuid);
        IntPtr deviceSet = SetupDiGetClassDevs(
            ref hidGuid,
            null,
            IntPtr.Zero,
            DigcfPresent | DigcfDeviceInterface);
        if (deviceSet == InvalidHandleValue)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error());
        }

        bool exactDeviceObserved = false;
        string? lastFailure = null;
        try
        {
            for (uint index = 0; ; index++)
            {
                var interfaceData = DeviceInterfaceData.Create();
                if (!SetupDiEnumDeviceInterfaces(
                        deviceSet,
                        IntPtr.Zero,
                        ref hidGuid,
                        index,
                        ref interfaceData))
                {
                    int error = Marshal.GetLastWin32Error();
                    if (error == ErrorNoMoreItems)
                    {
                        break;
                    }

                    throw new Win32Exception(error);
                }

                var deviceInfo = DeviceInfoData.Create();
                string path;
                try
                {
                    path = GetDevicePath(deviceSet, ref interfaceData, ref deviceInfo);
                }
                catch (Win32Exception exception)
                {
                    lastFailure = exception.Message;
                    continue;
                }

                bool hasExactRootAncestor;
                try
                {
                    hasExactRootAncestor = HasExactRootAncestor(deviceInfo.DeviceInstance);
                }
                catch (InvalidOperationException exception)
                {
                    lastFailure = exception.Message;
                    continue;
                }

                if (!hasExactRootAncestor)
                {
                    continue;
                }

                exactDeviceObserved = true;
                using SafeFileHandle handle = CreateFile(
                    path,
                    GenericRead | GenericWrite,
                    FileShareRead | FileShareWrite,
                    IntPtr.Zero,
                    OpenExisting,
                    0,
                    IntPtr.Zero);
                if (handle.IsInvalid)
                {
                    lastFailure = new Win32Exception(Marshal.GetLastWin32Error()).Message;
                    continue;
                }

                if (!IsVendorControlCollection(handle, out lastFailure))
                {
                    continue;
                }

                return (
                    path,
                    new DisplayBrightnessHidWriteResult(
                        DisplayBrightnessHidWriteStatus.Applied));
            }
        }
        finally
        {
            _ = SetupDiDestroyDeviceInfoList(deviceSet);
        }

        return exactDeviceObserved
            ? (
                null,
                new DisplayBrightnessHidWriteResult(
                    DisplayBrightnessHidWriteStatus.Failed,
                    lastFailure
                        ?? "The exact virtual HID device exposed no valid control collection."))
            : (
                null,
                new DisplayBrightnessHidWriteResult(
                    DisplayBrightnessHidWriteStatus.Unavailable,
                    lastFailure));
    }

    private static DisplayBrightnessHidWriteResult WriteUsageToPath(
        string path,
        DisplayBrightnessHidUsage usage)
    {
        using SafeFileHandle handle = CreateFile(
            path,
            GenericRead | GenericWrite,
            FileShareRead | FileShareWrite,
            IntPtr.Zero,
            OpenExisting,
            0,
            IntPtr.Zero);
        if (handle.IsInvalid)
        {
            return new DisplayBrightnessHidWriteResult(
                DisplayBrightnessHidWriteStatus.Unavailable,
                new Win32Exception(Marshal.GetLastWin32Error()).Message);
        }

        if (!HasExactRootAncestorForPath(path, out string? identityFailure))
        {
            return new DisplayBrightnessHidWriteResult(
                DisplayBrightnessHidWriteStatus.Unavailable,
                identityFailure
                    ?? "The cached HID path no longer belongs to the exact OpenBlade root device.");
        }

        if (!IsVendorControlCollection(handle, out string? failure))
        {
            return new DisplayBrightnessHidWriteResult(
                DisplayBrightnessHidWriteStatus.Unavailable,
                failure ?? "The cached HID path no longer identifies the vendor control collection.");
        }

        byte[] feature = CreateFeatureReport(usage);
        if (!DeviceIoControlSetFeature(
                handle,
                IoctlHidSetFeature,
                feature,
                checked((uint)feature.Length),
                IntPtr.Zero,
                0,
                out _,
                IntPtr.Zero))
        {
            return FailedLastError();
        }

        byte[] readback = [FeatureReportId, 0, 0];
        if (!DeviceIoControlGetFeature(
                handle,
                IoctlHidGetFeature,
                IntPtr.Zero,
                0,
                readback,
                checked((uint)readback.Length),
                out _,
                IntPtr.Zero))
        {
            return FailedLastError();
        }

        if (!readback.AsSpan().SequenceEqual(feature))
        {
            return new DisplayBrightnessHidWriteResult(
                DisplayBrightnessHidWriteStatus.Failed,
                "The virtual HID feature-report readback did not match the requested state.");
        }

        return new DisplayBrightnessHidWriteResult(DisplayBrightnessHidWriteStatus.Applied);
    }

    private static bool HasExactRootAncestorForPath(string path, out string? failure)
    {
        failure = null;
        IntPtr deviceSet = SetupDiCreateDeviceInfoList(IntPtr.Zero, IntPtr.Zero);
        if (deviceSet == InvalidHandleValue)
        {
            failure = new Win32Exception(Marshal.GetLastWin32Error()).Message;
            return false;
        }

        var interfaceData = DeviceInterfaceData.Create();
        bool interfaceOpened = false;
        try
        {
            if (!SetupDiOpenDeviceInterface(
                    deviceSet,
                    path,
                    0,
                    ref interfaceData))
            {
                failure = new Win32Exception(Marshal.GetLastWin32Error()).Message;
                return false;
            }

            interfaceOpened = true;
            var deviceInfo = DeviceInfoData.Create();
            string currentPath;
            try
            {
                currentPath = GetDevicePath(deviceSet, ref interfaceData, ref deviceInfo);
            }
            catch (Win32Exception exception)
            {
                failure = exception.Message;
                return false;
            }

            if (!string.Equals(path, currentPath, StringComparison.OrdinalIgnoreCase))
            {
                failure = "The cached HID path resolved to a different device interface.";
                return false;
            }

            try
            {
                if (HasExactRootAncestor(deviceInfo.DeviceInstance))
                {
                    return true;
                }

                failure = "The cached HID path no longer belongs to the exact OpenBlade root device.";
                return false;
            }
            catch (InvalidOperationException exception)
            {
                failure = exception.Message;
                return false;
            }
        }
        finally
        {
            if (interfaceOpened)
            {
                _ = SetupDiDeleteDeviceInterfaceData(deviceSet, ref interfaceData);
            }

            _ = SetupDiDestroyDeviceInfoList(deviceSet);
        }
    }

    internal static byte[] CreateFeatureReport(DisplayBrightnessHidUsage usage)
    {
        ushort value = checked((ushort)usage);
        return [FeatureReportId, (byte)value, (byte)(value >> 8)];
    }

    private static bool IsVendorControlCollection(
        SafeFileHandle handle,
        out string? failure)
    {
        failure = null;
        if (!HidD_GetPreparsedData(handle, out IntPtr preparsedData))
        {
            failure = new Win32Exception(Marshal.GetLastWin32Error()).Message;
            return false;
        }

        try
        {
            var caps = new HidCaps();
            int status = HidP_GetCaps(preparsedData, ref caps);
            if (status < 0)
            {
                failure = $"HidP_GetCaps failed with NTSTATUS 0x{unchecked((uint)status):X8}.";
                return false;
            }

            return caps.UsagePage == VendorUsagePage
                && caps.Usage == VendorUsage
                && caps.FeatureReportByteLength == FeatureReportLength;
        }
        finally
        {
            _ = HidD_FreePreparsedData(preparsedData);
        }
    }

    private static bool HasExactRootAncestor(uint deviceInstance)
    {
        uint current = deviceInstance;
        for (int depth = 0; depth < 16; depth++)
        {
            if (string.Equals(
                    GetDeviceInstanceId(current),
                    RootDeviceInstanceId,
                    StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            if (CM_Get_Parent(out uint parent, current, 0) != CrSuccess)
            {
                break;
            }

            current = parent;
        }

        return false;
    }

    private static string GetDeviceInstanceId(uint deviceInstance)
    {
        int status = CM_Get_Device_ID_Size(out uint characterCount, deviceInstance, 0);
        if (status != CrSuccess)
        {
            throw new InvalidOperationException(
                $"CM_Get_Device_ID_Size failed with CONFIGRET 0x{status:X8}.");
        }

        char[] buffer = new char[checked((int)characterCount + 1)];
        status = CM_Get_Device_ID(deviceInstance, buffer, checked((uint)buffer.Length), 0);
        if (status != CrSuccess)
        {
            throw new InvalidOperationException(
                $"CM_Get_Device_ID failed with CONFIGRET 0x{status:X8}.");
        }

        int terminator = Array.IndexOf(buffer, '\0');
        return new string(buffer, 0, terminator < 0 ? buffer.Length : terminator);
    }

    private static string GetDevicePath(
        IntPtr deviceSet,
        ref DeviceInterfaceData interfaceData,
        ref DeviceInfoData deviceInfo)
    {
        _ = SetupDiGetDeviceInterfaceDetail(
            deviceSet,
            ref interfaceData,
            IntPtr.Zero,
            0,
            out uint requiredBytes,
            IntPtr.Zero);
        int firstError = Marshal.GetLastWin32Error();
        if (requiredBytes == 0)
        {
            throw new Win32Exception(firstError);
        }

        IntPtr detail = Marshal.AllocHGlobal(checked((int)requiredBytes));
        try
        {
            Marshal.WriteInt32(detail, IntPtr.Size == 8 ? 8 : 6);
            if (!SetupDiGetDeviceInterfaceDetail(
                    deviceSet,
                    ref interfaceData,
                    detail,
                    requiredBytes,
                    out _,
                    ref deviceInfo))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error());
            }

            return Marshal.PtrToStringUni(IntPtr.Add(detail, 4))
                ?? throw new InvalidOperationException("The HID device path is unavailable.");
        }
        finally
        {
            Marshal.FreeHGlobal(detail);
        }
    }

    private static DisplayBrightnessHidWriteResult FailedLastError() => new(
        DisplayBrightnessHidWriteStatus.Failed,
        new Win32Exception(Marshal.GetLastWin32Error()).Message);

    [StructLayout(LayoutKind.Sequential)]
    private struct DeviceInterfaceData
    {
        internal uint Size;
        internal Guid InterfaceClassGuid;
        internal uint Flags;
        internal UIntPtr Reserved;

        internal static DeviceInterfaceData Create() => new()
        {
            Size = checked((uint)Marshal.SizeOf<DeviceInterfaceData>()),
        };
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DeviceInfoData
    {
        internal uint Size;
        internal Guid ClassGuid;
        internal uint DeviceInstance;
        internal UIntPtr Reserved;

        internal static DeviceInfoData Create() => new()
        {
            Size = checked((uint)Marshal.SizeOf<DeviceInfoData>()),
        };
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct HidCaps
    {
        internal ushort Usage;
        internal ushort UsagePage;
        internal ushort InputReportByteLength;
        internal ushort OutputReportByteLength;
        internal ushort FeatureReportByteLength;
        internal ushort Reserved0;
        internal ushort Reserved1;
        internal ushort Reserved2;
        internal ushort Reserved3;
        internal ushort Reserved4;
        internal ushort Reserved5;
        internal ushort Reserved6;
        internal ushort Reserved7;
        internal ushort Reserved8;
        internal ushort Reserved9;
        internal ushort Reserved10;
        internal ushort Reserved11;
        internal ushort Reserved12;
        internal ushort Reserved13;
        internal ushort Reserved14;
        internal ushort Reserved15;
        internal ushort Reserved16;
        internal ushort NumberLinkCollectionNodes;
        internal ushort NumberInputButtonCaps;
        internal ushort NumberInputValueCaps;
        internal ushort NumberInputDataIndices;
        internal ushort NumberOutputButtonCaps;
        internal ushort NumberOutputValueCaps;
        internal ushort NumberOutputDataIndices;
        internal ushort NumberFeatureButtonCaps;
        internal ushort NumberFeatureValueCaps;
        internal ushort NumberFeatureDataIndices;
    }

    [DllImport("hid.dll")]
    private static extern void HidD_GetHidGuid(out Guid hidGuid);

    [DllImport("kernel32.dll", EntryPoint = "DeviceIoControl", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeviceIoControlSetFeature(
        SafeFileHandle device,
        uint controlCode,
        byte[] inputBuffer,
        uint inputBufferLength,
        IntPtr outputBuffer,
        uint outputBufferLength,
        out uint bytesReturned,
        IntPtr overlapped);

    [DllImport("kernel32.dll", EntryPoint = "DeviceIoControl", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeviceIoControlGetFeature(
        SafeFileHandle device,
        uint controlCode,
        IntPtr inputBuffer,
        uint inputBufferLength,
        [Out] byte[] outputBuffer,
        uint outputBufferLength,
        out uint bytesReturned,
        IntPtr overlapped);

    [DllImport("hid.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool HidD_GetPreparsedData(
        SafeFileHandle device,
        out IntPtr preparsedData);

    [DllImport("hid.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool HidD_FreePreparsedData(IntPtr preparsedData);

    [DllImport("hid.dll")]
    private static extern int HidP_GetCaps(IntPtr preparsedData, ref HidCaps capabilities);

    [DllImport("setupapi.dll", EntryPoint = "SetupDiGetClassDevsW", SetLastError = true,
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

    [DllImport("setupapi.dll", EntryPoint = "SetupDiGetDeviceInterfaceDetailW",
        SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetupDiGetDeviceInterfaceDetail(
        IntPtr deviceInfoSet,
        ref DeviceInterfaceData deviceInterfaceData,
        IntPtr detailData,
        uint detailDataSize,
        out uint requiredSize,
        IntPtr deviceInfoData);

    [DllImport("setupapi.dll", EntryPoint = "SetupDiGetDeviceInterfaceDetailW",
        SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetupDiGetDeviceInterfaceDetail(
        IntPtr deviceInfoSet,
        ref DeviceInterfaceData deviceInterfaceData,
        IntPtr detailData,
        uint detailDataSize,
        out uint requiredSize,
        ref DeviceInfoData deviceInfoData);

    [DllImport("setupapi.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetupDiDestroyDeviceInfoList(IntPtr deviceInfoSet);

    [DllImport("setupapi.dll", SetLastError = true)]
    private static extern IntPtr SetupDiCreateDeviceInfoList(
        IntPtr classGuid,
        IntPtr parentWindow);

    [DllImport("setupapi.dll", EntryPoint = "SetupDiOpenDeviceInterfaceW",
        CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetupDiOpenDeviceInterface(
        IntPtr deviceInfoSet,
        string devicePath,
        uint openFlags,
        ref DeviceInterfaceData deviceInterfaceData);

    [DllImport("setupapi.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetupDiDeleteDeviceInterfaceData(
        IntPtr deviceInfoSet,
        ref DeviceInterfaceData deviceInterfaceData);

    [DllImport("kernel32.dll", EntryPoint = "CreateFileW", SetLastError = true,
        CharSet = CharSet.Unicode)]
    private static extern SafeFileHandle CreateFile(
        string fileName,
        uint desiredAccess,
        uint shareMode,
        IntPtr securityAttributes,
        uint creationDisposition,
        uint flagsAndAttributes,
        IntPtr templateFile);

    [DllImport("cfgmgr32.dll")]
    private static extern int CM_Get_Parent(
        out uint parentDeviceInstance,
        uint deviceInstance,
        uint flags);

    [DllImport("cfgmgr32.dll")]
    private static extern int CM_Get_Device_ID_Size(
        out uint length,
        uint deviceInstance,
        uint flags);

    [DllImport("cfgmgr32.dll", EntryPoint = "CM_Get_Device_IDW",
        CharSet = CharSet.Unicode)]
    private static extern int CM_Get_Device_ID(
        uint deviceInstance,
        [Out] char[] buffer,
        uint bufferLength,
        uint flags);
}
