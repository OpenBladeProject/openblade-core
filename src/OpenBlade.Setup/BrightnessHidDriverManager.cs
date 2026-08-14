using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace OpenBlade.Setup;

public interface IBrightnessHidDriverManager
{
    Task<BrightnessHidDriverInstallState> CaptureAsync(CancellationToken cancellationToken);
    Task InstallAsync(string infPath, CancellationToken cancellationToken);
    Task VerifyAsync(CancellationToken cancellationToken);
    Task RestoreAsync(
        BrightnessHidDriverInstallState priorState,
        string fallbackInfPath,
        CancellationToken cancellationToken);
    Task<string?> CommitAsync(
        BrightnessHidDriverInstallState priorState,
        CancellationToken cancellationToken);
    Task RemoveAsync(CancellationToken cancellationToken);
    Task<string?> CommitRemovalAsync(
        BrightnessHidDriverInstallState priorState,
        CancellationToken cancellationToken);
}

public sealed class WindowsBrightnessHidDriverManager : IBrightnessHidDriverManager
{
    internal const string RootEnumeratedDeviceId = "OSSBLADE_OPENBLADE_BRIGHTNESS_HID";
    public const string HardwareId = @"ROOT\" + RootEnumeratedDeviceId;
    public const string DeviceInstanceId = HardwareId + @"\0000";
    public const string InfFileName = "OpenBlade.BrightnessHid.inf";
    public const string DriverProvider = "OSSBlade";

    private string? installedPublishedInfPath;

