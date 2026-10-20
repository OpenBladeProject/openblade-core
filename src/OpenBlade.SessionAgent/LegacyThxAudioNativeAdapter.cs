using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32;
using Microsoft.Win32.SafeHandles;

namespace OpenBlade.SessionAgent;

internal interface ILegacyThxAudioNativeAdapter : IDisposable
{
    LegacyThxAudioNativeState Read();
    void SetSpatial(bool enabled);
}

internal sealed record LegacyThxAudioNativeState(
    string OutputDeviceId,
    bool SpatialEnabled,
    bool ListeningModeIsHeadphones);

// Private helper only. Construction activates COM; ordinary discovery must never construct this.
internal sealed class LegacyThxAudioNativeAdapter : ILegacyThxAudioNativeAdapter
{
    internal const string SettingsClass = "{087E4DB6-0519-4A63-9099-9201915E1371}";
    internal const string ThxClass = "{2CCFC059-A1C5-4408-BCE5-0B23690DA7A2}";
    internal const string SettingsInterface = "{392375FF-8204-4A26-B4C5-AF4B71ACA729}";
    internal const string ThxInterface = "{3B3AF690-70A6-4DDA-BBEA-0B96493E9DA3}";
    internal const string AppId = "{EF809770-D328-4410-9772-32775E710711}";
    internal const string TypeLibrary = "{1FD49704-F52F-4A60-80C6-E22723DEEBE2}";
    private const string UniversalMarshaler = "{00020424-0000-0000-C000-000000000046}";
    // Local server only, no custom IMarshal, no activate-as-activator.
    internal const uint ActivationContext = 0x4 | 0x1000 | 0x8000;
    private readonly int ownerThread = Environment.CurrentManagedThreadId;
    private readonly string originator = "OpenBlade-" + Guid.NewGuid().ToString("N");
    private FileStream? serverPin;
    private ILegacySettings? settings;
    private ILegacyThxSettings? thx;
    private bool comInitialized;
    private bool disposed;

