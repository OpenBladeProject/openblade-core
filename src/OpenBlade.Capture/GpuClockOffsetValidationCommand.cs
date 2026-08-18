using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using OpenBlade.Protocol;

namespace OpenBlade.Capture;

internal readonly record struct GpuClockOffsetValidationSettings(
    int CoreMegahertz,
    int MemoryMegahertz);

internal sealed record GpuClockOffsetValidationPlan(
    string Target,
    string ExpectedDeviceName,
    int CoreMinimumMegahertz,
    int CoreMaximumMegahertz,
    int MemoryMinimumMegahertz,
    int MemoryMaximumMegahertz,
    int AdjacentStepMegahertz,
    GpuClockOffsetValidationSettings? RequiredBaseline = null)
{
    internal static GpuClockOffsetValidationPlan Rz090528 { get; } = new(
        "RZ09-0528 1532:02C6 BIOS 2.02",
        "NVIDIA GeForce RTX 5090 Laptop GPU",
        CoreMinimumMegahertz: -1000,
        CoreMaximumMegahertz: 400,
        MemoryMinimumMegahertz: -1000,
        MemoryMaximumMegahertz: 3000,
        AdjacentStepMegahertz: 1);

    internal static GpuClockOffsetValidationPlan Rz090581Bios400 { get; } = new(
        "RZ09-0581 1532:02E0 BIOS 4.00",
        "NVIDIA GeForce RTX 5090 Laptop GPU",
        CoreMinimumMegahertz: -15,
        CoreMaximumMegahertz: 15,
        MemoryMinimumMegahertz: -15,
        MemoryMaximumMegahertz: 15,
        AdjacentStepMegahertz: 5,
        RequiredBaseline: new(0, 0));
}

internal interface IGpuClockOffsetValidationController
{
    GpuClockOffsetCapabilities Query();

    void Set(GpuClockOffsetValidationSettings settings);
}

internal enum SynapseActivityObservation
{
    Inactive,
    Active,
    ObservationFailed,
}

internal static class GpuClockOffsetValidationEnvironment
{
    internal const string Confirmation =
        "RZ09-0528:1532:02C6:GPU-OFFSETS:RESTORE";
    internal const string Rz090581Bios400Confirmation =
        "RZ09-0581:1532:02E0:BIOS-4.00:GPU-OFFSETS:RESTORE";

    internal static bool IsExactHost() =>
        WindowsHostIdentitySource.IsExactRz090528WindowsHost();

    internal static bool IsExactRz090581Bios400Host()
    {
        var registry = new WindowsRegistryValueSource();
        HostIdentityReadResult identity = new WindowsHostIdentitySource(
            registry,
            new RuntimeWindowsPlatformEvidenceSource(registry))
            .ReadExactIdentity();
        return identity is
        {
            Evidence.BiosVersion: "4.00",
            Failure: null,
        };
    }

    internal static bool HasOneExactProtocolCollection()
    {
        try
        {
            return WindowsHidEnumerator
                .FindCollections(0x1532, 0x02C6)
                .Count(collection =>
                    collection.UsagePage == 0x0001
                    && collection.Usage == 0x0002
                    && collection.FeatureReportByteLength == 91) == 1;
        }
        catch
        {
            return false;
        }
    }

    internal static bool HasOneExactRz090581ProtocolCollection()
    {
        try
        {
            return WindowsHidEnumerator
                .FindCollections(
                    TargetBladeDevice.VendorId,
                    TargetBladeDevice.ProductId)
                .Count(collection =>
                    collection.UsagePage == TargetBladeDevice.UsagePage
                    && collection.Usage == TargetBladeDevice.Usage
                    && TargetBladeDevice.CanCarryProtocolReport(
                        collection.FeatureReportByteLength)) == 1;
        }
        catch
        {
            return false;
        }
    }

