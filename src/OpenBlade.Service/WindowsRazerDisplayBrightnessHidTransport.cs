using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using Microsoft.Win32.SafeHandles;
using OpenBlade.Contracts;

namespace OpenBlade.Service;

/// <summary>
/// One exact, hardware-validated Razer driver tuple for a single Blade model.
/// Every device identity, ancestor, service, and driver hash belongs to the
/// same USB product ID, so one model's stack can never admit another's.
/// </summary>
internal sealed record RazerDisplayBrightnessDriverStack(
    string ProductId,
    string VirtualConsumerServiceName,
    string DeviceDriverSha256)
{
    internal string ControlInstanceIdPrefix =>
        $@"RZCONTROL\VID_1532&PID_{ProductId}&MI_00\";

    internal string VirtualConsumerInstanceIdPrefix =>
        $@"RZVIRTUAL\VID_1532&PID_{ProductId}&MI_00&COL03\";

    internal string PhysicalInterfaceAncestorPrefix =>
        $@"USB\VID_1532&PID_{ProductId}&MI_02\";

    internal string VirtualConsumerAncestorPrefix =>
        $@"USB\VID_1532&PID_{ProductId}&MI_01\";

    internal string DeviceDriverFileName => VirtualConsumerServiceName + ".sys";
}

internal sealed class WindowsRazerDisplayBrightnessHidTransport
    : IDisplayBrightnessHidTransport
{
    // Razer updates or another Blade model must be admitted independently.
    internal static readonly RazerDisplayBrightnessDriverStack Rz09_0581Stack = new(
        "02E0",
        "RzDev_02e0",
        "BC1666048D81A8DBA6BBFFDBDC86C2119632FD68EF2BB6D5D417FE3B84744787");
    internal static readonly RazerDisplayBrightnessDriverStack Rz09_0528Stack = new(
        "02C6",
        "RzDev_02c6",
        "307940C7C5AB537F19F0A33ADA592D2D6DE5ECADB13D748019A8F0E13816BA82");
    internal static readonly IReadOnlyList<RazerDisplayBrightnessDriverStack>
        AdmittedStacks = [Rz09_0581Stack, Rz09_0528Stack];
    internal static readonly Guid ControlInterfaceGuid = new(
        "e3be005d-d130-4910-88ff-09ae02f680e9");
    internal static readonly Guid HidClassGuid = new(
        "745a17a0-74d3-11d0-b6fe-00a0c90f57da");
    internal const string ControlServiceName = "RzCommon";
    internal const uint SubmitInputIoctl = 0x88883020;
    internal const uint ConsumerControlInputType = 10;
    internal const int ControlBufferLength = 32;
    internal const int UsageOffset = 8;
    internal const string RzCommonSha256 =
        "64B72EF9CE409BB17D31096C119B68C4A6A02AB35B7535D763157A4E1A587E59";

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

    public DisplayBrightnessHidAvailability GetAvailability()
    {
        lock (sync)
        {
            try
            {
                (string? path, DisplayBrightnessHidWriteResult resolution) = pathResolver();
                if (path is not null)
                {
                    return DisplayBrightnessHidAvailability.Available;
                }

                return resolution.Status == DisplayBrightnessHidWriteStatus.Unavailable
                    ? DisplayBrightnessHidAvailability.Unavailable
                    : DisplayBrightnessHidAvailability.Unknown;
            }
            catch (Exception exception) when (exception is
                Win32Exception or InvalidOperationException or IOException
                or UnauthorizedAccessException or OverflowException)
            {
                return DisplayBrightnessHidAvailability.Unknown;
            }
        }
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
        // Each stack checks only its own exact product ID, so at most one can
        // match the laptop. An identity failure outranks a simply absent stack.
        DisplayBrightnessHidWriteResult? unresolved = null;
        foreach (RazerDisplayBrightnessDriverStack stack in AdmittedStacks)
        {
            (string? path, DisplayBrightnessHidWriteResult result) = ResolveDevicePath(stack);
            if (path is not null)
            {
                return (path, result);
            }

            if (unresolved is null
                || result.Status == DisplayBrightnessHidWriteStatus.Failed
                && unresolved.Value.Status != DisplayBrightnessHidWriteStatus.Failed)
            {
                unresolved = result;
            }
        }

        return (
            null,
            unresolved ?? new DisplayBrightnessHidWriteResult(
                DisplayBrightnessHidWriteStatus.Unavailable));
    }

    private static (string? Path, DisplayBrightnessHidWriteResult Result) ResolveDevicePath(
        RazerDisplayBrightnessDriverStack stack)
    {
        DisplayBrightnessHidWriteStatus baselineStatus = GetDriverBaselineStatus(
            stack,
            out string? baselineFailure);
        if (baselineStatus != DisplayBrightnessHidWriteStatus.Applied)
        {
            return (
                null,
                new DisplayBrightnessHidWriteResult(
                    baselineStatus,
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
        string? inspectionFailure = null;
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
                catch (Win32Exception exception)
                {
                    inspectionFailure = exception.Message;
                    continue;
                }

                if (!IsExactControlDevice(deviceSet, ref deviceInfo, stack, out string? failure))
                {
                    if (failure is not null)
                    {
                        inspectionFailure = failure;
                    }

                    if (IsExactControlInstance(deviceInfo.DeviceInstance, stack))
                    {
                        exactDeviceObserved = true;
                        exactDeviceFailure = failure;
                    }

                    continue;
                }

                exactDeviceObserved = true;
                DisplayBrightnessHidWriteResult virtualConsumer =
                    ValidateVirtualConsumerDevice(stack);
                if (virtualConsumer.Status != DisplayBrightnessHidWriteStatus.Applied)
                {
                    return (null, virtualConsumer);
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
                    exactDeviceFailure
                        ?? "The exact Razer control interface failed identity validation."))
            : (
                null,
                new DisplayBrightnessHidWriteResult(
                    inspectionFailure is null
                        ? DisplayBrightnessHidWriteStatus.Unavailable
                        : DisplayBrightnessHidWriteStatus.Failed,
                    inspectionFailure));
    }

    private static DisplayBrightnessHidWriteResult ValidateVirtualConsumerDevice(
        RazerDisplayBrightnessDriverStack stack)
    {
        Guid hidClassGuid = HidClassGuid;
        IntPtr deviceSet = SetupDiGetClassDevs(
            ref hidClassGuid,
            null,
            IntPtr.Zero,
            DigcfPresent);
        if (deviceSet == InvalidHandleValue)
        {
            return new DisplayBrightnessHidWriteResult(
                DisplayBrightnessHidWriteStatus.Failed,
                new Win32Exception(Marshal.GetLastWin32Error()).Message);
        }

        bool exactDeviceObserved = false;
        string? inspectionFailure = null;
        try
        {
            for (uint index = 0; ; index++)
            {
                var deviceInfo = DeviceInfoData.Create();
                if (!SetupDiEnumDeviceInfo(deviceSet, index, ref deviceInfo))
                {
                    int error = Marshal.GetLastWin32Error();
                    if (error == ErrorNoMoreItems)
                    {
                        break;
                    }

                    return new DisplayBrightnessHidWriteResult(
                        DisplayBrightnessHidWriteStatus.Failed,
                        new Win32Exception(error).Message);
                }

                string instanceId;
                try
                {
                    instanceId = GetDeviceInstanceId(deviceInfo.DeviceInstance);
                }
                catch (InvalidOperationException exception)
                {
                    inspectionFailure = exception.Message;
                    continue;
                }

                if (!instanceId.StartsWith(
                        stack.VirtualConsumerInstanceIdPrefix,
                        StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                exactDeviceObserved = true;
                try
                {
                    if (string.Equals(
                            GetDeviceRegistryString(
                                deviceSet,
                                ref deviceInfo,
                                SpdrpService),
                            stack.VirtualConsumerServiceName,
                            StringComparison.OrdinalIgnoreCase)
                        && HasExactAncestor(
                            deviceInfo.DeviceInstance,
                            stack.VirtualConsumerAncestorPrefix))
                    {
                        return new DisplayBrightnessHidWriteResult(
                            DisplayBrightnessHidWriteStatus.Applied);
                    }
                }
                catch (Exception exception) when (exception is
                    Win32Exception or InvalidOperationException)
                {
                    inspectionFailure = exception.Message;
                }
            }
        }
        finally
        {
            _ = SetupDiDestroyDeviceInfoList(deviceSet);
        }

        return new DisplayBrightnessHidWriteResult(
            exactDeviceObserved || inspectionFailure is not null
                ? DisplayBrightnessHidWriteStatus.Failed
                : DisplayBrightnessHidWriteStatus.Unavailable,
            inspectionFailure ?? (exactDeviceObserved
                ? "The exact Razer virtual Consumer Control device failed identity validation."
                : null));
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

        if (!ValidateExactPath(path, out RazerDisplayBrightnessDriverStack? stack, out string? identityFailure)
            || stack is null)
        {
            return new DisplayBrightnessHidWriteResult(
                DisplayBrightnessHidWriteStatus.Unavailable,
                identityFailure
                    ?? "The cached Razer control path no longer belongs to the admitted device.");
        }

        DisplayBrightnessHidWriteResult virtualConsumer = ValidateVirtualConsumerDevice(stack);
        if (virtualConsumer.Status != DisplayBrightnessHidWriteStatus.Applied)
        {
            return virtualConsumer;
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

    private static bool ValidateExactPath(
        string path,
        out RazerDisplayBrightnessDriverStack? stack,
        out string? failure)
    {
        stack = null;
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

                foreach (RazerDisplayBrightnessDriverStack candidate in AdmittedStacks)
                {
                    if (IsExactControlDevice(deviceSet, ref deviceInfo, candidate, out failure))
                    {
                        stack = candidate;
                        return true;
                    }

                    if (failure is not null)
                    {
                        return false;
                    }
                }

                failure = "The cached Razer control path does not belong to an admitted device.";
                return false;
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
        RazerDisplayBrightnessDriverStack stack,
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

        if (!instanceId.StartsWith(stack.ControlInstanceIdPrefix, StringComparison.OrdinalIgnoreCase))
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
            if (HasExactAncestor(
                    deviceInfo.DeviceInstance,
                    stack.PhysicalInterfaceAncestorPrefix))
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

    private static bool IsExactControlInstance(
        uint deviceInstance,
        RazerDisplayBrightnessDriverStack stack)
    {
        try
        {
            return GetDeviceInstanceId(deviceInstance).StartsWith(
                stack.ControlInstanceIdPrefix,
                StringComparison.OrdinalIgnoreCase);
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    private static bool HasExactAncestor(
        uint deviceInstance,
        string expectedPrefix)
    {
        uint current = deviceInstance;
        for (int depth = 0; depth < 16; depth++)
        {
            if (GetDeviceInstanceId(current).StartsWith(
                    expectedPrefix,
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

    private static DisplayBrightnessHidWriteStatus GetDriverBaselineStatus(
        RazerDisplayBrightnessDriverStack stack,
        out string? failure)
    {
        string drivers = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.System),
            "drivers");
        DisplayBrightnessHidWriteStatus common = GetExpectedHashStatus(
            Path.Combine(drivers, "RzCommon.sys"),
            RzCommonSha256,
            out failure);
        return common == DisplayBrightnessHidWriteStatus.Applied
            ? GetExpectedHashStatus(
                Path.Combine(drivers, stack.DeviceDriverFileName),
                stack.DeviceDriverSha256,
                out failure)
            : common;
    }

    private static DisplayBrightnessHidWriteStatus GetExpectedHashStatus(
        string path,
        string expectedHash,
        out string? failure)
    {
        failure = null;
        if (!File.Exists(path))
        {
            failure = $"The admitted Razer driver file is missing: {Path.GetFileName(path)}.";
            return DisplayBrightnessHidWriteStatus.Unavailable;
        }

        try
        {
            using FileStream stream = File.OpenRead(path);
            byte[] actual = SHA256.HashData(stream);
            if (CryptographicOperations.FixedTimeEquals(
                    actual,
                    Convert.FromHexString(expectedHash)))
            {
                return DisplayBrightnessHidWriteStatus.Applied;
            }

            failure = $"The installed {Path.GetFileName(path)} build is not admitted.";
            return DisplayBrightnessHidWriteStatus.Unavailable;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            failure = exception.Message;
            return DisplayBrightnessHidWriteStatus.Failed;
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

    [DllImport("setupapi.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetupDiEnumDeviceInfo(
        IntPtr deviceInfoSet,
        uint memberIndex,
        ref DeviceInfoData deviceInfoData);

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
