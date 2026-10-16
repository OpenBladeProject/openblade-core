using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace OpenBlade.SessionAgent;

// Every call takes a fresh Windows snapshot. No service, device, or admission
// result survives a request. These queries never open the audio/native backend.
internal static class WindowsThxAdmissionQueries
{
    private const uint DigcfPresent = 2;
    private const uint DigcfAllClasses = 4;
    private const uint DevpropTypeStringList = 0x2012;
    private const int ErrorNoMoreItems = 259;
    private const int ErrorNotFound = 1168;
    private const int ErrorServiceDoesNotExist = 1060;
    private const uint ScManagerConnect = 1;
    private const uint ServiceQueryConfig = 1;
    private const uint ServiceQueryStatus = 4;
    private const uint ServiceRunning = 4;
    private const int MaximumDevices = 8192;
    private const int MaximumDeviceIdCharacters = 200;
    private const int MaximumPropertyBytes = 65536;
    private const int MaximumServiceConfigBytes = 8192;
    private static readonly DevicePropertyKey HardwareIds = new(
        new Guid("a45c254e-df1c-4efd-8020-67d146a850e0"), 3);

    internal static bool HasExactDeviceIdentities(ThxAudioCompatibilityProfile profile)
    {
        using SafeDeviceInfoSet devices = SetupDiGetClassDevs(
            IntPtr.Zero, null, IntPtr.Zero, DigcfPresent | DigcfAllClasses);
        if (devices.IsInvalid)
        {
            throw NativeFailure();
        }
        bool usb = false;
        bool codec = false;
        var instanceId = new char[MaximumDeviceIdCharacters];
        var propertyBuffer = new byte[MaximumPropertyBytes];
        for (uint index = 0; index < MaximumDevices; index++)
        {
            var device = new DeviceInfoData { Size = (uint)Marshal.SizeOf<DeviceInfoData>() };
            if (!SetupDiEnumDeviceInfo(devices, index, ref device))
            {
                int error = Marshal.GetLastWin32Error();
                if (error == ErrorNoMoreItems)
                {
                    return false;
                }
                throw new Win32Exception(error);
            }
            if (!usb)
            {
                if (!SetupDiGetDeviceInstanceId(devices, ref device, instanceId,
                    (uint)instanceId.Length, out uint requiredCharacters))
                {
                    throw NativeFailure();
                }
                usb = ThxAudioCompatibility.MatchesUsb(profile,
                    ReadTerminatedString(instanceId, checked((int)requiredCharacters)));
            }
            if (!codec)
            {
                DevicePropertyKey key = HardwareIds;
                if (SetupDiGetDeviceProperty(devices, ref device, ref key, out uint type,
                    propertyBuffer, (uint)propertyBuffer.Length, out uint requiredBytes, 0))
                {
                    codec = ThxAudioCompatibility.MatchesCodec(profile,
                        ParseHardwareIds(propertyBuffer, requiredBytes, type));
                }
                else
                {
                    int error = Marshal.GetLastWin32Error();
                    // A devnode need not have HardwareIds. Invalid data, removal,
                    // access failures, or an oversized property are not absence.
                    if (error != ErrorNotFound)
                    {
                        throw new Win32Exception(error);
                    }
                }
            }
            if (usb && codec)
            {
                return true;
            }
        }
        throw new InvalidDataException("The present-device inventory exceeded its admission bound.");
    }

    internal static bool HasCompetingService()
    {
        using SafeServiceHandle manager = OpenManager();
        if (IsRunning(manager, "RazerSynapseService"))
        {
            return true;
        }
        // Preserve the display-name alias even when a different service key was
        // used to install Synapse. SCM display names map uniquely to service keys.
        var name = new char[2048];
        uint length = (uint)name.Length;
        if (!GetServiceKeyName(manager, "Razer Synapse Service", name, ref length))
        {
            int error = Marshal.GetLastWin32Error();
            if (error == ErrorServiceDoesNotExist)
            {
                return false;
            }
            throw new Win32Exception(error);
        }
        return IsRunning(manager, ReadTerminatedString(name, checked((int)length + 1)));
    }