    public Task<BrightnessHidDriverInstallState> CaptureAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(CaptureSingleDevice());
    }

    public Task InstallAsync(string infPath, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        string sourceInf = ValidateInfPath(infPath);
        BrightnessHidDriverInstallState before = CaptureSingleDevice();
        if (!before.IsWellFormed)
        {
            throw new InvalidOperationException(
                "The existing OpenBlade brightness HID device is not in a safely serviceable state.");
        }

        if (!IsServiceablePriorState(before))
        {
            throw new InvalidOperationException(
                "The existing OpenBlade brightness HID device is not started; setup will not change state it cannot restore exactly.");
        }
        if (!IsOwnedPriorState(before))
        {
            throw new InvalidOperationException(
                "The OpenBlade brightness HID hardware ID is bound to an unexpected driver provider; setup made no changes.");
        }

        if (before.DevicePresent && !before.IsBound)
        {
            RemoveOwnedDevices();
        }

        if (!before.DevicePresent || !before.IsBound)
        {
            CreateRootDevice();
        }

        installedPublishedInfPath = CopyDriverPackage(sourceInf);
        UpdateDeviceDriver(sourceInf);
        return Task.CompletedTask;
    }

    public Task VerifyAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        BrightnessHidDriverInstallState state = CaptureSingleDevice();
        if (!state.IsWellFormed
            || !state.DevicePresent
            || !state.DeviceStarted
            || !string.Equals(state.DriverProvider, DriverProvider, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "The OpenBlade brightness HID driver did not start with the expected identity.");
        }

        if (installedPublishedInfPath is not null
            && !PublishedInfEquals(state.PublishedInfPath, installedPublishedInfPath))
        {
            throw new InvalidOperationException(
                "The OpenBlade brightness HID device is not bound to the package setup installed.");
        }

        return Task.CompletedTask;
    }

    public Task RestoreAsync(
        BrightnessHidDriverInstallState priorState,
        string fallbackInfPath,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(priorState);
        cancellationToken.ThrowIfCancellationRequested();
        if (!priorState.IsWellFormed)
        {
            throw new InvalidOperationException(
                "The prior brightness HID driver state cannot be restored safely.");
        }

        if (!priorState.DevicePresent)
        {
            RemoveOwnedDevices();
            UninstallPublishedPackage(installedPublishedInfPath, throwIfInUse: true);
            installedPublishedInfPath = null;
            return Task.CompletedTask;
        }

        if (!priorState.IsBound)
        {
            RemoveOwnedDevices();
            UninstallPublishedPackage(installedPublishedInfPath, throwIfInUse: true);
            CreateRootDevice();
            installedPublishedInfPath = null;
            return Task.CompletedTask;
        }

        string priorInf = priorState.PublishedInfPath!;
        if (!File.Exists(priorInf))
        {
            string sourceInf = ValidateInfPath(fallbackInfPath);
            installedPublishedInfPath = CopyDriverPackage(sourceInf);
            priorInf = sourceInf;
        }

        if (!CaptureSingleDevice().DevicePresent)
        {
            CreateRootDevice();
        }

        UpdateDeviceDriver(priorInf);
        BrightnessHidDriverInstallState restored = CaptureSingleDevice();
        if (!IsExactRestoredState(restored, priorState))
        {
            throw new InvalidOperationException(
                "The prior OpenBlade brightness HID driver state was not restored exactly.");
        }

        if (installedPublishedInfPath is not null
            && !PublishedInfEquals(installedPublishedInfPath, restored.PublishedInfPath))
        {
            UninstallPublishedPackage(installedPublishedInfPath, throwIfInUse: true);
        }

        installedPublishedInfPath = null;
        return Task.CompletedTask;
    }

    public Task<string?> CommitAsync(
        BrightnessHidDriverInstallState priorState,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(priorState);
        cancellationToken.ThrowIfCancellationRequested();
        if (!priorState.DevicePresent
            || priorState.PublishedInfPath is null
            || PublishedInfEquals(priorState.PublishedInfPath, installedPublishedInfPath))
        {
            return Task.FromResult<string?>(null);
        }

        try
        {
            UninstallPublishedPackage(priorState.PublishedInfPath, throwIfInUse: false);
            return Task.FromResult<string?>(null);
        }
        catch (Exception exception)
        {
            return Task.FromResult<string?>(
                "OpenBlade was updated successfully, but the superseded brightness HID "
                + $"driver package was retained. Cleanup detail: {exception.Message}");
        }
    }

    public Task RemoveAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        BrightnessHidDriverInstallState state = CaptureSingleDevice();
        if (!state.IsWellFormed)
        {
            throw new InvalidOperationException(
                "The OpenBlade brightness HID driver state cannot be removed safely.");
        }
        if (!IsOwnedPriorState(state))
        {
            throw new InvalidOperationException(
                "The OpenBlade brightness HID hardware ID is bound to an unexpected driver provider; setup made no changes.");
        }

        RemoveOwnedDevices();
        installedPublishedInfPath = state.PublishedInfPath;
        return Task.CompletedTask;
    }

    public Task<string?> CommitRemovalAsync(
        BrightnessHidDriverInstallState priorState,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(priorState);
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            UninstallPublishedPackage(priorState.PublishedInfPath, throwIfInUse: false);
            installedPublishedInfPath = null;
            return Task.FromResult<string?>(null);
        }
        catch (Exception exception)
        {
            return Task.FromResult<string?>(
                "OpenBlade was removed successfully, but its brightness HID driver "
                + $"package was retained. Cleanup detail: {exception.Message}");
        }
    }

    private static BrightnessHidDriverInstallState CaptureSingleDevice()
    {
        using DeviceInfoSet devices = DeviceInfoSet.GetAll();
        List<BrightnessHidDriverInstallState> matches = [];
        foreach (SP_DEVINFO_DATA device in devices.Enumerate())
        {
            string[] hardwareIds = devices.GetMultiStringProperty(device, NativeMethods.SPDRP_HARDWAREID);
            if (!hardwareIds.Contains(HardwareId, StringComparer.OrdinalIgnoreCase))
            {
                continue;
            }

            string instanceId = devices.GetInstanceId(device);
            if (!IsExpectedDeviceInstanceId(instanceId))
            {
                throw new InvalidOperationException(
                    $"Refusing unexpected brightness HID device instance '{instanceId}'.");
            }

            string? driverKey = devices.GetStringProperty(device, NativeMethods.SPDRP_DRIVER);
            string? infPath = null;
            string? versionText = null;
            string? provider = null;
            if (!string.IsNullOrWhiteSpace(driverKey))
            {
                if (!TryValidateDriverKey(driverKey, device.ClassGuid))
                {
                    throw new InvalidOperationException(
                        $"The brightness HID device has unexpected driver key '{driverKey}'.");
                }

                using RegistryKey? key = Registry.LocalMachine.OpenSubKey(
                    $@"SYSTEM\CurrentControlSet\Control\Class\{driverKey}");
                infPath = key?.GetValue("InfPath") as string;
                versionText = key?.GetValue("DriverVersion") as string;
                provider = key?.GetValue("ProviderName") as string;
            }

            Version? version = Version.TryParse(versionText, out Version? parsed)
                ? parsed
                : null;
            bool started = NativeMethods.CM_Get_DevNode_Status(
                out uint status,
                out _,
                device.DevInst,
                0) == NativeMethods.CR_SUCCESS
                && (status & NativeMethods.DN_STARTED) != 0;
            matches.Add(new(
                true,
                instanceId,
                ResolvePublishedInfPath(infPath),
                version,
                provider,
                started));
        }

        return matches.Count switch
        {
            0 => new(false),
            1 => matches[0],
            _ => throw new InvalidOperationException(
                "Multiple OpenBlade brightness HID root devices were found; setup made no changes."),
        };
    }

    private static void CreateRootDevice()
    {
        Guid hidClass = NativeMethods.HidClassGuid;
        using DeviceInfoSet devices = DeviceInfoSet.Create(hidClass);
        SP_DEVINFO_DATA device = devices.CreateDevice(
            RootEnumeratedDeviceId,
            hidClass,
            NativeMethods.DICD_GENERATE_ID);
        devices.SetMultiStringProperty(device, NativeMethods.SPDRP_HARDWAREID, [HardwareId]);
        devices.RegisterDevice(device);
    }

    private static void UpdateDeviceDriver(string infPath)
    {
        if (!NativeMethods.UpdateDriverForPlugAndPlayDevices(
            IntPtr.Zero,
            HardwareId,
            infPath,
            NativeMethods.INSTALLFLAG_FORCE,
            out _))
        {
            throw new Win32Exception(
                Marshal.GetLastWin32Error(),
                "Unable to bind the OpenBlade brightness HID driver package.");
        }
    }

    private static string CopyDriverPackage(string infPath)
    {
        char[] destination = new char[NativeMethods.MAX_PATH];
        if (!NativeMethods.SetupCopyOEMInf(
            infPath,
            null,
            NativeMethods.SPOST_PATH,
            0,
            destination,
            (uint)destination.Length,
            out _,
            IntPtr.Zero))
        {
            int error = Marshal.GetLastWin32Error();
            if (error != NativeMethods.ERROR_FILE_EXISTS)
            {
                throw new Win32Exception(error, "Unable to stage the OpenBlade brightness HID package.");
            }
        }

        string published = new string(destination).TrimEnd('\0');
        if (string.IsNullOrWhiteSpace(published))
        {
            throw new InvalidOperationException(
                "Windows did not report the published OpenBlade brightness HID INF path.");
        }

        return published;
    }

    private static void RemoveOwnedDevices()
    {
        using DeviceInfoSet devices = DeviceInfoSet.GetAll();
        List<SP_DEVINFO_DATA> matches = devices.Enumerate()
            .Where(device => devices.GetMultiStringProperty(
                    device,
                    NativeMethods.SPDRP_HARDWAREID)
                .Contains(HardwareId, StringComparer.OrdinalIgnoreCase))
            .ToList();
        if (matches.Count > 1)
        {
            throw new InvalidOperationException(
                "Multiple OpenBlade brightness HID root devices were found; setup made no changes.");
        }

        foreach (SP_DEVINFO_DATA device in matches)
        {
            string instanceId = devices.GetInstanceId(device);
            if (!IsExpectedDeviceInstanceId(instanceId))
            {
                throw new InvalidOperationException(
                    $"Refusing unexpected brightness HID device instance '{instanceId}'.");
            }
            devices.RemoveDevice(device);
        }
    }

    private static void UninstallPublishedPackage(string? publishedInfPath, bool throwIfInUse)
    {
        if (string.IsNullOrWhiteSpace(publishedInfPath))
        {
            return;
        }

        string publishedName = Path.GetFileName(publishedInfPath);
        if (!publishedName.StartsWith("oem", StringComparison.OrdinalIgnoreCase)
            || !publishedName.EndsWith(".inf", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"Refusing to remove unexpected published driver package '{publishedName}'.");
        }

        if (!NativeMethods.SetupUninstallOEMInf(publishedName, 0, IntPtr.Zero))
        {
            int error = Marshal.GetLastWin32Error();
            if (error == NativeMethods.ERROR_FILE_NOT_FOUND)
            {
                return;
            }

            if (!throwIfInUse && error == NativeMethods.ERROR_SHARING_VIOLATION)
            {
                return;
            }

            throw new Win32Exception(error, "Unable to remove the OpenBlade brightness HID package.");
        }
    }

    private static string ValidateInfPath(string infPath)
    {
        string fullPath = Path.GetFullPath(infPath);
        if (!string.Equals(Path.GetFileName(fullPath), InfFileName, StringComparison.Ordinal)
            || !File.Exists(fullPath))
        {
            throw new InvalidOperationException(
                $"The OpenBlade brightness HID INF is missing or has an unexpected name: '{fullPath}'.");
        }

        return fullPath;
    }

    private static string? ResolvePublishedInfPath(string? infPath) =>
        string.IsNullOrWhiteSpace(infPath)
            ? null
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "INF", infPath);

    private static bool PublishedInfEquals(string? left, string? right) =>
        left is not null
        && right is not null
        && string.Equals(
            Path.GetFileName(left),
            Path.GetFileName(right),
            StringComparison.OrdinalIgnoreCase);

    internal static bool TryValidateDriverKey(string driverKey, Guid classGuid)
    {
        string expectedPrefix = $"{{{classGuid:D}}}\\";
        if (!driverKey.StartsWith(expectedPrefix, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        ReadOnlySpan<char> instance = driverKey.AsSpan(expectedPrefix.Length);
        return instance.Length == 4 && instance.IndexOfAnyExcept("0123456789") < 0;
    }

    internal static bool IsServiceablePriorState(BrightnessHidDriverInstallState state) =>
        state.IsWellFormed
        && (!state.DevicePresent || !state.IsBound || state.DeviceStarted);

    internal static bool IsOwnedPriorState(BrightnessHidDriverInstallState state) =>
        !state.DevicePresent
        || !state.IsBound
        || string.Equals(state.DriverProvider, DriverProvider, StringComparison.Ordinal);

    internal static bool IsExpectedDeviceInstanceId(string instanceId) =>
        string.Equals(instanceId, DeviceInstanceId, StringComparison.OrdinalIgnoreCase);

    internal static bool IsExactRestoredState(
        BrightnessHidDriverInstallState restored,
        BrightnessHidDriverInstallState prior) =>
        restored.IsWellFormed
        && restored.DevicePresent
        && restored.DeviceStarted == prior.DeviceStarted
        && PublishedInfEquals(restored.PublishedInfPath, prior.PublishedInfPath)
        && restored.DriverVersion == prior.DriverVersion
        && string.Equals(
            restored.DriverProvider,
            prior.DriverProvider,
            StringComparison.Ordinal);
}

internal sealed class DeviceInfoSet : SafeHandle
{
    private DeviceInfoSet(IntPtr handle) : base(IntPtr.Zero, ownsHandle: true) => SetHandle(handle);

    public override bool IsInvalid => handle == IntPtr.Zero || handle == new IntPtr(-1);

    public static DeviceInfoSet GetAll() => FromHandle(NativeMethods.SetupDiGetClassDevs(
        IntPtr.Zero,
        null,
        IntPtr.Zero,
        NativeMethods.DIGCF_ALLCLASSES));

    public static DeviceInfoSet Create(Guid classGuid) => FromHandle(
        NativeMethods.SetupDiCreateDeviceInfoList(ref classGuid, IntPtr.Zero));

    public IEnumerable<SP_DEVINFO_DATA> Enumerate()
    {
        for (uint index = 0; ; index++)
        {
            SP_DEVINFO_DATA device = SP_DEVINFO_DATA.Create();
            if (NativeMethods.SetupDiEnumDeviceInfo(handle, index, ref device))
            {
                yield return device;
                continue;
            }

            int error = Marshal.GetLastWin32Error();
            if (error == NativeMethods.ERROR_NO_MORE_ITEMS)
            {
                yield break;
            }

            throw new Win32Exception(error, "Unable to enumerate Windows devices.");
        }
    }

    public SP_DEVINFO_DATA CreateDevice(string deviceName, Guid classGuid, uint flags)
    {
        SP_DEVINFO_DATA device = SP_DEVINFO_DATA.Create();
        if (!NativeMethods.SetupDiCreateDeviceInfo(
            handle,
            deviceName,
            ref classGuid,
            null,
            IntPtr.Zero,
            flags,
            ref device))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Unable to create the brightness HID root device.");
        }

        return device;
    }

    public void RegisterDevice(SP_DEVINFO_DATA device)
    {
        SP_DEVINFO_DATA mutable = device;
        if (!NativeMethods.SetupDiCallClassInstaller(
            NativeMethods.DIF_REGISTERDEVICE,
            handle,
            ref mutable))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Unable to register the brightness HID root device.");
        }
    }

    public void RemoveDevice(SP_DEVINFO_DATA device)
    {
        SP_DEVINFO_DATA mutable = device;
        SP_REMOVEDEVICE_PARAMS parameters = SP_REMOVEDEVICE_PARAMS.Create();
        if (!NativeMethods.SetupDiSetClassInstallParams(
            handle,
            ref mutable,
            ref parameters,
            (uint)Marshal.SizeOf<SP_REMOVEDEVICE_PARAMS>())
            || !NativeMethods.SetupDiCallClassInstaller(
                NativeMethods.DIF_REMOVE,
                handle,
                ref mutable))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Unable to remove the brightness HID root device.");
        }
    }

    public void SetMultiStringProperty(SP_DEVINFO_DATA device, uint property, string[] values)
    {
        byte[] bytes = System.Text.Encoding.Unicode.GetBytes(string.Join('\0', values) + "\0\0");
        SP_DEVINFO_DATA mutable = device;
        if (!NativeMethods.SetupDiSetDeviceRegistryProperty(
            handle,
            ref mutable,
            property,
            bytes,
            (uint)bytes.Length))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Unable to set a device registry property.");
        }
    }

    public string[] GetMultiStringProperty(SP_DEVINFO_DATA device, uint property) =>
        GetPropertyBytes(device, property) is { } bytes
            ? System.Text.Encoding.Unicode.GetString(bytes).TrimEnd('\0')
                .Split('\0', StringSplitOptions.RemoveEmptyEntries)
            : [];

    public string? GetStringProperty(SP_DEVINFO_DATA device, uint property) =>
        GetPropertyBytes(device, property) is { } bytes
            ? System.Text.Encoding.Unicode.GetString(bytes).TrimEnd('\0')
            : null;

    public string GetInstanceId(SP_DEVINFO_DATA device)
    {
        SP_DEVINFO_DATA mutable = device;
        char[] buffer = new char[512];
        if (!NativeMethods.SetupDiGetDeviceInstanceId(
            handle,
            ref mutable,
            buffer,
            (uint)buffer.Length,
            out _))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Unable to read a device instance ID.");
        }

        return new string(buffer).TrimEnd('\0');
    }

    protected override bool ReleaseHandle() => NativeMethods.SetupDiDestroyDeviceInfoList(handle);

    private byte[]? GetPropertyBytes(SP_DEVINFO_DATA device, uint property)
    {
        SP_DEVINFO_DATA mutable = device;
        _ = NativeMethods.SetupDiGetDeviceRegistryProperty(
            handle,
            ref mutable,
            property,
            out _,
            null,
            0,
            out uint size);
        int error = Marshal.GetLastWin32Error();
        if (size == 0 && error == NativeMethods.ERROR_INVALID_DATA)
        {
            return null;
        }

        if (size == 0 || error != NativeMethods.ERROR_INSUFFICIENT_BUFFER)
        {
            throw new Win32Exception(error, "Unable to size a device registry property.");
        }

        byte[] buffer = new byte[size];
        if (!NativeMethods.SetupDiGetDeviceRegistryProperty(
            handle,
            ref mutable,
            property,
            out _,
            buffer,
            (uint)buffer.Length,
            out _))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Unable to read a device registry property.");
        }

        return buffer;
    }

    private static DeviceInfoSet FromHandle(IntPtr handle)
    {
        DeviceInfoSet result = new(handle);
        if (result.IsInvalid)
        {
            result.Dispose();
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Unable to create a Windows device information set.");
        }

        return result;
    }
}