    internal LegacyThxAudioNativeAdapter()
    {
        if (!OperatingSystem.IsWindows() || !Environment.Is64BitProcess)
        {
            throw new PlatformNotSupportedException("Legacy THX requires the isolated Windows x64 helper.");
        }
        try
        {
            string path = Path.Combine(Environment.SystemDirectory, "VSSrv.exe");
            serverPin = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            CheckRegistration(path);
            ThrowOnFailure(CoInitializeEx(0, 0));
            comInitialized = true;
            uint servicePid = RequireRunningService(path);
            settings = Activate<ILegacySettings>(SettingsClass, SettingsInterface);
            CheckRegistration(path);
            if (RequireRunningService(path) != servicePid)
            {
                throw new InvalidOperationException("The THX service changed during activation.");
            }
            ThrowOnFailure(settings.Init(checked((uint)Environment.ProcessId)));
            // Recheck immediately before the second activation; COM can still race a service exit.
            CheckRegistration(path);
            if (RequireRunningService(path) != servicePid)
            {
                throw new InvalidOperationException("The THX service changed before activation.");
            }
            thx = Activate<ILegacyThxSettings>(ThxClass, ThxInterface);
            CheckRegistration(path);
            if (RequireRunningService(path) != servicePid)
            {
                throw new InvalidOperationException("The THX service changed during activation.");
            }
            ThrowOnFailure(thx.Init(checked((uint)Environment.ProcessId)));
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    public LegacyThxAudioNativeState Read()
    {
        CheckThread();
        var output = new ushort[80];
        ThrowOnFailure(settings!.GetOutputDevice(output));
        ThrowOnFailure(thx!.GetSpatialProcessingState(out int spatial));
        ThrowOnFailure(thx.GetListeningMode(out int headphones));
        return new(ParseOutputDevice(output), ParseBoolean(spatial), ParseBoolean(headphones));
    }

    public void SetSpatial(bool enabled)
    {
        CheckThread();
        var notification = new byte[4096];
        int result = thx!.SetSpatialProcessingState(originator, enabled ? 1 : 0, out ulong size, notification);
        ValidateMutationResult(result, size);
    }

    internal static string ParseOutputDevice(ushort[] value)
    {
        if (value.Length != 80)
        {
            throw new InvalidDataException("The THX output identifier has the wrong capacity.");
        }
        int end = Array.IndexOf(value, (ushort)0);
        if (end <= 0)
        {
            throw new InvalidDataException("The THX output identifier is empty or unterminated.");
        }
        string result = new(value.Take(end).Select(v => (char)v).ToArray());
        // Windows MMDevice endpoint IDs are ASCII. Reject controls/surrogates/ambiguous truncation.
        if (result.Any(c => c < 0x20 || c > 0x7e))
        {
            throw new InvalidDataException("The THX output identifier is malformed.");
        }
        return result;
    }

    internal static bool ParseBoolean(int value) => value switch
    {
        0 => false,
        1 => true,
        _ => throw new InvalidDataException("The THX boolean readback is invalid."),
    };

    internal static void ValidateMutationResult(int result, ulong size)
    {
        // Any exception here is potentially post-submission. Callers must stop and read explicitly.
        ThrowOnFailure(result);
        if (size > 4096)
        {
            throw new InvalidDataException("The THX notification exceeds its fixed buffer.");
        }
    }

    private static void ThrowOnFailure(int result) => Marshal.ThrowExceptionForHR(result);

    private void CheckThread()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (ownerThread != Environment.CurrentManagedThreadId)
        {
            throw new InvalidOperationException("Legacy THX COM access must stay on its owning helper thread.");
        }
    }

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }
        CheckThread();
        disposed = true;
        try
        {
            if (thx is not null)
            {
                Marshal.FinalReleaseComObject(thx);
            }
            if (settings is not null)
            {
                Marshal.FinalReleaseComObject(settings);
            }
        }
        finally
        {
            thx = null;
            settings = null;
            if (comInitialized)
            {
                CoUninitialize();
            }
            serverPin?.Dispose();
        }
    }

    private static T Activate<T>(string classId, string interfaceId) where T : class
    {
        Guid clsid = new(classId);
        Guid iid = new(interfaceId);
        ThrowOnFailure(CoCreateInstance(ref clsid, 0, ActivationContext, ref iid, out nint pointer));
        try
        {
            return (T)Marshal.GetObjectForIUnknown(pointer);
        }
        finally
        {
            Marshal.Release(pointer);
        }
    }

    private static void CheckRegistration(string serverPath)
    {
        using RegistryKey machine = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
        using RegistryKey user = RegistryKey.OpenBaseKey(RegistryHive.CurrentUser, RegistryView.Registry64);
        string[] paths = [$"AppID\\{AppId}", "AppID\\VSSrv.exe", $"TypeLib\\{TypeLibrary}",
            $"CLSID\\{SettingsClass}", $"CLSID\\{ThxClass}", $"CLSID\\{UniversalMarshaler}",
            $"Interface\\{SettingsInterface}", $"Interface\\{ThxInterface}"];
        foreach (string path in paths)
        {
            using RegistryKey? currentUser = user.OpenSubKey("Software\\Classes\\" + path);
            if (currentUser is not null)
            {
                throw new InvalidDataException("A current-user THX COM registration override is present.");
            }
        }
        foreach (string id in new[] { SettingsClass, ThxClass })
        {
            string path = "Software\\Classes\\CLSID\\" + id;
            RequireValue(machine, path, "AppID", AppId);
            RequireValue(machine, path + "\\TypeLib", null, TypeLibrary);
            RequirePathValue(machine, path + "\\LocalServer32", null, serverPath);
            RejectKey(machine, path + "\\InprocServer32");
            RejectKey(machine, path + "\\InprocHandler32");
            RejectKey(machine, path + "\\TreatAs");
            RejectKey(machine, path + "\\AutoTreatAs");
        }
        string appPath = "Software\\Classes\\AppID\\" + AppId;
        RequireValue(machine, appPath, "LocalService", "VSSrv");
        using (RegistryKey? app = machine.OpenSubKey(appPath))
        {
            if (app is null || new[] { "DllSurrogate", "RemoteServerName", "RunAs", "ServiceParameters" }
                .Any(name => app.GetValue(name) is not null))
            {
                throw new InvalidDataException("The THX COM AppID contains an unsupported activation override.");
            }
        }
        foreach (string iid in new[] { SettingsInterface, ThxInterface })
        {
            string path = "Software\\Classes\\Interface\\" + iid;
            RequireValue(machine, path + "\\ProxyStubClsid32", null, UniversalMarshaler);
            RequireValue(machine, path + "\\TypeLib", null, TypeLibrary);
            RequireValue(machine, path + "\\TypeLib", "Version", "1.0");
        }
        string proxy = "Software\\Classes\\CLSID\\" + UniversalMarshaler;
        RequirePathValue(machine, proxy + "\\InprocServer32", null,
            Path.Combine(Environment.SystemDirectory, "oleaut32.dll"));
        RejectKey(machine, proxy + "\\TreatAs");
        RejectKey(machine, proxy + "\\AutoTreatAs");
        RequirePathValue(machine, "Software\\Classes\\TypeLib\\" + TypeLibrary + "\\1.0\\0\\win64", null, serverPath);
    }

    private static void RequireValue(RegistryKey root, string path, string? name, string expected)
    {
        using RegistryKey? key = root.OpenSubKey(path);
        if (key?.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames) is not string actual ||
            !actual.Equals(expected, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("The legacy THX COM registration is not the pinned registration.");
        }
    }

    private static void RequirePathValue(RegistryKey root, string path, string? name, string expected)
    {
        using RegistryKey? key = root.OpenSubKey(path);
        if (key?.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames) is not string actual ||
            !IsExactServerPath(actual, expected))
        {
            throw new InvalidDataException("The legacy THX COM registration redirects an executable or type library.");
        }
    }

    internal static bool IsExactServerPath(string value, string expected)
    {
        // No command arguments, relative paths, environment expansion, or alternate resource paths.
        string unquoted = value.Length >= 2 && value[0] == '"' && value[^1] == '"'
            ? value[1..^1] : value;
        return unquoted.Equals(expected, StringComparison.OrdinalIgnoreCase);
    }

    private static void RejectKey(RegistryKey root, string path)
    {
        using RegistryKey? key = root.OpenSubKey(path);
        if (key is not null)
        {
            throw new InvalidDataException("The legacy THX COM registration contains a redirection.");
        }
    }

    private static uint RequireRunningService(string path)
    {
        nint manager = OpenSCManagerW(null, null, 1);
        if (manager == 0)
        {
            throw new InvalidOperationException("The service manager cannot be queried.");
        }
        try
        {
            nint service = OpenServiceW(manager, "VSSrv", 4);
            if (service == 0)
            {
                throw new InvalidOperationException("The legacy THX service is unavailable.");
            }
            try
            {
                if (!QueryServiceStatusEx(service, 0, out ServiceStatus status,
                    (uint)Marshal.SizeOf<ServiceStatus>(), out _) || status.CurrentState != 4 ||
                    status.ServiceType != 0x10 || status.ProcessId == 0)
                {
                    throw new InvalidOperationException("The pinned legacy THX service must already be running.");
                }
                using SafeProcessHandle process = OpenProcess(0x1000, false, status.ProcessId);
                var image = new StringBuilder(32768);
                uint imageSize = (uint)image.Capacity;
                if (process.IsInvalid || !QueryFullProcessImageNameW(process, 0, image, ref imageSize) ||
                    !IsExactServerPath(image.ToString(), path))
                {
                    throw new InvalidDataException("The THX service process does not match the pinned executable.");
                }
                using RegistryKey machine = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
                RequirePathValue(machine, "SYSTEM\\CurrentControlSet\\Services\\VSSrv", "ImagePath", path);
                return status.ProcessId;
            }
            finally
            {
                CloseServiceHandle(service);
            }
        }
        finally
        {
            CloseServiceHandle(manager);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ServiceStatus
    {
        public uint ServiceType, CurrentState, ControlsAccepted, Win32ExitCode, ServiceSpecificExitCode;
        public uint CheckPoint, WaitHint, ProcessId, ServiceFlags;
    }

    [DllImport("kernel32.dll", ExactSpelling = true)]
    private static extern SafeProcessHandle OpenProcess(uint access, [MarshalAs(UnmanagedType.Bool)] bool inherit, uint processId);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryFullProcessImageNameW(SafeProcessHandle process, uint flags, StringBuilder path, ref uint size);
    [DllImport("ole32.dll", ExactSpelling = true)]
    private static extern int CoInitializeEx(nint reserved, uint flags);
    [DllImport("ole32.dll", ExactSpelling = true)]
    private static extern void CoUninitialize();
    [DllImport("ole32.dll", ExactSpelling = true)]
    private static extern int CoCreateInstance(ref Guid clsid, nint outer, uint context, ref Guid iid, out nint instance);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern nint OpenSCManagerW(string? machine, string? database, uint access);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern nint OpenServiceW(nint manager, string name, uint access);
    [DllImport("advapi32.dll", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryServiceStatusEx(nint service, int level, out ServiceStatus status, uint size, out uint needed);
    [DllImport("advapi32.dll", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseServiceHandle(nint service);

    // Fixed C arrays, HRESULTs, and int32 BOOLs from VSSrv 2.6.5.0's embedded TYPELIB.
    // Trailing methods are retained to make the complete interface order reviewable.
    [ComImport, Guid("392375FF-8204-4A26-B4C5-AF4B71ACA729"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface ILegacySettings
    {
        [PreserveSig] int Init(uint processId);
        [PreserveSig] int GetProcessingState(out int enabled);
        [PreserveSig] int SetProcessingState(int enabled);
        [PreserveSig] int GetHRTFState(out int enabled);
        [PreserveSig] int SetHRTFState(int enabled);
        [PreserveSig] int IsCustomHRTFPresent(out int present);
        [PreserveSig] int ReloadHRTF();
        [PreserveSig] int GetOutputDeviceParams(uint version, [In, MarshalAs(UnmanagedType.LPArray, SizeConst = 64)] uint[] parameters, [Out, MarshalAs(UnmanagedType.LPArray, SizeConst = 64)] float[] data, uint count);
        [PreserveSig] int GetDefaultOutputDeviceParams(uint version, [In, MarshalAs(UnmanagedType.LPArray, SizeConst = 64)] uint[] parameters, [Out, MarshalAs(UnmanagedType.LPArray, SizeConst = 64)] float[] data, uint count);
        [PreserveSig] int SetOutputDeviceParams(uint version, [In, MarshalAs(UnmanagedType.LPArray, SizeConst = 64)] uint[] parameters, [In, MarshalAs(UnmanagedType.LPArray, SizeConst = 64)] float[] data, uint count);
        [PreserveSig] int GetOutputDevice([Out, MarshalAs(UnmanagedType.LPArray, SizeConst = 80)] ushort[] deviceId);
        [PreserveSig] int SetOutputDevice([In, MarshalAs(UnmanagedType.LPArray, SizeConst = 80)] ushort[] deviceId, uint deviceType);
        [PreserveSig] int GetInputDevice([Out, MarshalAs(UnmanagedType.LPArray, SizeConst = 80)] ushort[] deviceId);
        [PreserveSig] int SetInputDevice([In, MarshalAs(UnmanagedType.LPArray, SizeConst = 80)] ushort[] deviceId);
        [PreserveSig] int GetInputSidetoneState(out int enabled);
        [PreserveSig] int SetInputSidetoneState(int enabled);
        [PreserveSig] int GetInputSidetoneLevel(out float level);
        [PreserveSig] int SetInputSidetoneLevel(float level);
    }

    [ComImport, Guid("3B3AF690-70A6-4DDA-BBEA-0B96493E9DA3"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface ILegacyThxSettings
    {
        [PreserveSig] int Init(uint processId);
        [PreserveSig] int GetSpatialProcessingState(out int enabled);
        [PreserveSig] int SetSpatialProcessingState([MarshalAs(UnmanagedType.LPStr)] string originator, int enabled, out ulong size, [Out, MarshalAs(UnmanagedType.LPArray, SizeConst = 4096)] byte[] message);
        [PreserveSig] int GetProcessingMode(out uint mode);
        [PreserveSig] int SetProcessingMode([MarshalAs(UnmanagedType.LPStr)] string originator, uint mode, out ulong size, [Out, MarshalAs(UnmanagedType.LPArray, SizeConst = 4096)] byte[] message);
        [PreserveSig] int GetCustomRoomType(out uint room);
        [PreserveSig] int SetCustomRoomType([MarshalAs(UnmanagedType.LPStr)] string originator, uint room, out ulong size, [Out, MarshalAs(UnmanagedType.LPArray, SizeConst = 4096)] byte[] message);
        [PreserveSig] int GetCurrentModeEQGains([Out, MarshalAs(UnmanagedType.LPArray, SizeConst = 31)] float[] gains);
        [PreserveSig] int SetCurrentModeEQGains([MarshalAs(UnmanagedType.LPStr)] string originator, [In, MarshalAs(UnmanagedType.LPArray, SizeConst = 31)] float[] gains, out ulong size, [Out, MarshalAs(UnmanagedType.LPArray, SizeConst = 4096)] byte[] message);
        [PreserveSig] int ResetCurrentModeEQGains([MarshalAs(UnmanagedType.LPStr)] string originator, out ulong size, [Out, MarshalAs(UnmanagedType.LPArray, SizeConst = 4096)] byte[] message);
        [PreserveSig] int GetListeningMode(out int isHeadphones);
        [PreserveSig] int SetListeningMode([MarshalAs(UnmanagedType.LPStr)] string originator, int isHeadphones, out ulong size, [Out, MarshalAs(UnmanagedType.LPArray, SizeConst = 4096)] byte[] message);
        [PreserveSig] int GetBassBoostState(out int enabled);
        [PreserveSig] int SetBassBoostState([MarshalAs(UnmanagedType.LPStr)] string originator, int enabled, out ulong size, [Out, MarshalAs(UnmanagedType.LPArray, SizeConst = 4096)] byte[] message);
        [PreserveSig] int GetDialogEnhanceState(out int enabled);
        [PreserveSig] int SetDialogEnhanceState([MarshalAs(UnmanagedType.LPStr)] string originator, int enabled, out ulong size, [Out, MarshalAs(UnmanagedType.LPArray, SizeConst = 4096)] byte[] message);
        [PreserveSig] int GetDRCLevel(out float value);
        [PreserveSig] int SetDRCLevel([MarshalAs(UnmanagedType.LPStr)] string originator, float value, out ulong size, [Out, MarshalAs(UnmanagedType.LPArray, SizeConst = 4096)] byte[] message);
        [PreserveSig] int GetTiltEnabled(out int enabled);
        [PreserveSig] int SetTiltEnabled([MarshalAs(UnmanagedType.LPStr)] string originator, int enabled, out ulong size, [Out, MarshalAs(UnmanagedType.LPArray, SizeConst = 4096)] byte[] message);
        [PreserveSig] int GetParam(uint parameter, out float value);
        [PreserveSig] int SetParam([MarshalAs(UnmanagedType.LPStr)] string originator, uint parameter, float value, out ulong size, [Out, MarshalAs(UnmanagedType.LPArray, SizeConst = 4096)] byte[] message);
    }
}
