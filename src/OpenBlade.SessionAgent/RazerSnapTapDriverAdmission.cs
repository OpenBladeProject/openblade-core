using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Win32;
using OpenBlade.Contracts;
using OpenBlade.Platform;
using OpenBlade.Tray;

namespace OpenBlade.SessionAgent;

internal sealed record RazerSnapTapDriverEndpoint(
    string Path, string IdentityToken, RazerSnapTapDriverProfile Profile);

internal sealed record RazerSnapTapControlDevice(
    string DevicePath,
    string InstanceId,
    string ServiceName,
    Guid ContainerId,
    IReadOnlyList<string> Ancestors);

internal sealed record RazerSnapTapKeyboardDevice(
    string InstanceId,
    IReadOnlyList<string> UpperFilters,
    Guid ContainerId,
    IReadOnlyList<string> Ancestors);

internal sealed record RazerSnapTapDriverSnapshot(
    string? BiosManufacturer,
    string? BiosProductName,
    string? SystemManufacturer,
    string? SystemProductName,
    RazerDriverApiPackageEvidence CommonDriver,
    RazerDriverApiPackageEvidence DeviceDriver,
    IReadOnlyList<RazerSnapTapControlDevice> ControlDevices,
    IReadOnlyList<RazerSnapTapKeyboardDevice> KeyboardDevices,
    bool CompetingControllerDetected,
    bool ControllerObservationComplete,
    bool ActiveConsoleSession)
{
    public DiagnosticFailure? ControllerObservationFailure { get; init; }
}

internal enum RazerSnapTapAdmissionFailure
{
    None,
    UnsupportedModel,
    InactiveConsoleSession,
    IncompleteControllerObservation,
    CompetingController,
    DriverSetupRequired,
    DriverMissing,
    ManualDriverSetupRequired,
    DriverPackageUntrusted,
    DriverFilesUnavailable,
    ControlIdentityMismatch,
    KeyboardIdentityMismatch,
    DiscoveryFailed,
}

/// <summary>Fresh read-only admission for exact reviewed driver profiles.</summary>
internal sealed class RazerSnapTapDriverAdmission
{
    private readonly Func<RazerSnapTapDriverSnapshot> capture;
    private readonly Func<RazerDriverInstallationInspection>? inspectInstallation;

    internal RazerSnapTapDriverAdmission()
        : this(WindowsRazerSnapTapDriverDiscovery.Capture, RazerKeyboardDriverInstaller.Inspect)
    {
    }

    internal RazerSnapTapDriverAdmission(Func<RazerSnapTapDriverSnapshot> capture)
        : this(capture, null)
    {
    }

    internal RazerSnapTapDriverAdmission(
        Func<RazerSnapTapDriverSnapshot> capture,
        Func<RazerDriverInstallationInspection>? inspectInstallation)
    {
        this.capture = capture ?? throw new ArgumentNullException(nameof(capture));
        this.inspectInstallation = inspectInstallation;
    }

    internal bool TryGetEndpoint(out RazerSnapTapDriverEndpoint endpoint)
        => TryGetEndpoint(out endpoint, out _);

    internal bool TryGetEndpoint(out RazerSnapTapDriverEndpoint endpoint, out bool driverMissing)
    {
        bool admitted = TryGetEndpointWithFailure(out endpoint, out var failure);
        driverMissing = failure is RazerSnapTapAdmissionFailure.DriverMissing
            or RazerSnapTapAdmissionFailure.ManualDriverSetupRequired;
        return admitted;
    }

    internal bool TryGetEndpointWithFailure(
        out RazerSnapTapDriverEndpoint endpoint, out RazerSnapTapAdmissionFailure failure) =>
        TryGetEndpointWithFailure(out endpoint, out failure, out _);