[StructLayout(LayoutKind.Sequential)]
internal struct SP_DEVINFO_DATA
{
    public uint CbSize;
    public Guid ClassGuid;
    public uint DevInst;
    public IntPtr Reserved;

    public static SP_DEVINFO_DATA Create() => new() { CbSize = (uint)Marshal.SizeOf<SP_DEVINFO_DATA>() };
}

[StructLayout(LayoutKind.Sequential)]
internal struct SP_CLASSINSTALL_HEADER
{
    public uint CbSize;
    public uint InstallFunction;
}

[StructLayout(LayoutKind.Sequential)]
internal struct SP_REMOVEDEVICE_PARAMS
{
    public SP_CLASSINSTALL_HEADER ClassInstallHeader;
    public uint Scope;
    public uint HwProfile;

    public static SP_REMOVEDEVICE_PARAMS Create() => new()
    {
        ClassInstallHeader = new()
        {
            CbSize = (uint)Marshal.SizeOf<SP_CLASSINSTALL_HEADER>(),
            InstallFunction = NativeMethods.DIF_REMOVE,
        },
        Scope = NativeMethods.DI_REMOVEDEVICE_GLOBAL,
    };
}

internal static class NativeMethods
{
    public const int MAX_PATH = 260;
    public const int ERROR_FILE_NOT_FOUND = 2;
    public const int ERROR_FILE_EXISTS = 80;
    public const int ERROR_INVALID_DATA = 13;
    public const int ERROR_SHARING_VIOLATION = 32;
    public const int ERROR_INSUFFICIENT_BUFFER = 122;
    public const int ERROR_NO_MORE_ITEMS = 259;
    public const uint DIGCF_PRESENT = 0x2;
    public const uint DIGCF_ALLCLASSES = 0x4;
    public const uint DICD_GENERATE_ID = 0x1;
    public const uint SPDRP_HARDWAREID = 0x1;
    public const uint SPDRP_DRIVER = 0x9;
    public const uint DIF_REMOVE = 0x5;
    public const uint DIF_REGISTERDEVICE = 0x19;
    public const uint DI_REMOVEDEVICE_GLOBAL = 0x1;
    public const uint SPOST_PATH = 0x1;
    public const uint INSTALLFLAG_FORCE = 0x1;
    public const uint CR_SUCCESS = 0;
    public const uint DN_STARTED = 0x8;
    public static readonly Guid HidClassGuid = new("745A17A0-74D3-11D0-B6FE-00A0C90F57DA");