    internal static bool HasOneExactCoolingPadControlCollection()
    {
        try
        {
            HidInterfaceInfo[] controlCollections = WindowsHidEnumerator
                .FindCollections(0x1532, 0x0F43)
                .Where(collection =>
                    collection.UsagePage == 0x000C
                    && collection.Usage == 0x0001
                    && collection.FeatureReportByteLength == 91)
                .ToArray();
            return controlCollections.Length == 1
                && controlCollections[0].VersionNumber ==
                    WindowsCoolingPadDiscoverySource.VersionNumber;
        }
        catch
        {
            return false;
        }
    }

    internal static bool IsSynapseInactive() =>
        ObserveSynapseActivity() == SynapseActivityObservation.Inactive;

    internal static SynapseActivityObservation ObserveSynapseActivity()
    {
        bool active = false;
        foreach (string name in new[]
        {
            "RazerAppEngine",
            "GameManagerService3",
            "razer_elevation_service",
        })
        {
            Process[] processes;
            try
            {
                processes = Process.GetProcessesByName(name);
            }
            catch
            {
                return SynapseActivityObservation.ObservationFailed;
            }

            try
            {
                active |= processes.Length > 0;
            }
            finally
            {
                foreach (Process process in processes)
                {
                    process.Dispose();
                }
            }
        }

        return active
            ? SynapseActivityObservation.Active
            : SynapseActivityObservation.Inactive;
    }

    internal static bool IsOpenBladeServiceInactive() =>
        HasNoProcess("OpenBlade.Service");

    internal static bool IsUsbCaptureInactive() =>
        HasNoProcess("USBPcapCMD")
        && HasNoProcess("dumpcap")
        && HasNoProcess("tshark");

    internal static bool AreGpuTuningToolsInactive() =>
        HasNoProcess("MSIAfterburner")
        && HasNoProcess("RTSS");

    private static bool HasNoProcess(string name)
    {
        Process[] processes;
        try
        {
            processes = Process.GetProcessesByName(name);
        }
        catch
        {
            return false;
        }

        try
        {
            return processes.Length == 0;
        }
        finally
        {
            foreach (Process process in processes)
            {
                process.Dispose();
            }
        }
    }
}

internal static class GpuClockOffsetValidationCommand
{
    internal static int Run(
        IGpuClockOffsetValidationController controller,
        TextWriter output,
        Func<bool>? mutationAllowed = null) =>
        Run(
            controller,
            output,
            GpuClockOffsetValidationPlan.Rz090528,
            mutationAllowed);

