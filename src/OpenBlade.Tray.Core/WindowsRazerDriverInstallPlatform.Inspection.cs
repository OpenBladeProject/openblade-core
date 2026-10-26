using System.ComponentModel;
using System.Security.Principal;
using System.Text.Json;
using Microsoft.Win32;
using OpenBlade.Contracts;
using OpenBlade.Platform;

namespace OpenBlade.Tray;

internal sealed partial class WindowsRazerDriverInstallPlatform
{
    internal static IReadOnlyList<string> ReviewedWindowsUpdateHardwareIds(RazerKeyboardDriverRecipe recipe)
    {
        RequireKnownRecipe(recipe);
        // Exact signed INF model matches reviewed in snap-tap-vendor-artifacts.md,
        // snap-tap-driver-review.md and the separately reviewed 0255 .66 CAB recipe.
        return
        [
            $@"USB\VID_1532&PID_{recipe.ProductId}&MI_01",
            $@"HID\VID_1532&PID_{recipe.ProductId}&MI_01&COL01",
            $@"HID\VID_1532&PID_{recipe.ProductId}&MI_02",
            $@"RZVIRTUAL\VID_1532&PID_{recipe.ProductId}&MI_00&COL03",
            @"RAZER\DeviceContol",
        ];
    }

    internal static RazerDriverInstallSnapshot Capture(bool requireElevation, bool windowsUpdate = false)
    {
        string stage = "Elevation";
        RazerKeyboardDriverRecipe? recipe = null;
        try
        {
            using WindowsIdentity identity = WindowsIdentity.GetCurrent();
            if (requireElevation && !new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator))
            {
                return Block("ElevationRequired");
            }
            stage = "HostIdentity";
            using RegistryKey registry = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
            using RegistryKey? bios = registry.OpenSubKey(@"HARDWARE\DESCRIPTION\System\BIOS");
            using RegistryKey? system = registry.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\SystemInformation");
            recipe = RazerKeyboardDriverRecipes.FindHost(
                bios?.GetValue("SystemManufacturer") as string, bios?.GetValue("SystemProductName") as string,
                system?.GetValue("SystemManufacturer") as string, system?.GetValue("SystemProductName") as string);
            if (recipe is null) { return Block("ExactHostNotAdmitted"); }
            stage = "Session";
            if (!ProcessIdToSessionId((uint)Environment.ProcessId, out uint session)
                || session == 0 || session == uint.MaxValue || session != WTSGetActiveConsoleSessionId())
            {
                return Block("ActiveConsoleSessionRequired");
            }
            stage = "Power";
            if (!GetSystemPowerStatus(out PowerStatus power)) { throw NativeError(); }
            if (power.AcLineStatus != 1) { return Block("AcPowerRequired", RazerDriverInstallOutcome.AcPowerRequired); }
            stage = "CompetingController";
            if (HasController()) { return Block("RazerSoftwareRunning", RazerDriverInstallOutcome.RazerSoftwareRunning); }
            stage = "DeviceEnumeration";
            List<Device> devices = ReadDevices(registry, recipe);
            stage = "Topology";
            if (!TryAdmitTree(devices, recipe, out Guid container, out DiagnosticFailure? topologyFailure))
            {
                return Unavailable(topologyFailure!);
            }
            stage = "ServiceBinding";
            foreach (string name in new[] { "RzCommon", recipe.DeviceService })
            {
                using RegistryKey? service = registry.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\" + name);
                int? type = service?.GetValue("Type") is int value ? value : null;
                string? image = service?.GetValue("ImagePath", null, RegistryValueOptions.DoNotExpandEnvironmentNames) as string;
                if (!AdmitServiceBinding(service is not null, type, image, name))
                {
                    return Block("ServiceImageOrTypeMismatch", devices: devices,
                        extra: new
                        {
                            Service = name,
                            ActualType = type,
                            ActualImagePath = image,
                            ExpectedType = 1,
                            ExpectedImagePath = @"\SystemRoot\System32\drivers\" + name + ".sys"
                        });
                }
            }
            stage = "ExistingImages";
            string drivers = Path.Combine(Environment.SystemDirectory, "drivers");
            string? commonHash = HashFile(Path.Combine(drivers, "RzCommon.sys"));
            string? deviceHash = HashFile(Path.Combine(drivers, recipe.DeviceService + ".sys"));
            if (!windowsUpdate && ((commonHash is not null && !Equal(commonHash, recipe.CommonHash))
                || (deviceHash is not null && !Equal(deviceHash, recipe.DeviceHash))))
            {
                return Block("ExistingImageDiffersFromPinnedPackage", devices: devices,
                    extra: new
                    {
                        CommonObserved = commonHash,
                        CommonExpected = recipe.CommonHash,
                        DeviceObserved = deviceHash,
                        DeviceExpected = recipe.DeviceHash
                    });
            }
            stage = "InstalledReadback";
            bool installed = IsInstalled(devices, container, commonHash, deviceHash, recipe, requirePinnedFiles: !windowsUpdate)
                && ServiceBinariesMatch(registry, recipe);
            if (windowsUpdate)
            {
                RazerDriverInstallationInspection packageInspection = InspectWindowsUpdatePackages(
                    devices, recipe, commonHash, deviceHash, installed);
                if (!packageInspection.Admitted) { return Unavailable(packageInspection.FailureDetail!); }
                installed = packageInspection.Installed;
            }
            stage = "DriverStoreInventory";
            var inventory = ReadInventory();
            string[] reviewed = ReviewedWindowsUpdateHardwareIds(recipe).ToArray();
            return new(true, installed, inventory, JsonSerializer.Serialize(devices))
            {
                RecipeId = recipe.Id,
                WindowsUpdateTargets = devices.Where(device => device.HardwareIds.Any(id => reviewed.Contains(id, StringComparer.OrdinalIgnoreCase)))
                    .Select(device => new RazerKeyboardWindowsUpdateTarget(device.Id, device.HardwareIds.ToArray())).ToArray(),
            };
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or UnauthorizedAccessException
            or InvalidOperationException or Win32Exception or System.Security.Cryptography.CryptographicException
            or JsonException or OverflowException)
        {
            return Unavailable(DiagnosticFailure.FromException("RazerDriverInstall", "Inspect", stage,
                "InspectionFailed", exception) with
            { Context = JsonSerializer.Serialize(new { RecipeId = recipe?.Id, WindowsUpdate = windowsUpdate }) });
        }

