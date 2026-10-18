using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using Microsoft.Win32;

namespace OpenBlade.Tray;

public sealed record RazerDriverInstallationInspection(bool Admitted, bool Installed, RazerDriverInstallOutcome AdmissionFailure);

internal sealed class WindowsRazerDriverInstallPlatform : IRazerDriverInstallPlatform
{
    private const string UsbKeyboard = @"USB\VID_1532&PID_02E0&MI_01\";
    private const string UsbMouse = @"USB\VID_1532&PID_02E0&MI_02\";
    private const string Keyboard = @"HID\VID_1532&PID_02E0&MI_01&COL01\";
    private const string Mouse = @"HID\VID_1532&PID_02E0&MI_02\";
    private const string Virtual = @"RZVIRTUAL\VID_1532&PID_02E0&MI_00&COL03\";
    private const string Control = @"RZCONTROL\VID_1532&PID_02E0&MI_00\";
    private static readonly string[] Controllers = ["RazerAppEngine", "Razer Synapse Service", "RazerSynapseService", "Razer Synapse 3", "Razer Synapse"];


    internal sealed record Device(string Id, Guid Container, string Service, string[] Upper, string[] Lower,
        string[] Ancestors, uint Status, uint Problem, string? InfHash);

    public RazerDriverInstallSnapshot Capture() => Capture(requireElevation: true);

    internal static RazerDriverInstallSnapshot Capture(bool requireElevation)
    {
        using WindowsIdentity identity = WindowsIdentity.GetCurrent();
        if (requireElevation && !new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator))
        {
            return Unavailable();
        }

        using RegistryKey registry = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
        foreach (string key in new[] { @"HARDWARE\DESCRIPTION\System\BIOS", @"SYSTEM\CurrentControlSet\Control\SystemInformation" })
        {
            using RegistryKey? values = registry.OpenSubKey(key);
            if (values is null || values.GetValue("SystemManufacturer") as string != "Razer"
                || values.GetValue("SystemProductName") as string != "Blade 16 - RZ09-0581")
            {
                return Unavailable();
            }
        }
        if (!ProcessIdToSessionId((uint)Environment.ProcessId, out uint session)
            || session == 0 || session == uint.MaxValue || session != WTSGetActiveConsoleSessionId())
        {
            return Unavailable();
        }

        if (!GetSystemPowerStatus(out PowerStatus power))
        {
            throw NativeError();
        }

        if (power.AcLineStatus != 1)
        {
            return Unavailable(RazerDriverInstallOutcome.AcPowerRequired);
        }

        if (HasController())
        {
            return Unavailable(RazerDriverInstallOutcome.RazerSoftwareRunning);
        }

        List<Device> devices = ReadDevices(registry);
        if (!TryAdmitTree(devices, out Guid container))
        {
            return Unavailable();
        }

        string drivers = Path.Combine(Environment.SystemDirectory, "drivers");
        string? commonHash = HashFile(Path.Combine(drivers, "RzCommon.sys"));
        string? deviceHash = HashFile(Path.Combine(drivers, "RzDev_02e0.sys"));
        if ((commonHash is not null && !Equal(commonHash, CommonHash))
            || (deviceHash is not null && !Equal(deviceHash, DeviceHash)))
        {
            return Unavailable();
        }