    internal static int Run(
        IGpuClockOffsetValidationController controller,
        TextWriter output,
        GpuClockOffsetValidationPlan plan,
        Func<bool>? mutationAllowed = null)
    {
        ArgumentNullException.ThrowIfNull(controller);
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(plan);
        mutationAllowed ??= static () => true;

        GpuClockOffsetCapabilities? baseline = null;
        GpuClockOffsetValidationSettings? target = null;
        GpuClockOffsetCapabilities? appliedState = null;
        GpuClockOffsetCapabilities? restoredState = null;
        bool mutationAttempted = false;
        bool applied = false;
        bool readBack = false;
        bool restored = false;
        string? failure = null;

        try
        {
            baseline = controller.Query();
            ValidateBaseline(baseline, plan);
            target = new(
                ChooseAdjacent(
                    baseline.Core.CurrentMegahertz,
                    plan.CoreMinimumMegahertz,
                    plan.CoreMaximumMegahertz,
                    plan.AdjacentStepMegahertz),
                ChooseAdjacent(
                    baseline.Memory.CurrentMegahertz,
                    plan.MemoryMinimumMegahertz,
                    plan.MemoryMaximumMegahertz,
                    plan.AdjacentStepMegahertz));
            if (!mutationAllowed())
            {
                throw new InvalidOperationException(
                    "The controller-isolation checks changed before the GPU write.");
            }
            mutationAttempted = true;
            controller.Set(target.Value);
            applied = true;
            appliedState = controller.Query();
            readBack = Matches(appliedState, target.Value);
            if (!readBack)
            {
                failure = "NVIDIA did not confirm the adjacent offset target.";
            }
        }
        catch (Exception exception)
        {
            failure = exception.Message;
        }
        finally
        {
            if (baseline is not null && mutationAttempted)
            {
                var saved = new GpuClockOffsetValidationSettings(
                    baseline.Core.CurrentMegahertz,
                    baseline.Memory.CurrentMegahertz);
                try
                {
                    controller.Set(saved);
                    restoredState = controller.Query();
                    restored = Matches(restoredState, saved);
                    if (!restored)
                    {
                        failure = Append(
                            failure,
                            "NVIDIA did not confirm the saved offsets after restoration.");
                    }
                }
                catch (Exception exception)
                {
                    failure = Append(
                        failure,
                        $"GPU offset restoration failed: {exception.Message}");
                }
            }
        }

        bool success = applied && readBack && restored && failure is null;
        output.WriteLine($"Target={plan.Target}");
        output.WriteLine("MutationPolicy=SaveSetReadbackRestore");
        output.WriteLine($"Device={baseline?.DeviceName ?? "Unavailable"}");
        output.WriteLine($"Original={Format(baseline)}");
        output.WriteLine($"Requested={Format(target)}");
        output.WriteLine($"AppliedState={Format(appliedState)}");
        output.WriteLine($"RestoredState={Format(restoredState)}");
        output.WriteLine($"Applied={applied}");
        output.WriteLine($"ReadBack={readBack}");
        output.WriteLine($"Restored={restored}");
        output.WriteLine($"Failure={failure ?? "None"}");
        output.WriteLine($"Success={success}");
        return success ? 0 : 2;
    }

    private static void ValidateBaseline(
        GpuClockOffsetCapabilities baseline,
        GpuClockOffsetValidationPlan plan)
    {
        if (!string.Equals(
                baseline.DeviceName,
                plan.ExpectedDeviceName,
                StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "The exact NVIDIA laptop GPU was not found.");
        }
        if (!Contains(
                baseline.Core,
                plan.CoreMinimumMegahertz,
                plan.CoreMaximumMegahertz)
            || !Contains(
                baseline.Memory,
                plan.MemoryMinimumMegahertz,
                plan.MemoryMaximumMegahertz))
        {
            throw new InvalidOperationException(
                "The current offsets or driver ranges do not contain the validation bounds.");
        }
        if (plan.RequiredBaseline is { } required
            && (baseline.Core.CurrentMegahertz != required.CoreMegahertz
                || baseline.Memory.CurrentMegahertz != required.MemoryMegahertz))
        {
            throw new InvalidOperationException(
                "The current GPU clock offsets do not match the required safe baseline.");
        }
    }

    private static bool Contains(
        GpuClockOffsetRange range,
        int policyMinimum,
        int policyMaximum) =>
        range.MinimumMegahertz <= policyMinimum
        && range.MaximumMegahertz >= policyMaximum
        && range.CurrentMegahertz >= policyMinimum
        && range.CurrentMegahertz <= policyMaximum;

    private static int ChooseAdjacent(
        int current,
        int minimum,
        int maximum,
        int step) =>
        current <= maximum - step
            ? checked(current + step)
            : current >= minimum + step
                ? checked(current - step)
                : throw new InvalidOperationException(
                    "The exact-model GPU offset policy has no adjacent validation value.");

    private static bool Matches(
        GpuClockOffsetCapabilities actual,
        GpuClockOffsetValidationSettings expected) =>
        actual.Core.CurrentMegahertz == expected.CoreMegahertz
        && actual.Memory.CurrentMegahertz == expected.MemoryMegahertz;

    private static string Append(string? first, string second) =>
        first is null ? second : $"{first} {second}";

    private static string Format(GpuClockOffsetCapabilities? value) =>
        value is null
            ? "Unavailable"
            : $"Core={value.Core.CurrentMegahertz};Memory={value.Memory.CurrentMegahertz}";