        RazerDriverInstallSnapshot Block(string reason, RazerDriverInstallOutcome outcome = RazerDriverInstallOutcome.Unavailable,
            List<Device>? devices = null, object? extra = null) =>
            Unavailable(InspectionFailure(stage, reason, recipe, devices, extra), outcome);
    }

    private static RazerDriverInstallSnapshot Unavailable(DiagnosticFailure failure,
        RazerDriverInstallOutcome outcome = RazerDriverInstallOutcome.Unavailable)
    {
        failure = failure.LimitDetails();
        DiagnosticTrace.TraceFailure(failure);
        return new(false, false, [], string.Empty, outcome) { FailureDetail = failure };
    }

    internal static DiagnosticFailure InspectionFailure(string stage, string reason, RazerKeyboardDriverRecipe? recipe,
        IReadOnlyList<Device>? devices = null, object? extra = null) => new DiagnosticFailure("RazerDriverInstall", "Inspect", stage, reason)
        {
            Context = JsonSerializer.Serialize(new
            {
                RecipeId = recipe?.Id,
                ProductId = recipe?.ProductId,
                DeviceCount = devices?.Count,
                // No unique instance suffix, container GUID or serial belongs in ordinary logs.
                Devices = devices?.Take(16).Select(device => new
                {
                    HardwareIdentity = string.Join('\\', device.Id.Split('\\').Take(2)),
                    device.Service,
                    device.Upper,
                    device.Lower,
                    device.Status,
                    device.Problem,
                }).ToArray(),
                DevicesTruncated = devices?.Count > 16,
                Extra = extra,
            }),
        }.LimitDetails();

    internal static bool ShouldInspectDevice(string instanceId, IReadOnlyList<string> hardwareIds, RazerKeyboardDriverRecipe recipe) =>
        instanceId.Contains($"VID_1532&PID_{recipe.ProductId}", StringComparison.OrdinalIgnoreCase)
        || hardwareIds.Any(id => ReviewedWindowsUpdateHardwareIds(recipe).Contains(id, StringComparer.OrdinalIgnoreCase));

    internal static bool WindowsUpdateTargetsAdmitted(IReadOnlyList<Device> devices, RazerKeyboardDriverRecipe recipe, Guid container)
    {
        string[] ids = ReviewedWindowsUpdateHardwareIds(recipe).ToArray();
        foreach (Device device in devices)
        {
            for (int role = 0; role < ids.Length; role++)
            {
                if (!device.HardwareIds.Contains(ids[role], StringComparer.OrdinalIgnoreCase)) { continue; }
                string prefix = role == 4 ? $@"RZCONTROL\VID_1532&PID_{recipe.ProductId}&MI_00\" : ids[role] + "\\";
                string ancestor = $@"USB\VID_1532&PID_{recipe.ProductId}&MI_{(role is 2 or 4 ? "02" : "01")}\";
                if (device.Container != container || !device.Id.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
                    || role != 0 && !device.Ancestors.Any(value => value.StartsWith(ancestor, StringComparison.OrdinalIgnoreCase)))
                {
                    return false;
                }
            }
        }
        return true;
    }

    internal static bool VerifyExistingWindowsUpdatePackages(List<Device> devices, RazerKeyboardDriverRecipe recipe,
        string? commonHash, string? deviceHash, out DiagnosticFailure? failure,
        Func<string, string, string, RazerDriverApiPackageEvidence>? capture = null, Action? markRepairRequired = null)
    {
        capture ??= WindowsRazerDriverApiDiscovery.Capture;
        failure = null;
        bool trustedModel = false, trustedCommon = false;
        string[] ids = ReviewedWindowsUpdateHardwareIds(recipe).ToArray();
        for (int role = 0; role < ids.Length; role++)
        {
            string prefix = role == 4 ? $@"RZCONTROL\VID_1532&PID_{recipe.ProductId}&MI_00\" : ids[role] + "\\";
            Device? device = devices.SingleOrDefault(value => value.Id.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
            if (device is null || !(role == 4 ? Equal(device.Service, "RzCommon")
                : Equal(device.Service, recipe.DeviceService) || device.Upper.Contains(recipe.DeviceService, StringComparer.OrdinalIgnoreCase)
                    || device.Lower.Contains(recipe.DeviceService, StringComparer.OrdinalIgnoreCase))) { continue; }
            string service = role == 4 ? "RzCommon" : recipe.DeviceService;
            RazerDriverApiPackageEvidence evidence = capture(device.Id, service, ids[role]);
            if (evidence.Trusted && evidence.Installed && !string.IsNullOrWhiteSpace(evidence.Identity))
            {
                trustedModel |= role != 4;
                trustedCommon |= role == 4;
                continue;
            }
            // A known source artifact with an inbox binding is the same bounded repair
            // state admitted by the pinned installer. Catalog/signature failures never qualify.
            bool knownInbox = role == 1 && Equal(deviceHash, recipe.DeviceHash)
                && evidence.FailureDetail is { Stage: "InfContract", Reason: "ProviderMismatch", Context: { } context }
                && HasInboxKeyboardInf(context);
            bool missingImage = (role == 4 ? commonHash : deviceHash) is null && !evidence.Installed
                && evidence.FailureDetail is { } missing && (missing is { Stage: "ServiceBinding", Reason: "ServiceMissing" }
                    or { Stage: "ServiceImage", Reason: "ServiceImageNotFound" });
            if (knownInbox || missingImage)
            {
                markRepairRequired?.Invoke();
                continue;
            }
            failure = InspectionFailure("ExistingPackages", "ExistingBoundPackageUntrusted", recipe, devices, new { Hardware = ids[role] })
                with
            { Cause = evidence.FailureDetail };
            return false;
        }
        // Orphaned files cannot inherit runtime trust from an absent endpoint. Preserve
        // unknown bytes; exact reviewed source artifacts remain eligible for missing binding repair.
        if (commonHash is not null && !trustedCommon && !Equal(commonHash, recipe.CommonHash)
            || deviceHash is not null && !trustedModel && !Equal(deviceHash, recipe.DeviceHash))
        {
            failure = InspectionFailure("ExistingPackages", "OrphanedDriverImageUntrusted", recipe, devices,
                new { CommonObserved = commonHash, DeviceObserved = deviceHash });
            return false;
        }
        return true;
    }

    private static bool HasInboxKeyboardInf(string context)
    {
        try
        {
            using JsonDocument document = JsonDocument.Parse(context);
            if (document.RootElement.ValueKind != JsonValueKind.Object) { return false; }
            var values = document.RootElement.EnumerateObject().Where(property => property.NameEquals("PublishedInfName")).ToArray();
            return values.Length == 1 && values[0].Value.ValueKind == JsonValueKind.String
                && Equal(values[0].Value.GetString(), "keyboard.inf");
        }
        catch (JsonException) { return false; }
    }

    internal static RazerDriverInstallationInspection InspectWindowsUpdatePackages(List<Device> devices,
        RazerKeyboardDriverRecipe recipe, string? commonHash, string? deviceHash, bool structurallyInstalled,
        Func<string, string, string, RazerDriverApiPackageEvidence>? capture = null)
    {
        bool repairRequired = false;
        if (!VerifyExistingWindowsUpdatePackages(devices, recipe, commonHash, deviceHash, out DiagnosticFailure? failure,
            capture, () => repairRequired = true))
        {
            return new(false, false, RazerDriverInstallOutcome.Unavailable) { FailureDetail = failure };
        }
        bool installed = structurallyInstalled && !repairRequired;
        if (installed && !VerifyInstalledWindowsUpdatePackages(devices, recipe, out failure, capture))
        {
            return new(false, false, RazerDriverInstallOutcome.Unavailable) { FailureDetail = failure };
        }
        return new(true, installed, RazerDriverInstallOutcome.Unavailable);
    }

    internal static bool VerifyInstalledWindowsUpdatePackages(List<Device> devices, RazerKeyboardDriverRecipe recipe,
        out DiagnosticFailure? failure,
        Func<string, string, string, RazerDriverApiPackageEvidence>? capture = null)
    {
        capture ??= WindowsRazerDriverApiDiscovery.Capture;
        failure = null;
        string[] contracts = ReviewedWindowsUpdateHardwareIds(recipe).ToArray();
        foreach ((string hardware, string prefix, string service) in new[]
        {
            (contracts[0], contracts[0] + "\\", recipe.DeviceService),
            (contracts[1], contracts[1] + "\\", recipe.DeviceService),
            (contracts[2], contracts[2] + "\\", recipe.DeviceService),
            (contracts[3], contracts[3] + "\\", recipe.DeviceService),
            (contracts[4], $@"RZCONTROL\VID_1532&PID_{recipe.ProductId}&MI_00\", "RzCommon"),
        })
        {
            Device[] matches = devices.Where(device => device.Id.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)).ToArray();
            if (matches.Length != 1)
            {
                failure = InspectionFailure("InstalledReadback", "InstalledRoleMissingOrAmbiguous", recipe, devices, new { Hardware = hardware });
                return false;
            }
            RazerDriverApiPackageEvidence evidence = capture(matches[0].Id, service, hardware);
            if (!evidence.Installed || !evidence.Trusted || string.IsNullOrWhiteSpace(evidence.Identity))
            {
                failure = InspectionFailure("InstalledReadback", "InstalledPackageUntrusted", recipe, devices, new { Hardware = hardware })
                    with
                { Cause = evidence.FailureDetail };
                return false;
            }
        }
        return true;
    }
}