        var inventory = ReadInventory();
        bool installed = IsInstalled(devices, container, commonHash, deviceHash) && ServiceBinariesMatch(registry);
        return new(true, installed, inventory, JsonSerializer.Serialize(devices));
    }

    private static RazerDriverInstallSnapshot Unavailable(RazerDriverInstallOutcome reason = RazerDriverInstallOutcome.Unavailable)
        => new(false, false, [], string.Empty, reason);

    internal const string CommonHash = "64B72EF9CE409BB17D31096C119B68C4A6A02AB35B7535D763157A4E1A587E59";
    internal const string DeviceHash = "BC1666048D81A8DBA6BBFFDBDC86C2119632FD68EF2BB6D5D417FE3B84744787";

    internal static bool TryAdmitTree(List<Device> devices, out Guid container)
    {
        container = Guid.Empty;
        Device[] keyboards = Matches(devices, UsbKeyboard), mice = Matches(devices, UsbMouse),
            collections = Matches(devices, Keyboard), mouseCollections = Matches(devices, Mouse);
        if (keyboards.Length != 1 || mice.Length != 1 || collections.Length != 1 || mouseCollections.Length != 1)
        {
            return false;
        }

        container = keyboards[0].Container;
        if (container == Guid.Empty || mice[0].Container != container || collections[0].Container != container
            || mouseCollections[0].Container != container
            || !collections[0].Ancestors.Contains(keyboards[0].Id, StringComparer.OrdinalIgnoreCase)
            || !mouseCollections[0].Ancestors.Contains(mice[0].Id, StringComparer.OrdinalIgnoreCase)
            || !keyboards[0].Ancestors.Intersect(mice[0].Ancestors, StringComparer.OrdinalIgnoreCase)
                .Any(id => id.StartsWith(@"USB\VID_1532&PID_02E0\", StringComparison.OrdinalIgnoreCase)))
        {
            return false;
        }
        // The clean inbox stack or the reviewed filters are the only repair targets.
        // Unknown filters and newer driver binaries must not be overwritten by this package.
        return AllowedFilter(keyboards[0].Lower, "RzDev_02e0")
            && keyboards[0].Upper.Length == 0 && collections[0].Lower.Length == 0
            && AllowedFilter(collections[0].Upper, "RzDev_02e0")
            && AllowedFilter(mouseCollections[0].Upper, "RzDev_02e0") && mouseCollections[0].Lower.Length == 0;
    }
    private static bool AllowedFilter(string[] filters, string expected) => filters.Length == 0 || Only(filters, expected);
    private static bool Only(string[] filters, string expected) => filters.Length == 1 && Equal(filters[0], expected);
    private static bool Equal(string? value, string expected) => string.Equals(value, expected, StringComparison.OrdinalIgnoreCase);
    private static Device[] Matches(IEnumerable<Device> devices, string prefix) =>
        devices.Where(device => device.Id.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)).ToArray();

    internal static bool IsInstalled(List<Device> devices, Guid container, string? commonHash, string? deviceHash)
    {
        Device[] usb = Matches(devices, UsbKeyboard), keyboard = Matches(devices, Keyboard), mouse = Matches(devices, Mouse),
            controls = Matches(devices, Control), virtuals = Matches(devices, Virtual);
        if (usb.Length != 1 || keyboard.Length != 1 || mouse.Length != 1 || controls.Length != 1 || virtuals.Length != 1)
        {
            return false;
        }

        Device[] stack = [usb[0], keyboard[0], mouse[0], controls[0], virtuals[0]];
        if (stack.Any(device => device.Container != container || device.Problem != 0 || (device.Status & 8) == 0))
        {
            return false;
        }

        if (!Only(usb[0].Lower, "RzDev_02e0") || !Only(keyboard[0].Upper, "RzDev_02e0")
            || !Only(mouse[0].Upper, "RzDev_02e0") || !Equal(controls[0].Service, "RzCommon")
            || !Equal(virtuals[0].Service, "RzDev_02e0")
            || !controls[0].Ancestors.Any(id => id.StartsWith(UsbMouse, StringComparison.OrdinalIgnoreCase))
            || !virtuals[0].Ancestors.Any(id => id.StartsWith(UsbKeyboard, StringComparison.OrdinalIgnoreCase)))
        {
            return false;
        }

        foreach ((Device device, string part) in new[] { (usb[0], "dkm"), (keyboard[0], "kbd"), (mouse[0], "mou"),
            (controls[0], "common"), (virtuals[0], "vcon") })
        {
            string name = part == "common" ? "rzcommonu.inf" : $"rzdevu_02e0_{part}.inf";
            var pin = RazerKeyboardDriverPackage.Files.Single(file => Equal(Path.GetFileName(file.RelativePath), name));
            if (!Equal(device.InfHash, pin.Sha256))
            {
                return false;
            }
        }
        return Equal(commonHash, CommonHash) && Equal(deviceHash, DeviceHash);
    }

    private static bool ServiceBinariesMatch(RegistryKey registry)
    {
        foreach (string name in new[] { "RzCommon", "RzDev_02e0" })
        {
            using RegistryKey? service = registry.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\" + name);
            string? path = service?.GetValue("ImagePath", null, RegistryValueOptions.DoNotExpandEnvironmentNames) as string;
            if (!Equal(path, @"\SystemRoot\System32\drivers\" + name + ".sys"))
            {
                return false;
            }
        }
        return true;
    }
    public bool HasUnfinishedTransaction()
    {
        try { return RazerDriverInstallJournal.HasUnfinishedTransaction(); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            // Sharing failures include another live installer; unreadable/partial records cannot
            // establish that a previous native operation completed. Never replay either case.
            return true;
        }
    }
    public IRazerDriverInstallJournal OpenJournal() => new RazerDriverInstallJournal();

    public Task<string> AcquireVerifiedAsync(string destination, CancellationToken cancellationToken)
    {
        RazerDriverInstallJournal.EnsureProtectedDirectory(destination);
        return RazerKeyboardDriverPackage.AcquireVerifiedAsync(destination, cancellationToken);
    }

    public async Task<RazerDriverMutationResult> MutateAsync(string root, RazerDriverMutation operation, CancellationToken cancellationToken)
    {
        string fullRoot = Path.GetFullPath(root);
        string allowedRoot = Path.GetFullPath(RazerDriverInstallJournal.Root) + Path.DirectorySeparatorChar;
        if (!fullRoot.StartsWith(allowedRoot, StringComparison.OrdinalIgnoreCase)
            || !RazerKeyboardDriverInstaller.Operations().Contains(operation))
        {
            throw new UnauthorizedAccessException("Unrecognized driver operation.");
        }

        RazerDriverInstallJournal.ValidateProtectedDirectory(fullRoot);
        string inf = Path.GetFullPath(Path.Combine(fullRoot, operation.RelativeInf));
        RazerDriverInstallJournal.RejectReparse(inf);
        if (!inf.StartsWith(fullRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
        {
            throw new UnauthorizedAccessException();
        }

        foreach (var pin in RazerKeyboardDriverPackage.Files)
        {
            string pinnedPath = Path.Combine(fullRoot, pin.RelativePath);
            RazerDriverInstallJournal.RejectReparse(pinnedPath);
            if (!Equal(HashFile(pinnedPath), pin.Sha256))
            {
                throw new InvalidDataException("The verified driver package changed.");
            }
        }
        cancellationToken.ThrowIfCancellationRequested();
        if (!Capture().Admitted)
        {
            throw new InvalidOperationException("Fresh driver-install admission failed.");
        }

        var start = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "pnputil.exe"))
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = fullRoot,
        };
        start.ArgumentList.Add("/add-driver");
        start.ArgumentList.Add(inf);
        if (operation.Bind)
        {
            start.ArgumentList.Add("/install");
        }

        using Process process = Process.Start(start) ?? throw new InvalidOperationException("PnPUtil did not start.");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromMinutes(2));
        Task<string> output = ReadBoundedAsync(process.StandardOutput, timeout.Token);
        Task<string> error = ReadBoundedAsync(process.StandardError, timeout.Token);
        try
        {
            await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            return new(process.ExitCode, await output.ConfigureAwait(false) + "\n" + await error.ConfigureAwait(false));
        }
        catch
        {
            // Do not kill/replay an operation that Windows may have accepted. The retained intent
            // blocks subsequent launches. Observe both readers before releasing their streams.
            await timeout.CancelAsync().ConfigureAwait(false);
            try { await Task.WhenAll(output, error).ConfigureAwait(false); } catch (Exception) { }
            throw;
        }
    }

    private static async Task<string> ReadBoundedAsync(StreamReader reader, CancellationToken cancellationToken)
    {
        var output = new StringBuilder();
        char[] buffer = new char[4096];
        int count;
        while ((count = await reader.ReadAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(false)) != 0)
        {
            // Continue draining after the diagnostic cap so a noisy child cannot deadlock.
            int take = Math.Min(count, 32768 - output.Length);
            if (take > 0)
            {
                output.Append(buffer, 0, take);
            }
        }
        return output.ToString();
    }

    private static IReadOnlyList<RazerDriverInventoryEntry> ReadInventory()
    {
        string infDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "INF");
        var result = new List<RazerDriverInventoryEntry>();
        foreach (string path in Directory.EnumerateFiles(infDirectory, "oem*.inf"))
        {
            if (result.Count >= 8192)
            {
                throw new InvalidDataException("Driver inventory exceeded its bound.");
            }

            string name = Path.GetFileName(path);
            if (!name.StartsWith("oem", StringComparison.OrdinalIgnoreCase)
                || !name.EndsWith(".inf", StringComparison.OrdinalIgnoreCase)
                || !name[3..^4].All(char.IsAsciiDigit))
            {
                throw new InvalidDataException("Invalid published driver name.");
            }

            result.Add(new(name, HashFile(path) ?? throw new IOException("Driver inventory changed during inspection.")));
        }
        return result;
    }

    private static string? HashFile(string path)
    {
        try { using FileStream file = File.OpenRead(path); return Convert.ToHexString(SHA256.HashData(file)); }
        catch (FileNotFoundException) { return null; }
        catch (DirectoryNotFoundException) { return null; }
    }

    private static List<Device> ReadDevices(RegistryKey registry)
    {
        nint set = SetupDiGetClassDevs(0, null, 0, 6);
        if (set == -1)
        {
            throw NativeError();
        }

        try
        {
            var result = new List<Device>();
            for (uint index = 0; index < 8192; index++)
            {
                var device = new DeviceInfo { Size = (uint)Marshal.SizeOf<DeviceInfo>() };
                if (!SetupDiEnumDeviceInfo(set, index, ref device))
                {
                    if (Marshal.GetLastWin32Error() == 259)
                    {
                        return result;
                    }

                    throw NativeError();
                }
                string id = DeviceId(device.Instance);
                if (!id.Contains("VID_1532&PID_02E0", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var key = new PropertyKey(new Guid("8C7ED206-3F8A-4827-B3AB-AE9E1FAEFC6C"), 2);
                byte[] bytes = new byte[16];
                if (!SetupDiGetDeviceProperty(set, ref device, ref key, out uint type, bytes, 16, out uint required, 0)
                    || type != 13 || required != 16)
                {
                    throw NativeError();
                }

                if (CM_Get_DevNode_Status(out uint status, out uint problem, device.Instance, 0) != 0)
                {
                    throw new IOException("Device state unavailable.");
                }

                var ancestors = new List<string>();
                uint current = device.Instance;
                for (int depth = 0; depth < 16; depth++)
                {
                    if (CM_Get_Parent(out current, current, 0) != 0)
                    {
                        break;
                    }

                    ancestors.Add(DeviceId(current));
                }
                string driverKey = Property(set, ref device, 9).SingleOrDefault() ?? string.Empty;
                string? infHash = null;
                if (driverKey.Length > 0)
                {
                    using RegistryKey? driver = registry.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\Class\" + driverKey);
                    string? inf = driver?.GetValue("InfPath") as string;
                    if (inf is not null && Path.GetFileName(inf) == inf)
                    {
                        infHash = HashFile(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "INF", inf));
                    }
                }
                result.Add(new(id, new Guid(bytes), Property(set, ref device, 4).SingleOrDefault() ?? string.Empty,
                    Property(set, ref device, 0x11), Property(set, ref device, 0x12), ancestors.ToArray(), status, problem, infHash));
            }
            throw new IOException("Device inventory exceeded its bound.");
        }
        finally { _ = SetupDiDestroyDeviceInfoList(set); }
    }

    private static string[] Property(nint set, ref DeviceInfo device, uint property)
    {
        byte[] bytes = new byte[8192];
        if (!SetupDiGetDeviceRegistryProperty(set, ref device, property, out uint type, bytes, (uint)bytes.Length, out uint required))
        {
            if (Marshal.GetLastWin32Error() == 13)
            {
                return [];
            }

            throw NativeError();
        }
        if (type is not (1 or 7) || required < 2 || required > bytes.Length || required % 2 != 0)
        {
            throw new IOException("Invalid device property.");
        }

        return Encoding.Unicode.GetString(bytes, 0, (int)required).Split('\0', StringSplitOptions.RemoveEmptyEntries);
    }

    private static string DeviceId(uint instance)
    {
        var id = new StringBuilder(512);
        if (CM_Get_Device_ID(instance, id, id.Capacity, 0) != 0)
        {
            throw new IOException("Device identity unavailable.");
        }

        return id.ToString();
    }

    private static bool HasController()
    {
        Process[] processes = Process.GetProcesses();
        try
        {
            foreach (Process process in processes)
            {
                if (Controllers.Contains(process.ProcessName, StringComparer.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
        }
        finally
        {
            foreach (Process process in processes)
            {
                process.Dispose();
            }
        }
        nint manager = OpenSCManager(null, null, 1);
        if (manager == 0)
        {
            throw NativeError();
        }

        try
        {
            foreach (string name in Controllers)
            {
                nint service = OpenService(manager, name, 4);
                if (service == 0)
                {
                    if (Marshal.GetLastWin32Error() == 1060)
                    {
                        continue;
                    }

                    throw NativeError();
                }
                try
                {
                    if (!QueryServiceStatus(service, out ServiceStatus status))
                    {
                        throw NativeError();
                    }

                    if (status.State != 1)
                    {
                        return true;
                    }
                }
                finally { _ = CloseServiceHandle(service); }
            }
            return false;
        }
        finally { _ = CloseServiceHandle(manager); }
    }

    private static Win32Exception NativeError() => new(Marshal.GetLastWin32Error());
    [StructLayout(LayoutKind.Sequential)] private struct DeviceInfo { internal uint Size; internal Guid Class; internal uint Instance; internal nint Reserved; }
    [StructLayout(LayoutKind.Sequential)] private readonly struct PropertyKey(Guid format, uint id) { internal readonly Guid Format = format; internal readonly uint Id = id; }
    [StructLayout(LayoutKind.Sequential)] private struct PowerStatus { internal byte AcLineStatus, BatteryFlag, BatteryLifePercent, SystemStatusFlag; internal uint BatteryLifeTime, BatteryFullLifeTime; }
    [StructLayout(LayoutKind.Sequential)] private struct ServiceStatus { internal uint Type, State, Accepted, ExitCode, SpecificExitCode, Checkpoint, WaitHint; }
    [DllImport("kernel32.dll")] private static extern uint WTSGetActiveConsoleSessionId();
    [DllImport("kernel32.dll")][return: MarshalAs(UnmanagedType.Bool)] private static extern bool ProcessIdToSessionId(uint process, out uint session);
    [DllImport("kernel32.dll", SetLastError = true)][return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetSystemPowerStatus(out PowerStatus status);
    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern nint SetupDiGetClassDevs(nint guid, string? enumerator, nint parent, uint flags);
    [DllImport("setupapi.dll", SetLastError = true)][return: MarshalAs(UnmanagedType.Bool)] private static extern bool SetupDiEnumDeviceInfo(nint set, uint index, ref DeviceInfo device);
    [DllImport("setupapi.dll", SetLastError = true)][return: MarshalAs(UnmanagedType.Bool)] private static extern bool SetupDiDestroyDeviceInfoList(nint set);
    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)][return: MarshalAs(UnmanagedType.Bool)] private static extern bool SetupDiGetDeviceProperty(nint set, ref DeviceInfo device, ref PropertyKey key, out uint type, byte[] buffer, uint size, out uint required, uint flags);
    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)][return: MarshalAs(UnmanagedType.Bool)] private static extern bool SetupDiGetDeviceRegistryProperty(nint set, ref DeviceInfo device, uint property, out uint type, byte[] buffer, uint size, out uint required);
    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)] private static extern int CM_Get_Device_ID(uint instance, StringBuilder id, int length, uint flags);
    [DllImport("cfgmgr32.dll")] private static extern int CM_Get_Parent(out uint parent, uint instance, uint flags);
    [DllImport("cfgmgr32.dll")] private static extern int CM_Get_DevNode_Status(out uint status, out uint problem, uint instance, uint flags);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern nint OpenSCManager(string? machine, string? database, uint access);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern nint OpenService(nint manager, string name, uint access);
    [DllImport("advapi32.dll", SetLastError = true)][return: MarshalAs(UnmanagedType.Bool)] private static extern bool QueryServiceStatus(nint service, out ServiceStatus status);
    [DllImport("advapi32.dll", SetLastError = true)][return: MarshalAs(UnmanagedType.Bool)] private static extern bool CloseServiceHandle(nint handle);
}