    private static string Format(GpuClockOffsetValidationSettings? value) =>
        value is null
            ? "Unavailable"
            : $"Core={value.Value.CoreMegahertz};Memory={value.Value.MemoryMegahertz}";
}

internal sealed class NvidiaNvmlGpuClockOffsetValidationController
    : IGpuClockOffsetValidationController
{
    private const int Success = 0;
    private const uint ClockOffsetStructVersion = (1u << 24) | 24u;
    private const int PerformanceStateZero = 0;
    private const int NvmlMemoryOffsetMultiplier = 2;
    private readonly INvidiaNvmlGpuClockOffsetValidationApi api;

    internal NvidiaNvmlGpuClockOffsetValidationController()
        : this(NvidiaNvmlGpuClockOffsetValidationApi.Instance)
    {
    }

    internal NvidiaNvmlGpuClockOffsetValidationController(
        INvidiaNvmlGpuClockOffsetValidationApi api)
    {
        ArgumentNullException.ThrowIfNull(api);
        this.api = api;
    }

    public GpuClockOffsetCapabilities Query()
    {
        EnsureSuccess(api.Initialize(), "initialize NVIDIA NVML");
        try
        {
            nint device = OpenFirstDevice();
            return QueryCapabilities(device);
        }
        finally
        {
            _ = api.Shutdown();
        }
    }

    public void Set(GpuClockOffsetValidationSettings settings)
    {
        bool initialized = false;
        bool coreMayHaveChanged = false;
        bool memoryMayHaveChanged = false;
        nint device = nint.Zero;
        GpuClockOffsetValidationSettings? prior = null;
        try
        {
            EnsureSuccess(api.Initialize(), "initialize NVIDIA NVML");
            initialized = true;
            device = OpenFirstDevice();
            GpuClockOffsetCapabilities before = QueryCapabilities(device);
            prior = new(
                before.Core.CurrentMegahertz,
                before.Memory.CurrentMegahertz);
            if (!Supports(before, settings))
            {
                throw new InvalidOperationException(
                    "The requested GPU offsets are outside the driver range.");
            }

            NvmlValidationClockOffset core = CreateWrite(
                NvmlValidationClockType.Graphics,
                settings.CoreMegahertz);
            coreMayHaveChanged = true;
            EnsureSuccess(
                api.SetClockOffsets(device, ref core),
                "set the GPU core clock offset");

            NvmlValidationClockOffset memory = CreateWrite(
                NvmlValidationClockType.Memory,
                settings.MemoryMegahertz);
            memoryMayHaveChanged = true;
            EnsureSuccess(
                api.SetClockOffsets(device, ref memory),
                "set the GPU memory clock offset");

            GpuClockOffsetCapabilities confirmed = QueryCapabilities(device);
            if (confirmed.Core.CurrentMegahertz != settings.CoreMegahertz
                || confirmed.Memory.CurrentMegahertz != settings.MemoryMegahertz)
            {
                throw new InvalidOperationException(
                    "NVIDIA confirmed "
                    + $"Core={confirmed.Core.CurrentMegahertz};"
                    + $"Memory={confirmed.Memory.CurrentMegahertz} instead of the requested offsets.");
            }
        }
        catch (Exception exception)
        {
            bool restored = prior is not null
                && TryRestore(
                    device,
                    prior.Value,
                    coreMayHaveChanged,
                    memoryMayHaveChanged);
            throw new InvalidOperationException(
                restored
                    ? $"GPU offset write failed and the prior offsets were restored. {exception.Message}"
                    : $"GPU offset write failed. {exception.Message}",
                exception);
        }
        finally
        {
            if (initialized)
            {
                _ = api.Shutdown();
            }
        }
    }

    private nint OpenFirstDevice()
    {
        EnsureSuccess(api.GetDeviceCount(out uint count), "enumerate NVIDIA GPUs");
        if (count == 0)
        {
            throw new InvalidOperationException("NVIDIA NVML did not report a GPU.");
        }

        EnsureSuccess(
            api.GetDeviceHandleByIndex(0, out nint device),
            "open NVIDIA GPU 0");
        return device;
    }

    private GpuClockOffsetCapabilities QueryCapabilities(nint device)
    {
        var name = new StringBuilder(128);
        EnsureSuccess(
            api.GetDeviceName(device, name, checked((uint)name.Capacity)),
            "read the NVIDIA GPU name");
        return new(
            name.ToString(),
            QueryRange(device, NvmlValidationClockType.Graphics, "core"),
            QueryRange(device, NvmlValidationClockType.Memory, "memory"));
    }

    private GpuClockOffsetRange QueryRange(
        nint device,
        NvmlValidationClockType type,
        string label)
    {
        NvmlValidationClockOffset info = CreateWrite(type, 0);
        EnsureSuccess(
            api.GetClockOffsets(device, ref info),
            $"read the GPU {label} clock offset");
        int divisor = type == NvmlValidationClockType.Memory
            ? NvmlMemoryOffsetMultiplier
            : 1;
        if (info.MinimumMegahertz % divisor != 0
            || info.MaximumMegahertz % divisor != 0
            || info.CurrentMegahertz % divisor != 0)
        {
            throw new InvalidOperationException(
                $"NVIDIA returned an unrepresentable GPU {label} clock-offset value.");
        }

        var range = new GpuClockOffsetRange(
            info.MinimumMegahertz / divisor,
            info.MaximumMegahertz / divisor,
            info.CurrentMegahertz / divisor);
        if (range.MinimumMegahertz > range.MaximumMegahertz
            || range.CurrentMegahertz < range.MinimumMegahertz
            || range.CurrentMegahertz > range.MaximumMegahertz)
        {
            throw new InvalidOperationException(
                $"NVIDIA returned an invalid GPU {label} clock-offset range.");
        }

        return range;
    }

    private bool TryRestore(
        nint device,
        GpuClockOffsetValidationSettings prior,
        bool restoreCore,
        bool restoreMemory)
    {
        try
        {
            if (restoreCore)
            {
                NvmlValidationClockOffset core = CreateWrite(
                    NvmlValidationClockType.Graphics,
                    prior.CoreMegahertz);
                EnsureSuccess(
                    api.SetClockOffsets(device, ref core),
                    "restore the GPU core clock offset");
            }
            if (restoreMemory)
            {
                NvmlValidationClockOffset memory = CreateWrite(
                    NvmlValidationClockType.Memory,
                    prior.MemoryMegahertz);
                EnsureSuccess(
                    api.SetClockOffsets(device, ref memory),
                    "restore the GPU memory clock offset");
            }

            GpuClockOffsetCapabilities restored = QueryCapabilities(device);
            return restored.Core.CurrentMegahertz == prior.CoreMegahertz
                && restored.Memory.CurrentMegahertz == prior.MemoryMegahertz;
        }
        catch
        {
            return false;
        }
    }

    private static bool Supports(
        GpuClockOffsetCapabilities capabilities,
        GpuClockOffsetValidationSettings settings) =>
        settings.CoreMegahertz >= capabilities.Core.MinimumMegahertz
        && settings.CoreMegahertz <= capabilities.Core.MaximumMegahertz
        && settings.MemoryMegahertz >= capabilities.Memory.MinimumMegahertz
        && settings.MemoryMegahertz <= capabilities.Memory.MaximumMegahertz;

    private static NvmlValidationClockOffset CreateWrite(
        NvmlValidationClockType type,
        int currentMegahertz) => new()
        {
            Version = ClockOffsetStructVersion,
            Type = type,
            PerformanceState = PerformanceStateZero,
            CurrentMegahertz = type == NvmlValidationClockType.Memory
                ? checked(currentMegahertz * NvmlMemoryOffsetMultiplier)
                : currentMegahertz,
        };

    private static void EnsureSuccess(int status, string action)
    {
        if (status != Success)
        {
            throw new InvalidOperationException(
                $"Unable to {action}; NVIDIA NVML returned status {status}.");
        }
    }
}