    internal bool TryGetEndpointWithFailure(
        out RazerSnapTapDriverEndpoint endpoint,
        out RazerSnapTapAdmissionFailure failure,
        out DiagnosticFailure? detail)
    {
        endpoint = null!;
        detail = null;
        try
        {
            RazerSnapTapDriverSnapshot snapshot = capture();
            RazerSnapTapAdmissionFailure evaluatedFailure = Evaluate(snapshot, out endpoint);
            DiagnosticFailure? inspectionFailure = null;
            bool setupAvailable = evaluatedFailure != RazerSnapTapAdmissionFailure.None
                && TryInspectSetupAvailability(snapshot, evaluatedFailure, out inspectionFailure);
            failure = setupAvailable ? RazerSnapTapAdmissionFailure.DriverMissing : evaluatedFailure;
            if (failure != RazerSnapTapAdmissionFailure.None)
            {
                detail = new(
                    "SnapTapAdmission",
                    "ResolveEndpoint",
                    "Evaluate",
                    evaluatedFailure.ToString())
                {
                    Cause = AdmissionFailureCause(snapshot, evaluatedFailure),
                    Related = inspectionFailure is null ? null : [inspectionFailure],
                    Context = System.Text.Json.JsonSerializer.Serialize(new
                    {
                        ReportedFailure = failure.ToString(),
                        InstallerSetupAvailable = setupAvailable,
                        snapshot.BiosProductName,
                        snapshot.SystemProductName,
                        snapshot.ActiveConsoleSession,
                        snapshot.ControllerObservationComplete,
                        snapshot.CompetingControllerDetected,
                        ControlCandidates = snapshot.ControlDevices.Count,
                        KeyboardCandidates = snapshot.KeyboardDevices.Count,
                        CommonInstalled = snapshot.CommonDriver.Installed,
                        CommonTrusted = snapshot.CommonDriver.Trusted,
                        DeviceInstalled = snapshot.DeviceDriver.Installed,
                        DeviceTrusted = snapshot.DeviceDriver.Trusted,
                    }),
                };
                detail = detail.LimitDetails();
                Trace.TraceWarning("SnapTap driver admission rejected: {0}; host={1}.",
                    failure, snapshot.BiosProductName);
            }
            return failure == RazerSnapTapAdmissionFailure.None;
        }
        catch (Exception exception) when (exception is
            Win32Exception or InvalidOperationException or IOException or InvalidDataException
            or UnauthorizedAccessException or CryptographicException
            or OverflowException)
        {
            failure = RazerSnapTapAdmissionFailure.DiscoveryFailed;
            detail = DiagnosticFailure.FromException(
                "SnapTapAdmission",
                "ResolveEndpoint",
                "Capture",
                "DiscoveryFailed",
                exception);
            Trace.TraceWarning("SnapTap driver discovery failed: {0}; HRESULT=0x{1:X8}.",
                exception.GetType().Name, exception.HResult);
            return false;
        }
    }

    private bool TryInspectSetupAvailability(
        RazerSnapTapDriverSnapshot snapshot,
        RazerSnapTapAdmissionFailure failure,
        out DiagnosticFailure? inspectionFailure)
    {
        inspectionFailure = null;
        RazerSnapTapDriverProfile? profile = RazerSnapTapDriverProfiles.Find(
            snapshot.BiosManufacturer, snapshot.BiosProductName,
            snapshot.SystemManufacturer, snapshot.SystemProductName);
        if (inspectInstallation is null
            || profile?.AutomaticInstallationAvailable != true
            || !CanInspectSetupAvailability(snapshot, profile, failure))
        {
            return false;
        }

        try
        {
            RazerDriverInstallationInspection inspection = inspectInstallation();
            if (inspection.Admitted)
            {
                return !inspection.Installed;
            }

            inspectionFailure = new DiagnosticFailure(
                "SnapTapAdmission",
                "InspectDriverSetup",
                "InstallerAdmission",
                inspection.AdmissionFailure.ToString())
            {
                Context = System.Text.Json.JsonSerializer.Serialize(new
                {
                    inspection.Admitted,
                    inspection.Installed,
                    Outcome = inspection.AdmissionFailure.ToString(),
                }),
            }.LimitDetails();
            return false;
        }
        catch (Exception exception) when (exception is
            Win32Exception or InvalidOperationException or IOException or InvalidDataException
            or UnauthorizedAccessException or CryptographicException or OverflowException)
        {
            inspectionFailure = DiagnosticFailure.FromException(
                "SnapTapAdmission",
                "InspectDriverSetup",
                "InstallerAdmission",
                "InspectionFailed",
                exception);
            Trace.TraceWarning("SnapTap driver setup inspection failed: {0}; HRESULT=0x{1:X8}.",
                exception.GetType().Name, exception.HResult);
            return false;
        }
    }