    internal static bool HasRunningPinnedService(string serviceName, string expectedBinary)
    {
        using SafeServiceHandle manager = OpenManager();
        using SafeServiceHandle service = OpenService(manager, serviceName, ServiceQueryConfig | ServiceQueryStatus);
        if (service.IsInvalid)
        {
            return ServiceIsAbsentOrThrow(Marshal.GetLastWin32Error());
        }
        // QueryServiceConfig's documented maximum is 8 KiB. A single bounded
        // read avoids a size/read race and rejects any unexpected oversized data.
        IntPtr buffer = Marshal.AllocHGlobal(MaximumServiceConfigBytes);
        try
        {
            if (!QueryServiceConfig(service, buffer, MaximumServiceConfigBytes, out _))
            {
                throw NativeFailure();
            }
            ServiceConfiguration configuration = Marshal.PtrToStructure<ServiceConfiguration>(buffer);
            string path = ReadBufferString(buffer, MaximumServiceConfigBytes, configuration.BinaryPathName);
            return string.Equals(NormalizeServiceBinary(path), Path.GetFullPath(expectedBinary),
                StringComparison.OrdinalIgnoreCase) && IsRunning(service);
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    internal static string[] ParseHardwareIds(byte[] buffer, uint byteCount, uint propertyType)
    {
        if (propertyType != DevpropTypeStringList || byteCount < 4
            || byteCount > MaximumPropertyBytes || byteCount > buffer.Length || (byteCount & 1) != 0)
        {
            throw new InvalidDataException("Invalid device HardwareIds metadata.");
        }
        string value = new UnicodeEncoding(false, false, true).GetString(buffer, 0, checked((int)byteCount));
        if (!value.EndsWith("\0\0", StringComparison.Ordinal))
        {
            throw new InvalidDataException("The device HardwareIds list is not terminated.");
        }
        if (value.Length == 2)
        {
            return [];
        }
        string[] ids = value[..^2].Split('\0');
        if (ids.Any(string.IsNullOrEmpty))
        {
            throw new InvalidDataException("The device HardwareIds list contains an early terminator.");
        }
        return ids;
    }

    internal static string ReadTerminatedString(char[] buffer, int characterCount)
    {
        if (characterCount < 2 || characterCount > buffer.Length
            || buffer[characterCount - 1] != '\0'
            || Array.IndexOf(buffer, '\0', 0, characterCount - 1) >= 0)
        {
            throw new InvalidDataException("Invalid native identity string.");
        }
        return new string(buffer, 0, characterCount - 1);
    }

    internal static string ReadBufferString(IntPtr buffer, int bufferBytes, IntPtr value)
    {
        long offset = value.ToInt64() - buffer.ToInt64();
        if (offset < Marshal.SizeOf<ServiceConfiguration>() || offset >= bufferBytes || (offset & 1) != 0)
        {
            throw new InvalidDataException("Invalid service binary-path pointer.");
        }
        int characters = (bufferBytes - checked((int)offset)) / sizeof(char);
        for (int index = 0; index < characters; index++)
        {
            if (Marshal.ReadInt16(value, index * sizeof(char)) == 0)
            {
                return index > 0 ? Marshal.PtrToStringUni(value, index)!
                    : throw new InvalidDataException("Empty service binary path.");
            }
        }
        throw new InvalidDataException("Unterminated service binary path.");
    }

    internal static string NormalizeServiceBinary(string commandLine)
    {
        string value = commandLine.Trim();
        if (value.StartsWith('"'))
        {
            int end = value.IndexOf('"', 1);
            return end > 1 ? Path.GetFullPath(value[1..end]) : string.Empty;
        }
        int separator = value.IndexOf(' ');
        return Path.GetFullPath(separator < 0 ? value : value[..separator]);
    }

    // Only SCM's definitive absence result means absent. Access denied and
    // deletion-in-progress must propagate to Check's fail-closed result.
    internal static bool ServiceIsAbsentOrThrow(int error) => error == ErrorServiceDoesNotExist
        ? false : throw new Win32Exception(error);

    private static bool IsRunning(SafeServiceHandle manager, string name)
    {
        using SafeServiceHandle service = OpenService(manager, name, ServiceQueryStatus);
        return service.IsInvalid ? ServiceIsAbsentOrThrow(Marshal.GetLastWin32Error()) : IsRunning(service);
    }

    private static bool IsRunning(SafeServiceHandle service)
    {
        if (!QueryServiceStatus(service, out ServiceStatus status))
        {
            throw NativeFailure();
        }
        return status.CurrentState == ServiceRunning;
    }

    private static SafeServiceHandle OpenManager()
    {
        SafeServiceHandle manager = OpenSCManager(null, null, ScManagerConnect);
        if (manager.IsInvalid)
        {
            int error = Marshal.GetLastWin32Error();
            manager.Dispose();
            throw new Win32Exception(error);
        }
        return manager;
    }

    private static Win32Exception NativeFailure() => new(Marshal.GetLastWin32Error());

    [StructLayout(LayoutKind.Sequential)]
    private struct DeviceInfoData
    {
        internal uint Size;
        internal Guid ClassGuid;
        internal uint DevInst;
        internal UIntPtr Reserved;
    }

    [StructLayout(LayoutKind.Sequential)]
    private readonly struct DevicePropertyKey(Guid formatId, uint propertyId)
    {
        internal readonly Guid FormatId = formatId;
        internal readonly uint PropertyId = propertyId;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ServiceStatus
    {
        internal uint ServiceType;
        internal uint CurrentState;
        internal uint ControlsAccepted;
        internal uint Win32ExitCode;
        internal uint ServiceSpecificExitCode;
        internal uint CheckPoint;
        internal uint WaitHint;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ServiceConfiguration
    {
        internal uint ServiceType;
        internal uint StartType;
        internal uint ErrorControl;
        internal IntPtr BinaryPathName;
        internal IntPtr LoadOrderGroup;
        internal uint TagId;
        internal IntPtr Dependencies;
        internal IntPtr ServiceStartName;
        internal IntPtr DisplayName;
    }

    private sealed class SafeDeviceInfoSet : SafeHandleZeroOrMinusOneIsInvalid
    {
        private SafeDeviceInfoSet() : base(true) { }
        protected override bool ReleaseHandle() => SetupDiDestroyDeviceInfoList(handle);
    }

    private sealed class SafeServiceHandle : SafeHandleZeroOrMinusOneIsInvalid
    {
        private SafeServiceHandle() : base(true) { }
        protected override bool ReleaseHandle() => CloseServiceHandle(handle);
    }

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeDeviceInfoSet SetupDiGetClassDevs(IntPtr classGuid, string? enumerator, IntPtr parent, uint flags);

    [DllImport("setupapi.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetupDiEnumDeviceInfo(SafeDeviceInfoSet devices, uint index, ref DeviceInfoData device);

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetupDiGetDeviceInstanceId(SafeDeviceInfoSet devices, ref DeviceInfoData device,
        [Out] char[] instanceId, uint size, out uint requiredSize);

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetupDiGetDeviceProperty(SafeDeviceInfoSet devices, ref DeviceInfoData device,
        ref DevicePropertyKey key, out uint propertyType, [Out] byte[] buffer, uint size, out uint requiredSize, uint flags);

    [DllImport("setupapi.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetupDiDestroyDeviceInfoList(IntPtr devices);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeServiceHandle OpenSCManager(string? machineName, string? databaseName, uint desiredAccess);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeServiceHandle OpenService(SafeServiceHandle manager, string name, uint desiredAccess);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetServiceKeyName(SafeServiceHandle manager, string displayName, [Out] char[] name, ref uint size);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryServiceStatus(SafeServiceHandle service, out ServiceStatus status);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryServiceConfig(SafeServiceHandle service, IntPtr buffer, int bufferSize, out uint bytesNeeded);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseServiceHandle(IntPtr service);
}
