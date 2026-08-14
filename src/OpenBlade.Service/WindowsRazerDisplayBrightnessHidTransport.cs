using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using Microsoft.Win32.SafeHandles;
using OpenBlade.Contracts;

namespace OpenBlade.Service;

internal sealed class WindowsRazerDisplayBrightnessHidTransport
    : IDisplayBrightnessHidTransport
{
    // This is one exact, hardware-validated RZ09-0581 driver tuple. Razer
    // updates or another Blade model must be admitted independently.
    internal static readonly Guid ControlInterfaceGuid = new(
        "e3be005d-d130-4910-88ff-09ae02f680e9");
    internal const string ControlInstanceIdPrefix =
        @"RZCONTROL\VID_1532&PID_02E0&MI_00\";
    internal const string PhysicalInterfaceAncestorPrefix =
        @"USB\VID_1532&PID_02E0&MI_02\";
    internal const string ControlServiceName = "RzCommon";
    internal const uint SubmitInputIoctl = 0x88883020;
    internal const uint ConsumerControlInputType = 10;
    internal const int ControlBufferLength = 32;
    internal const int UsageOffset = 8;
    internal const string RzCommonSha256 =
        "64B72EF9CE409BB17D31096C119B68C4A6A02AB35B7535D763157A4E1A587E59";
    internal const string RzDeviceSha256 =
        "BC1666048D81A8DBA6BBFFDBDC86C2119632FD68EF2BB6D5D417FE3B84744787";

    private const uint GenericWrite = 0x40000000;
    private const uint FileShareRead = 0x00000001;
    private const uint FileShareWrite = 0x00000002;
    private const uint OpenExisting = 3;
    private const uint DigcfPresent = 0x00000002;
    private const uint DigcfDeviceInterface = 0x00000010;
    private const uint SpdrpService = 0x00000004;
    private const int ErrorFileNotFound = 2;
    private const int ErrorPathNotFound = 3;
    private const int ErrorInvalidHandle = 6;
    private const int ErrorInsufficientBuffer = 122;
    private const int ErrorNoMoreItems = 259;
    private const int ErrorDeviceNotConnected = 1167;
    private const int CrSuccess = 0;
    private static readonly IntPtr InvalidHandleValue = new(-1);

    private readonly object sync = new();
    private readonly Func<(string? Path, DisplayBrightnessHidWriteResult Result)> pathResolver;
    private readonly Func<string, DisplayBrightnessHidUsage, DisplayBrightnessHidWriteResult>
        usageWriter;
    private string? cachedDevicePath;

    public WindowsRazerDisplayBrightnessHidTransport()
        : this(ResolveDevicePath, WriteUsageToPath)
    {
    }

    internal WindowsRazerDisplayBrightnessHidTransport(
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
                Win32Exception or InvalidOperationException or IOException
                or UnauthorizedAccessException or OverflowException)
            {
                return new DisplayBrightnessHidWriteResult(
                    DisplayBrightnessHidWriteStatus.Failed,
                    exception.Message);
            }
        }
    }

    private DisplayBrightnessHidWriteResult SetUsageCore(DisplayBrightnessHidUsage usage)
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

    internal static byte[] CreateControlBuffer(DisplayBrightnessHidUsage usage)
    {
        // RzCommon type 10 accepts a 32-byte METHOD_BUFFERED request and maps
        // the little-endian value at +8 to Consumer Control report 1. Keep the
        // generic command structure private: only the three typed usages in
        // DisplayBrightnessHidUsage can reach this transport.
        ushort value = checked((ushort)usage);
        byte[] buffer = new byte[ControlBufferLength];
        BitConverter.TryWriteBytes(buffer.AsSpan(sizeof(uint)), ConsumerControlInputType);
        BitConverter.TryWriteBytes(buffer.AsSpan(UsageOffset), value);
        return buffer;
    }

    private static (string? Path, DisplayBrightnessHidWriteResult Result) ResolveDevicePath()
    {
        if (!IsDriverBaselineAdmitted(out string? baselineFailure))
        {
            return (
                null,
                new DisplayBrightnessHidWriteResult(
                    DisplayBrightnessHidWriteStatus.Unavailable,
                    baselineFailure));
        }

        Guid interfaceGuid = ControlInterfaceGuid;
        IntPtr deviceSet = SetupDiGetClassDevs(
            ref interfaceGuid,
            null,
            IntPtr.Zero,
            DigcfPresent | DigcfDeviceInterface);
        if (deviceSet == InvalidHandleValue)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error());
        }

        bool exactDeviceObserved = false;
        string? exactDeviceFailure = null;
        try
        {
            for (uint index = 0; ; index++)
            {
                var interfaceData = DeviceInterfaceData.Create();
                if (!SetupDiEnumDeviceInterfaces(
                        deviceSet,
                        IntPtr.Zero,
                        ref interfaceGuid,
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
                catch (Win32Exception)
                {
                    continue;
                }

                if (!IsExactControlDevice(deviceSet, ref deviceInfo, out string? failure))
                {
                    if (IsExactControlInstance(deviceInfo.DeviceInstance))
                    {
                        exactDeviceObserved = true;
                        exactDeviceFailure = failure;
                    }

                    continue;
                }

                exactDeviceObserved = true;
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
                    exactDeviceFailure
                        ?? "The exact Razer control interface failed identity validation."))
            : (
                null,
                new DisplayBrightnessHidWriteResult(
                    DisplayBrightnessHidWriteStatus.Unavailable));
    }

    private static DisplayBrightnessHidWriteResult WriteUsageToPath(
        string path,
        DisplayBrightnessHidUsage usage)
    {
        using SafeFileHandle handle = CreateFile(
            path,
            GenericWrite,
            FileShareRead | FileShareWrite,
            IntPtr.Zero,
            OpenExisting,
            0,
            IntPtr.Zero);
        if (handle.IsInvalid)
        {
            int error = Marshal.GetLastWin32Error();
            return new DisplayBrightnessHidWriteResult(
                IsUnavailableOpenError(error)
                    ? DisplayBrightnessHidWriteStatus.Unavailable
                    : DisplayBrightnessHidWriteStatus.Failed,
                new Win32Exception(error).Message);
        }

        if (!ValidateExactPath(path, out string? identityFailure))
        {
            return new DisplayBrightnessHidWriteResult(
                DisplayBrightnessHidWriteStatus.Unavailable,
                identityFailure
                    ?? "The cached Razer control path no longer belongs to the admitted device.");
        }

        byte[] buffer = CreateControlBuffer(usage);
        if (!DeviceIoControl(
                handle,
                SubmitInputIoctl,
                buffer,
                checked((uint)buffer.Length),
                IntPtr.Zero,
                0,
                out _,
                IntPtr.Zero))
        {
            return new DisplayBrightnessHidWriteResult(
                DisplayBrightnessHidWriteStatus.Failed,
                new Win32Exception(Marshal.GetLastWin32Error()).Message);
        }

        return new DisplayBrightnessHidWriteResult(DisplayBrightnessHidWriteStatus.Applied);
    }

    private static bool ValidateExactPath(string path, out string? failure)
    {
        failure = null;
        Guid interfaceGuid = ControlInterfaceGuid;
        IntPtr deviceSet = SetupDiGetClassDevs(
            ref interfaceGuid,
            null,
            IntPtr.Zero,
            DigcfPresent | DigcfDeviceInterface);
        if (deviceSet == InvalidHandleValue)
        {
            failure = new Win32Exception(Marshal.GetLastWin32Error()).Message;
            return false;
        }

        try
        {
            for (uint index = 0; ; index++)
            {
                var interfaceData = DeviceInterfaceData.Create();
                if (!SetupDiEnumDeviceInterfaces(
                        deviceSet,
                        IntPtr.Zero,
                        ref interfaceGuid,
                        index,
                        ref interfaceData))
                {
                    int error = Marshal.GetLastWin32Error();
                    if (error == ErrorNoMoreItems)
                    {
                        break;
                    }

                    failure = new Win32Exception(error).Message;
                    return false;
                }

                var deviceInfo = DeviceInfoData.Create();
                string currentPath;
                try
                {
                    currentPath = GetDevicePath(deviceSet, ref interfaceData, ref deviceInfo);
                }
                catch (Win32Exception)
                {
                    continue;
                }

                if (!string.Equals(path, currentPath, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                return IsExactControlDevice(deviceSet, ref deviceInfo, out failure);
            }

            failure = "The cached Razer interface path is no longer present.";
            return false;
        }
        finally
        {
            _ = SetupDiDestroyDeviceInfoList(deviceSet);
        }
    }

    private static bool IsExactControlDevice(
        IntPtr deviceSet,
        ref DeviceInfoData deviceInfo,
        out string? failure)
    {
        failure = null;
        string instanceId;
        try
        {
            instanceId = GetDeviceInstanceId(deviceInfo.DeviceInstance);
        }
        catch (InvalidOperationException exception)
        {
            failure = exception.Message;
            return false;
        }

        if (!instanceId.StartsWith(ControlInstanceIdPrefix, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        string service;
        try
        {
            service = GetDeviceRegistryString(deviceSet, ref deviceInfo, SpdrpService);
        }
        catch (Win32Exception exception)
        {
            failure = exception.Message;
            return false;
        }

        if (!string.Equals(service, ControlServiceName, StringComparison.OrdinalIgnoreCase))
        {
            failure = $"The exact Razer control device uses unexpected service '{service}'.";
            return false;
        }

        try
        {
            if (HasExactPhysicalAncestor(deviceInfo.DeviceInstance))
            {
                return true;
            }

            failure = "The Razer control interface has no admitted physical-device ancestor.";
            return false;
        }
        catch (InvalidOperationException exception)
        {
            failure = exception.Message;
            return false;
        }
    }

    private static bool IsExactControlInstance(uint deviceInstance)
    {
        try
        {
            return GetDeviceInstanceId(deviceInstance).StartsWith(
                ControlInstanceIdPrefix,
                StringComparison.OrdinalIgnoreCase);
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    private static bool HasExactPhysicalAncestor(uint deviceInstance)
    {
        uint current = deviceInstance;
        for (int depth = 0; depth < 16; depth++)
        {
            if (GetDeviceInstanceId(current).StartsWith(
                    PhysicalInterfaceAncestorPrefix,
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

    private static bool IsDriverBaselineAdmitted(out string? failure)
    {
        string drivers = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.System),
            "drivers");
        return HasExpectedHash(
                Path.Combine(drivers, "RzCommon.sys"),
                RzCommonSha256,
                out failure)
            && HasExpectedHash(
                Path.Combine(drivers, "RzDev_02e0.sys"),
                RzDeviceSha256,
                out failure);
    }

    private static bool HasExpectedHash(
        string path,
        string expectedHash,
        out string? failure)
    {
        failure = null;
        if (!File.Exists(path))
        {
            failure = $"The admitted Razer driver file is missing: {Path.GetFileName(path)}.";
            return false;
        }

        try
        {
            using FileStream stream = File.OpenRead(path);
            byte[] actual = SHA256.HashData(stream);
            if (CryptographicOperations.FixedTimeEquals(
                    actual,
                    Convert.FromHexString(expectedHash)))
            {
                return true;
            }

            failure = $"The installed {Path.GetFileName(path)} build is not admitted.";
            return false;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            failure = exception.Message;
            return false;
        }
    }

    private static bool IsUnavailableOpenError(int error) => error is
        ErrorFileNotFound or ErrorPathNotFound or ErrorInvalidHandle or ErrorDeviceNotConnected;

    private static string GetDeviceRegistryString(
        IntPtr deviceSet,
        ref DeviceInfoData deviceInfo,
        uint property)
    {
        _ = SetupDiGetDeviceRegistryProperty(
            deviceSet,
            ref deviceInfo,
            property,
            out _,
            null,
            0,
            out uint requiredBytes);
        int firstError = Marshal.GetLastWin32Error();
        if (requiredBytes == 0 || firstError != ErrorInsufficientBuffer)
        {
            throw new Win32Exception(firstError);
        }

        byte[] buffer = new byte[requiredBytes];
        if (!SetupDiGetDeviceRegistryProperty(
                deviceSet,
                ref deviceInfo,
                property,
                out _,
                buffer,
                checked((uint)buffer.Length),
                out _))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error());
        }

        return System.Text.Encoding.Unicode.GetString(buffer).TrimEnd('\0');
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
                ?? throw new InvalidOperationException(
                    "The Razer control device path is unavailable.");
        }
        finally
        {
            Marshal.FreeHGlobal(detail);
        }
    }

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

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
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

    [DllImport("setupapi.dll", EntryPoint = "SetupDiGetDeviceInterfaceDetailW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetupDiGetDeviceInterfaceDetail(
        IntPtr deviceInfoSet,
        ref DeviceInterfaceData deviceInterfaceData,
        IntPtr deviceInterfaceDetailData,
        uint deviceInterfaceDetailDataSize,
        out uint requiredSize,
        IntPtr deviceInfoData);

    [DllImport("setupapi.dll", EntryPoint = "SetupDiGetDeviceInterfaceDetailW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetupDiGetDeviceInterfaceDetail(
        IntPtr deviceInfoSet,
        ref DeviceInterfaceData deviceInterfaceData,
        IntPtr deviceInterfaceDetailData,
        uint deviceInterfaceDetailDataSize,
        out uint requiredSize,
        ref DeviceInfoData deviceInfoData);

    [DllImport("setupapi.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetupDiDestroyDeviceInfoList(IntPtr deviceInfoSet);

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetupDiGetDeviceRegistryProperty(
        IntPtr deviceInfoSet,
        ref DeviceInfoData deviceInfoData,
        uint property,
        out uint propertyRegDataType,
        byte[]? propertyBuffer,
        uint propertyBufferSize,
        out uint requiredSize);

    [DllImport("cfgmgr32.dll")]
    private static extern int CM_Get_Parent(
        out uint parentDeviceInstance,
        uint deviceInstance,
        uint flags);

    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
    private static extern int CM_Get_Device_ID_Size(
        out uint length,
        uint deviceInstance,
        uint flags);

    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
    private static extern int CM_Get_Device_ID(
        uint deviceInstance,
        [Out] char[] buffer,
        uint bufferLength,
        uint flags);

    [DllImport("kernel32.dll", EntryPoint = "CreateFileW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFile(
        string fileName,
        uint desiredAccess,
        uint shareMode,
        IntPtr securityAttributes,
        uint creationDisposition,
        uint flagsAndAttributes,
        IntPtr templateFile);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeviceIoControl(
        SafeFileHandle device,
        uint controlCode,
        byte[] inputBuffer,
        uint inputBufferLength,
        IntPtr outputBuffer,
        uint outputBufferLength,
        out uint bytesReturned,
        IntPtr overlapped);
}