    private static bool CanInspectSetupAvailability(
        RazerSnapTapDriverSnapshot snapshot,
        RazerSnapTapDriverProfile profile,
        RazerSnapTapAdmissionFailure failure) => failure switch
        {
            RazerSnapTapAdmissionFailure.DriverSetupRequired
                or RazerSnapTapAdmissionFailure.ControlIdentityMismatch
                or RazerSnapTapAdmissionFailure.KeyboardIdentityMismatch => true,
            RazerSnapTapAdmissionFailure.DriverPackageUntrusted
                or RazerSnapTapAdmissionFailure.DriverFilesUnavailable =>
                IsKnownRepairablePackageState(snapshot.CommonDriver, "RzCommon", common: true)
                && IsKnownRepairablePackageState(snapshot.DeviceDriver, profile.DeviceService, common: false),
            _ => false,
        };

    private static bool IsKnownRepairablePackageState(
        RazerDriverApiPackageEvidence evidence,
        string expectedOperation,
        bool common)
    {
        if (!evidence.Installed || Trusted(evidence))
        {
            return true;
        }
        if (evidence.FailureDetail is not { } failure
            || !failure.IsValid()
            || !string.Equals(failure.Component, "DriverPackage", StringComparison.Ordinal)
            || !string.Equals(failure.Operation, expectedOperation, StringComparison.Ordinal))
        {
            return false;
        }
        if (common)
        {
            return string.Equals(failure.Stage, "EndpointBinding", StringComparison.Ordinal)
                && string.Equals(failure.Reason, "EndpointMissing", StringComparison.Ordinal);
        }
        if (!string.Equals(failure.Stage, "InfContract", StringComparison.Ordinal)
            || !string.Equals(failure.Reason, "ProviderMismatch", StringComparison.Ordinal)
            || string.IsNullOrWhiteSpace(failure.Context))
        {
            return false;
        }

        try
        {
            using System.Text.Json.JsonDocument context = System.Text.Json.JsonDocument.Parse(failure.Context);
            System.Text.Json.JsonProperty[] publishedInfNames = context.RootElement.ValueKind
                == System.Text.Json.JsonValueKind.Object
                ? context.RootElement.EnumerateObject()
                    .Where(property => string.Equals(
                        property.Name, "PublishedInfName", StringComparison.Ordinal))
                    .ToArray()
                : [];
            return publishedInfNames.Length == 1
                && publishedInfNames[0].Value.ValueKind == System.Text.Json.JsonValueKind.String
                && string.Equals(
                    publishedInfNames[0].Value.GetString(), "keyboard.inf", StringComparison.Ordinal);
        }
        catch (System.Text.Json.JsonException)
        {
            return false;
        }
    }