internal enum NvmlValidationClockType
{
    Graphics = 0,
    Memory = 2,
}

[StructLayout(LayoutKind.Sequential)]
internal struct NvmlValidationClockOffset
{
    internal uint Version;
    internal NvmlValidationClockType Type;
    internal int PerformanceState;
    internal int CurrentMegahertz;
    internal int MinimumMegahertz;
    internal int MaximumMegahertz;
}

internal interface INvidiaNvmlGpuClockOffsetValidationApi
{
    int Initialize();

    int Shutdown();

    int GetDeviceCount(out uint count);

    int GetDeviceHandleByIndex(uint index, out nint device);

    int GetDeviceName(nint device, StringBuilder name, uint length);

    int GetClockOffsets(nint device, ref NvmlValidationClockOffset info);

    int SetClockOffsets(nint device, ref NvmlValidationClockOffset info);
}

internal sealed class NvidiaNvmlGpuClockOffsetValidationApi
    : INvidiaNvmlGpuClockOffsetValidationApi
{
    internal static NvidiaNvmlGpuClockOffsetValidationApi Instance { get; } = new();

    private NvidiaNvmlGpuClockOffsetValidationApi()
    {
    }

    public int Initialize() => NvmlInit();

    public int Shutdown() => NvmlShutdown();

    public int GetDeviceCount(out uint count) => NvmlDeviceGetCount(out count);

    public int GetDeviceHandleByIndex(uint index, out nint device) =>
        NvmlDeviceGetHandleByIndex(index, out device);

    public int GetDeviceName(nint device, StringBuilder name, uint length) =>
        NvmlDeviceGetName(device, name, length);

    public int GetClockOffsets(
        nint device,
        ref NvmlValidationClockOffset info) =>
        NvmlDeviceGetClockOffsets(device, ref info);

    public int SetClockOffsets(
        nint device,
        ref NvmlValidationClockOffset info) =>
        NvmlDeviceSetClockOffsets(device, ref info);

    [DllImport(
        "nvml.dll",
        EntryPoint = "nvmlInit_v2",
        CallingConvention = CallingConvention.Cdecl)]
    private static extern int NvmlInit();

    [DllImport(
        "nvml.dll",
        EntryPoint = "nvmlShutdown",
        CallingConvention = CallingConvention.Cdecl)]
    private static extern int NvmlShutdown();

    [DllImport(
        "nvml.dll",
        EntryPoint = "nvmlDeviceGetCount_v2",
        CallingConvention = CallingConvention.Cdecl)]
    private static extern int NvmlDeviceGetCount(out uint count);

    [DllImport(
        "nvml.dll",
        EntryPoint = "nvmlDeviceGetHandleByIndex_v2",
        CallingConvention = CallingConvention.Cdecl)]
    private static extern int NvmlDeviceGetHandleByIndex(
        uint index,
        out nint device);

    [DllImport(
        "nvml.dll",
        EntryPoint = "nvmlDeviceGetName",
        CallingConvention = CallingConvention.Cdecl,
        CharSet = CharSet.Ansi)]
    private static extern int NvmlDeviceGetName(
        nint device,
        StringBuilder name,
        uint length);

    [DllImport(
        "nvml.dll",
        EntryPoint = "nvmlDeviceGetClockOffsets",
        CallingConvention = CallingConvention.Cdecl)]
    private static extern int NvmlDeviceGetClockOffsets(
        nint device,
        ref NvmlValidationClockOffset info);

    [DllImport(
        "nvml.dll",
        EntryPoint = "nvmlDeviceSetClockOffsets",
        CallingConvention = CallingConvention.Cdecl)]
    private static extern int NvmlDeviceSetClockOffsets(
        nint device,
        ref NvmlValidationClockOffset info);
}