    [DllImport("setupapi.dll", SetLastError = true)]
    public static extern IntPtr SetupDiGetClassDevs(
        IntPtr classGuid,
        [MarshalAs(UnmanagedType.LPWStr)] string? enumerator,
        IntPtr parent,
        uint flags);

    [DllImport("setupapi.dll", SetLastError = true)]
    public static extern IntPtr SetupDiCreateDeviceInfoList(ref Guid classGuid, IntPtr parent);

    [DllImport("setupapi.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool SetupDiCreateDeviceInfo(
        IntPtr deviceInfoSet,
        string deviceName,
        ref Guid classGuid,
        string? deviceDescription,
        IntPtr parent,
        uint creationFlags,
        ref SP_DEVINFO_DATA deviceInfoData);

    [DllImport("setupapi.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool SetupDiEnumDeviceInfo(
        IntPtr deviceInfoSet,
        uint memberIndex,
        ref SP_DEVINFO_DATA deviceInfoData);

    [DllImport("setupapi.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool SetupDiGetDeviceRegistryProperty(
        IntPtr deviceInfoSet,
        ref SP_DEVINFO_DATA deviceInfoData,
        uint property,
        out uint propertyRegDataType,
        [Out] byte[]? propertyBuffer,
        uint propertyBufferSize,
        out uint requiredSize);

    [DllImport("setupapi.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool SetupDiSetDeviceRegistryProperty(
        IntPtr deviceInfoSet,
        ref SP_DEVINFO_DATA deviceInfoData,
        uint property,
        byte[] propertyBuffer,
        uint propertyBufferSize);

    [DllImport("setupapi.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool SetupDiGetDeviceInstanceId(
        IntPtr deviceInfoSet,
        ref SP_DEVINFO_DATA deviceInfoData,
        [Out] char[] deviceInstanceId,
        uint deviceInstanceIdSize,
        out uint requiredSize);

    [DllImport("setupapi.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool SetupDiCallClassInstaller(
        uint installFunction,
        IntPtr deviceInfoSet,
        ref SP_DEVINFO_DATA deviceInfoData);

    [DllImport("setupapi.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool SetupDiSetClassInstallParams(
        IntPtr deviceInfoSet,
        ref SP_DEVINFO_DATA deviceInfoData,
        ref SP_REMOVEDEVICE_PARAMS classInstallParams,
        uint classInstallParamsSize);

    [DllImport("setupapi.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool SetupDiDestroyDeviceInfoList(IntPtr deviceInfoSet);

    [DllImport("setupapi.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool SetupCopyOEMInf(
        string sourceInfFileName,
        string? oemSourceMediaLocation,
        uint oemSourceMediaType,
        uint copyStyle,
        [Out] char[] destinationInfFileName,
        uint destinationInfFileNameSize,
        out uint requiredSize,
        IntPtr destinationInfFileNameComponent);

    [DllImport("setupapi.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool SetupUninstallOEMInf(
        string infFileName,
        uint flags,
        IntPtr reserved);

    [DllImport("newdev.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool UpdateDriverForPlugAndPlayDevices(
        IntPtr parentWindow,
        string hardwareId,
        string fullInfPath,
        uint installFlags,
        [MarshalAs(UnmanagedType.Bool)] out bool rebootRequired);

    [DllImport("cfgmgr32.dll")]
    public static extern uint CM_Get_DevNode_Status(
        out uint status,
        out uint problemNumber,
        uint deviceInstance,
        uint flags);
}