    internal bool IsCurrent(RazerSnapTapDriverEndpoint endpoint)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        return TryGetEndpoint(out RazerSnapTapDriverEndpoint current)
            && string.Equals(current.Path, endpoint.Path, StringComparison.OrdinalIgnoreCase)
            && string.Equals(current.IdentityToken, endpoint.IdentityToken, StringComparison.Ordinal);
    }

    internal static bool TryAdmit(RazerSnapTapDriverSnapshot snapshot, out RazerSnapTapDriverEndpoint endpoint) =>
        Evaluate(snapshot, out endpoint) == RazerSnapTapAdmissionFailure.None;

    internal static RazerSnapTapAdmissionFailure Evaluate(
        RazerSnapTapDriverSnapshot snapshot, out RazerSnapTapDriverEndpoint endpoint)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        endpoint = null!;
        RazerSnapTapDriverProfile? profile = RazerSnapTapDriverProfiles.Find(
            snapshot.BiosManufacturer, snapshot.BiosProductName,
            snapshot.SystemManufacturer, snapshot.SystemProductName);
        if (profile is null)
        {
            return RazerSnapTapAdmissionFailure.UnsupportedModel;
        }
        if (!snapshot.ActiveConsoleSession)
        {
            return RazerSnapTapAdmissionFailure.InactiveConsoleSession;
        }
        if (!snapshot.ControllerObservationComplete)
        {
            return RazerSnapTapAdmissionFailure.IncompleteControllerObservation;
        }
        if (snapshot.CompetingControllerDetected)
        {
            return RazerSnapTapAdmissionFailure.CompetingController;
        }
        if (IsDriverMissing(snapshot, profile))
        {
            return profile.AutomaticInstallationAvailable
                ? RazerSnapTapAdmissionFailure.DriverSetupRequired
                : RazerSnapTapAdmissionFailure.ManualDriverSetupRequired;
        }
        if (!Trusted(snapshot.CommonDriver)
            || !Trusted(snapshot.DeviceDriver))
        {
            return !snapshot.CommonDriver.Installed || !snapshot.DeviceDriver.Installed
                ? RazerSnapTapAdmissionFailure.DriverFilesUnavailable
                : RazerSnapTapAdmissionFailure.DriverPackageUntrusted;
        }

        RazerSnapTapControlDevice[] controls = snapshot.ControlDevices
            .Where(control =>
                control.ContainerId != Guid.Empty
                && control.InstanceId.StartsWith(profile.ControlInstancePrefix, StringComparison.OrdinalIgnoreCase)
                && string.Equals(control.ServiceName, "RzCommon", StringComparison.Ordinal)
                && HasAncestor(control.Ancestors, profile.ControlAncestorPrefix))
            .ToArray();
        if (controls.Length != 1 || string.IsNullOrWhiteSpace(controls[0].DevicePath))
        {
            return RazerSnapTapAdmissionFailure.ControlIdentityMismatch;
        }
        RazerSnapTapControlDevice control = controls[0];
        RazerSnapTapKeyboardDevice[] keyboards = snapshot.KeyboardDevices
            .Where(keyboard =>
                keyboard.ContainerId == control.ContainerId
                && keyboard.InstanceId.StartsWith(profile.KeyboardInstancePrefix, StringComparison.OrdinalIgnoreCase)
                && keyboard.UpperFilters.Count == 1
                && string.Equals(keyboard.UpperFilters[0], profile.DeviceService, StringComparison.OrdinalIgnoreCase)
                && HasAncestor(keyboard.Ancestors, profile.KeyboardAncestorPrefix))
            .ToArray();
        if (keyboards.Length != 1)
        {
            return RazerSnapTapAdmissionFailure.KeyboardIdentityMismatch;
        }
        string identity = string.Join('\n',
            "Razer", profile.ProductName, snapshot.CommonDriver.Identity!, snapshot.DeviceDriver.Identity!,
            control.DevicePath.ToUpperInvariant(), control.InstanceId.ToUpperInvariant(),
            control.ContainerId.ToString("D"), keyboards[0].InstanceId.ToUpperInvariant());
        endpoint = new RazerSnapTapDriverEndpoint(control.DevicePath,
            Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity))), profile);
        return RazerSnapTapAdmissionFailure.None;
    }

    private static DiagnosticFailure? PackageFailure(RazerSnapTapDriverSnapshot snapshot)
    {
        // Retain actual failures in common/filter order; diagnostics grant no admission.
        DiagnosticFailure? common = !Trusted(snapshot.CommonDriver) ? snapshot.CommonDriver.FailureDetail : null;
        DiagnosticFailure? device = !Trusted(snapshot.DeviceDriver) ? snapshot.DeviceDriver.FailureDetail : null;
        return common is null ? device : device is null ? common : common with
        {
            Related = [.. common.Related ?? [], device],
        };
    }

    private static DiagnosticFailure? AdmissionFailureCause(
        RazerSnapTapDriverSnapshot snapshot,
        RazerSnapTapAdmissionFailure failure)
    {
        DiagnosticFailure? package = PackageFailure(snapshot);
        if (failure != RazerSnapTapAdmissionFailure.IncompleteControllerObservation
            || snapshot.ControllerObservationFailure is not { } observation)
        {
            return package;
        }

        return package is null ? observation : observation with
        {
            Related = [.. observation.Related ?? [], package],
        };
    }

    private static bool Trusted(RazerDriverApiPackageEvidence evidence) =>
        evidence.Installed && evidence.Trusted && !string.IsNullOrWhiteSpace(evidence.Identity);

    private static bool IsDriverMissing(RazerSnapTapDriverSnapshot snapshot, RazerSnapTapDriverProfile profile)
    {
        bool commonMissing = !snapshot.CommonDriver.Installed;
        bool deviceMissing = !snapshot.DeviceDriver.Installed;
        if ((!commonMissing && !Trusted(snapshot.CommonDriver))
            || (!deviceMissing && !Trusted(snapshot.DeviceDriver))
            || (!commonMissing && !deviceMissing))
        {
            return false;
        }
        RazerSnapTapKeyboardDevice[] keyboards = snapshot.KeyboardDevices
            .Where(keyboard => keyboard.InstanceId.StartsWith(
                profile.KeyboardInstancePrefix, StringComparison.OrdinalIgnoreCase))
            .ToArray();
        return keyboards.Length == 1 && keyboards[0].ContainerId != Guid.Empty
            && HasAncestor(keyboards[0].Ancestors, profile.KeyboardAncestorPrefix);
    }

    private static bool HasAncestor(IEnumerable<string> ancestors, string prefix) =>
        ancestors.Any(ancestor => ancestor.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
}
internal static class WindowsRazerSnapTapDriverDiscovery
{
    internal static readonly Guid ControlInterfaceGuid = new("E3BE005D-D130-4910-88FF-09AE02F680E9");
    // The reviewed model-specific Kbd INFs install MI_01/COL01 in Class=Keyboard.
    // The generic HID setup class does not enumerate this keyboard collection.
    private static readonly Guid KeyboardClassGuid = new("4D36E96B-E325-11CE-BFC1-08002BE10318");
    private static readonly DevicePropertyKey ContainerIdKey = new(
        new Guid("8C7ED206-3F8A-4827-B3AB-AE9E1FAEFC6C"), 2);
    private static readonly HashSet<string> CompetingControllerNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "RazerAppEngine",
        "Razer Synapse Service",
        "RazerSynapseService",
    };

    private const string BiosKey = @"HARDWARE\DESCRIPTION\System\BIOS";
    private const string SystemInformationKey = @"SYSTEM\CurrentControlSet\Control\SystemInformation";
    private const uint DigcfPresent = 0x2;
    private const uint DigcfDeviceInterface = 0x10;
    private const uint SpdrpService = 0x4;
    private const uint SpdrpUpperFilters = 0x11;
    private const uint DevpropTypeGuid = 0xD;
    private const int ErrorInvalidData = 13;
    private const int ErrorInsufficientBuffer = 122;
    private const int ErrorNoMoreItems = 259;
    private const int CrSuccess = 0;
    private static readonly nint InvalidHandle = new(-1);

    internal static RazerSnapTapDriverSnapshot Capture()
    {
        using RegistryKey localMachine = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
        (bool competing, bool complete, DiagnosticFailure? observationFailure) = ObserveControllers();
        string? biosManufacturer = ReadRegistryString(localMachine, BiosKey, "SystemManufacturer");
        string? biosProduct = ReadRegistryString(localMachine, BiosKey, "SystemProductName");
        string? systemManufacturer = ReadRegistryString(localMachine, SystemInformationKey, "SystemManufacturer");
        string? systemProduct = ReadRegistryString(localMachine, SystemInformationKey, "SystemProductName");
        RazerSnapTapDriverProfile? profile = RazerSnapTapDriverProfiles.Find(
            biosManufacturer, biosProduct, systemManufacturer, systemProduct);
        IReadOnlyList<RazerSnapTapControlDevice> controls = profile is null ? [] : ReadControls();
        IReadOnlyList<RazerSnapTapKeyboardDevice> keyboards = profile is null ? [] : ReadKeyboards(profile);
        RazerSnapTapControlDevice[] matchingControls = controls.Where(control =>
            control.InstanceId.StartsWith(profile!.ControlInstancePrefix, StringComparison.OrdinalIgnoreCase)
            && string.Equals(control.ServiceName, "RzCommon", StringComparison.Ordinal)).ToArray();
        string? controlInstance = matchingControls.Length == 1 ? matchingControls[0].InstanceId : null;
        string? keyboardInstance = keyboards.Count == 1 ? keyboards[0].InstanceId : null;
        return new RazerSnapTapDriverSnapshot(
            biosManufacturer, biosProduct, systemManufacturer, systemProduct,
            profile is null ? RazerDriverApiPackageEvidence.Rejected
                : WindowsRazerDriverApiDiscovery.Capture(controlInstance, "RzCommon", @"RAZER\DeviceContol"),
            profile is null ? RazerDriverApiPackageEvidence.Rejected
                : WindowsRazerDriverApiDiscovery.Capture(keyboardInstance, profile.DeviceService, profile.KeyboardHardwareId),
            controls, keyboards, competing, complete, HasActiveConsoleSession())
        {
            ControllerObservationFailure = observationFailure,
        };
    }
    internal static bool HasActiveConsoleSession() =>
        ProcessIdToSessionId((uint)Environment.ProcessId, out uint current)
        && IsActiveConsoleSession(current, WTSGetActiveConsoleSessionId());

    internal static bool IsActiveConsoleSession(uint current, uint active) =>
        current != 0 && current != uint.MaxValue && current == active;

    private static string? ReadRegistryString(RegistryKey root, string path, string name)
    {
        using RegistryKey? key = root.OpenSubKey(path, writable: false);
        return key?.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames) as string;
    }

    internal static (bool Detected, bool Complete, DiagnosticFailure? Failure) ObserveControllers(
        Func<Process[]>? enumerateProcesses = null)
    {
        Process[] processes;
        try
        {
            processes = (enumerateProcesses ?? Process.GetProcesses)();
        }
        catch (Exception exception) when (exception is Win32Exception or InvalidOperationException)
        {
            return (false, false, ControllerObservationFailure(
                "EnumerateProcesses",
                "ProcessEnumerationFailed",
                exception));
        }

        bool complete = true;
        bool detected = false;
        DiagnosticFailure? failure = null;
        try
        {
            foreach (Process process in processes)
            {
                try
                {
                    detected |= CompetingControllerNames.Contains(process.ProcessName);
                }
                catch (Exception exception) when (exception is
                    Win32Exception or InvalidOperationException or NotSupportedException)
                {
                    complete = false;
                    failure ??= ControllerObservationFailure(
                        "ReadProcessName",
                        "ProcessInspectionFailed",
                        exception);
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
        return (detected, complete, failure);
    }

    private static DiagnosticFailure ControllerObservationFailure(
        string stage,
        string reason,
        Exception exception) => DiagnosticFailure.FromException(
            "SnapTapAdmission",
            "ObserveControllers",
            stage,
            reason,
            exception);

    private static IReadOnlyList<RazerSnapTapControlDevice> ReadControls()
    {
        Guid classGuid = ControlInterfaceGuid;
        nint set = SetupDiGetClassDevs(ref classGuid, null, 0, DigcfPresent | DigcfDeviceInterface);
        if (set == InvalidHandle)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error());
        }
        try
        {
            List<RazerSnapTapControlDevice> result = [];
            for (uint index = 0; ; index++)
            {
                DeviceInterfaceData interfaceData = DeviceInterfaceData.Create();
                if (!SetupDiEnumDeviceInterfaces(set, 0, ref classGuid, index, ref interfaceData))
                {
                    if (Marshal.GetLastWin32Error() == ErrorNoMoreItems)
                    {
                        break;
                    }
                    throw new Win32Exception(Marshal.GetLastWin32Error());
                }
                DeviceInfoData info = DeviceInfoData.Create();
                string path = GetDevicePath(set, ref interfaceData, ref info);
                result.Add(new RazerSnapTapControlDevice(path, GetDeviceId(info.DeviceInstance),
                    GetRegistryStrings(set, ref info, SpdrpService).Single(),
                    GetContainerId(set, ref info), GetAncestors(info.DeviceInstance)));
            }
            return result;
        }
        finally
        {
            _ = SetupDiDestroyDeviceInfoList(set);
        }
    }

    private static IReadOnlyList<RazerSnapTapKeyboardDevice> ReadKeyboards(RazerSnapTapDriverProfile profile)
    {
        Guid classGuid = KeyboardClassGuid;
        nint set = SetupDiGetClassDevs(ref classGuid, null, 0, DigcfPresent);
        if (set == InvalidHandle)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error());
        }
        try
        {
            List<RazerSnapTapKeyboardDevice> result = [];
            for (uint index = 0; ; index++)
            {
                DeviceInfoData info = DeviceInfoData.Create();
                if (!SetupDiEnumDeviceInfo(set, index, ref info))
                {
                    if (Marshal.GetLastWin32Error() == ErrorNoMoreItems)
                    {
                        break;
                    }
                    throw new Win32Exception(Marshal.GetLastWin32Error());
                }
                string id = GetDeviceId(info.DeviceInstance);
                if (!id.StartsWith(profile.KeyboardInstancePrefix, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }
                result.Add(new RazerSnapTapKeyboardDevice(id,
                    GetRegistryStrings(set, ref info, SpdrpUpperFilters, allowMissing: true),
                    GetContainerId(set, ref info), GetAncestors(info.DeviceInstance)));
            }
            return result;
        }
        finally
        {
            _ = SetupDiDestroyDeviceInfoList(set);
        }
    }

    private static IReadOnlyList<string> GetAncestors(uint instance)
    {
        List<string> result = [];
        uint current = instance;
        for (int depth = 0; depth < 16; depth++)
        {
            result.Add(GetDeviceId(current));
            if (CM_Get_Parent(out current, current, 0) != CrSuccess)
            {
                break;
            }
        }
        return result;
    }

    private static string GetDeviceId(uint instance)
    {
        int status = CM_Get_Device_ID_Size(out uint size, instance, 0);
        if (status != CrSuccess)
        {
            throw new InvalidOperationException($"CM_Get_Device_ID_Size failed: 0x{status:X8}.");
        }
        char[] buffer = new char[checked((int)size + 1)];
        status = CM_Get_Device_ID(instance, buffer, (uint)buffer.Length, 0);
        if (status != CrSuccess)
        {
            throw new InvalidOperationException($"CM_Get_Device_ID failed: 0x{status:X8}.");
        }
        int end = Array.IndexOf(buffer, '\0');
        return new string(buffer, 0, end < 0 ? buffer.Length : end);
    }

    private static IReadOnlyList<string> GetRegistryStrings(
        nint set,
        ref DeviceInfoData info,
        uint property,
        bool allowMissing = false)
    {
        _ = SetupDiGetDeviceRegistryProperty(set, ref info, property, out _, null, 0, out uint required);
        int error = Marshal.GetLastWin32Error();
        if (allowMissing && required == 0 && error == ErrorInvalidData)
        {
            return [];
        }
        if (required == 0 || error != ErrorInsufficientBuffer)
        {
            throw new Win32Exception(error);
        }
        byte[] buffer = new byte[required];
        if (!SetupDiGetDeviceRegistryProperty(set, ref info, property, out _, buffer, (uint)buffer.Length, out _))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error());
        }
        return Encoding.Unicode.GetString(buffer).Split('\0', StringSplitOptions.RemoveEmptyEntries);
    }

    private static Guid GetContainerId(nint set, ref DeviceInfoData info)
    {
        DevicePropertyKey key = ContainerIdKey;
        byte[] buffer = new byte[16];
        if (!SetupDiGetDeviceProperty(set, ref info, ref key, out uint type,
                buffer, (uint)buffer.Length, out uint required, 0)
            || type != DevpropTypeGuid || required != 16)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error());
        }
        return new Guid(buffer);
    }

    private static string GetDevicePath(nint set, ref DeviceInterfaceData interfaceData, ref DeviceInfoData info)
    {
        _ = SetupDiGetDeviceInterfaceDetail(set, ref interfaceData, 0, 0, out uint required, 0);
        if (required == 0)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error());
        }
        nint detail = Marshal.AllocHGlobal(checked((int)required));
        try
        {
            Marshal.WriteInt32(detail, IntPtr.Size == 8 ? 8 : 6);
            if (!SetupDiGetDeviceInterfaceDetail(set, ref interfaceData, detail, required, out _, ref info))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error());
            }
            return Marshal.PtrToStringUni(IntPtr.Add(detail, 4))
                ?? throw new InvalidOperationException("The Razer control path is unavailable.");
        }
        finally
        {
            Marshal.FreeHGlobal(detail);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DevicePropertyKey
    {
        internal Guid FormatId;
        internal uint PropertyId;
        internal DevicePropertyKey(Guid formatId, uint propertyId) => (FormatId, PropertyId) = (formatId, propertyId);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DeviceInfoData
    {
        internal uint Size;
        internal Guid ClassGuid;
        internal uint DeviceInstance;
        internal nint Reserved;
        internal static DeviceInfoData Create() => new() { Size = (uint)Marshal.SizeOf<DeviceInfoData>() };
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DeviceInterfaceData
    {
        internal uint Size;
        internal Guid InterfaceClassGuid;
        internal uint Flags;
        internal nint Reserved;
        internal static DeviceInterfaceData Create() => new() { Size = (uint)Marshal.SizeOf<DeviceInterfaceData>() };
    }

    [DllImport("kernel32.dll")]
    private static extern uint WTSGetActiveConsoleSessionId();
    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ProcessIdToSessionId(uint id, out uint session);
    [DllImport("setupapi.dll", EntryPoint = "SetupDiGetClassDevsW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern nint SetupDiGetClassDevs(ref Guid classGuid, string? enumerator, nint parent, uint flags);
    [DllImport("setupapi.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetupDiEnumDeviceInterfaces(nint set, nint info, ref Guid guid, uint index, ref DeviceInterfaceData data);
    [DllImport("setupapi.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetupDiEnumDeviceInfo(nint set, uint index, ref DeviceInfoData data);
    [DllImport("setupapi.dll", EntryPoint = "SetupDiGetDeviceInterfaceDetailW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetupDiGetDeviceInterfaceDetail(nint set, ref DeviceInterfaceData data, nint detail, uint size, out uint required, ref DeviceInfoData info);
    [DllImport("setupapi.dll", EntryPoint = "SetupDiGetDeviceInterfaceDetailW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetupDiGetDeviceInterfaceDetail(nint set, ref DeviceInterfaceData data, nint detail, uint size, out uint required, nint info);
    [DllImport("setupapi.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetupDiDestroyDeviceInfoList(nint set);
    [DllImport("setupapi.dll", EntryPoint = "SetupDiGetDeviceRegistryPropertyW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetupDiGetDeviceRegistryProperty(nint set, ref DeviceInfoData info, uint property, out uint type, byte[]? buffer, uint size, out uint required);
    [DllImport("setupapi.dll", EntryPoint = "SetupDiGetDevicePropertyW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetupDiGetDeviceProperty(nint set, ref DeviceInfoData info, ref DevicePropertyKey key, out uint type, byte[] buffer, uint size, out uint required, uint flags);
    [DllImport("cfgmgr32.dll", EntryPoint = "CM_Get_Parent")]
    private static extern int CM_Get_Parent(out uint parent, uint instance, uint flags);
    [DllImport("cfgmgr32.dll", EntryPoint = "CM_Get_Device_ID_Size")]
    private static extern int CM_Get_Device_ID_Size(out uint size, uint instance, uint flags);
    [DllImport("cfgmgr32.dll", EntryPoint = "CM_Get_Device_IDW", CharSet = CharSet.Unicode)]
    private static extern int CM_Get_Device_ID(uint instance, char[] buffer, uint length, uint flags);
}
