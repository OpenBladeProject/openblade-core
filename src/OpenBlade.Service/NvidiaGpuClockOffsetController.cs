using System.Runtime.InteropServices;
using System.Text;
using OpenBlade.Contracts;
using OpenBlade.Protocol;

namespace OpenBlade.Service;

internal interface IGpuClockOffsetController
{
    GpuClockOffsetCapabilities? Query();

    void Set(GpuClockOffsetSettings settings);
}

internal sealed class UnavailableGpuClockOffsetController : IGpuClockOffsetController
{
    internal static UnavailableGpuClockOffsetController Instance { get; } = new();

    public GpuClockOffsetCapabilities? Query() => null;

    public void Set(GpuClockOffsetSettings settings) => throw new BladeWriteException(
        WriteSubmissionState.KnownNotApplied,
        "GPU clock tuning is unavailable.");
}

internal sealed class NvidiaGpuClockOffsetController : IGpuClockOffsetController
{
    private const int Success = 0;
    private const uint ClockOffsetStructVersion = (1u << 24) | 24u;
    private const int PerformanceStateZero = 0;
    private const int NvmlMemoryOffsetMultiplier = 2;

    private readonly INvidiaNvmlClockOffsetApi api;

    internal NvidiaGpuClockOffsetController()
        : this(NvidiaNvmlClockOffsetApi.Instance)
    {
    }

    internal NvidiaGpuClockOffsetController(INvidiaNvmlClockOffsetApi api)
    {
        this.api = api ?? throw new ArgumentNullException(nameof(api));
    }

    public GpuClockOffsetCapabilities? Query()
    {
        bool initialized = false;
        try
        {
            EnsureSuccess(api.Initialize(), "initialize NVIDIA NVML");
            initialized = true;
            nint device = OpenFirstDevice();
            return QueryCapabilities(device);
        }
        catch (Exception exception) when (IsAvailabilityFailure(exception))
        {
            return null;
        }
        finally
        {
            if (initialized)
            {
                _ = api.Shutdown();
            }
        }
    }

    public void Set(GpuClockOffsetSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        if (!GpuClockOffsetSettings.IsWellFormed(settings))
        {
            throw new BladeWriteException(
                WriteSubmissionState.KnownNotApplied,
                "GPU clock offsets are invalid.");
        }

        bool initialized = false;
        bool coreMayHaveChanged = false;
        bool memoryMayHaveChanged = false;
        nint device = nint.Zero;
        GpuClockOffsetCapabilities? prior = null;
        try
        {
            EnsureSuccess(api.Initialize(), "initialize NVIDIA NVML");
            initialized = true;
            device = OpenFirstDevice();
            prior = QueryCapabilities(device);
            if (!prior.Supports(settings))
            {
                throw new BladeWriteException(
                    WriteSubmissionState.KnownNotApplied,
                    "GPU clock offsets are outside the ranges reported by the NVIDIA driver.");
            }

            NvmlClockOffset core = CreateWrite(
                NvmlClockType.Graphics,
                settings.CoreMegahertz);
            EnsureSuccess(api.SetClockOffsets(device, ref core), "set the GPU core clock offset");
            coreMayHaveChanged = true;

            NvmlClockOffset memory = CreateWrite(
                NvmlClockType.Memory,
                settings.MemoryMegahertz);
            EnsureSuccess(
                api.SetClockOffsets(device, ref memory),
                "set the GPU memory clock offset");
            memoryMayHaveChanged = true;

            GpuClockOffsetCapabilities confirmed = QueryCapabilities(device);
            if (confirmed.Current != settings)
            {
                throw new NvmlClockOffsetException(
                    status: -1,
                    "NVIDIA confirmed "
                    + $"Core={confirmed.Current.CoreMegahertz};"
                    + $"Memory={confirmed.Current.MemoryMegahertz} instead of "
                    + $"Core={settings.CoreMegahertz};"
                    + $"Memory={settings.MemoryMegahertz}.");
            }
        }
        catch (BladeWriteException)
        {
            throw;
        }
        catch (Exception exception) when (IsOperationalFailure(exception))
        {
            bool restored = prior is not null
                && TryRestore(
                    device,
                    prior.Current,
                    coreMayHaveChanged,
                    memoryMayHaveChanged);
            throw new BladeWriteException(
                restored || (!coreMayHaveChanged && !memoryMayHaveChanged)
                    ? WriteSubmissionState.KnownNotApplied
                    : WriteSubmissionState.Indeterminate,
                restored
                    ? $"GPU clock tuning failed and the previous offsets were restored. {exception.Message}"
                    : $"GPU clock tuning failed. {exception.Message}",
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
            throw new NvmlClockOffsetException(
                status: -1,
                "NVIDIA NVML did not report a GPU.");
        }

        EnsureSuccess(api.GetDeviceHandleByIndex(0, out nint device), "open NVIDIA GPU 0");
        return device;
    }

    private GpuClockOffsetCapabilities QueryCapabilities(nint device)
    {
        var name = new StringBuilder(128);
        EnsureSuccess(
            api.GetDeviceName(device, name, checked((uint)name.Capacity)),
            "read the NVIDIA GPU name");
        var capabilities = new GpuClockOffsetCapabilities(
            name.ToString(),
            QueryRange(device, NvmlClockType.Graphics, "GPU core clock offset"),
            QueryRange(device, NvmlClockType.Memory, "GPU memory clock offset"));
        if (!capabilities.IsWellFormed)
        {
            throw new NvmlClockOffsetException(
                status: -1,
                "NVIDIA returned invalid GPU clock-offset capabilities.");
        }

        return capabilities;
    }

    private GpuClockOffsetRange QueryRange(
        nint device,
        NvmlClockType type,
        string label)
    {
        NvmlClockOffset info = CreateWrite(type, currentMegahertz: 0);
        EnsureSuccess(api.GetClockOffsets(device, ref info), $"read the {label}");
        int divisor = type == NvmlClockType.Memory
            ? NvmlMemoryOffsetMultiplier
            : 1;
        if (info.MinimumMegahertz % divisor != 0
            || info.MaximumMegahertz % divisor != 0
            || info.CurrentMegahertz % divisor != 0)
        {
            throw new NvmlClockOffsetException(
                status: -1,
                $"NVIDIA returned an unrepresentable {label} value.");
        }

        var range = new GpuClockOffsetRange(
            info.MinimumMegahertz / divisor,
            info.MaximumMegahertz / divisor,
            info.CurrentMegahertz / divisor);
        if (!range.IsWellFormed)
        {
            throw new NvmlClockOffsetException(
                status: -1,
                $"NVIDIA returned an invalid {label} range.");
        }

        return range;
    }

    private bool TryRestore(
        nint device,
        GpuClockOffsetSettings prior,
        bool restoreCore,
        bool restoreMemory)
    {
        try
        {
            if (restoreCore)
            {
                NvmlClockOffset core = CreateWrite(
                    NvmlClockType.Graphics,
                    prior.CoreMegahertz);
                EnsureSuccess(
                    api.SetClockOffsets(device, ref core),
                    "restore the GPU core clock offset");
            }

            if (restoreMemory)
            {
                NvmlClockOffset memory = CreateWrite(
                    NvmlClockType.Memory,
                    prior.MemoryMegahertz);
                EnsureSuccess(
                    api.SetClockOffsets(device, ref memory),
                    "restore the GPU memory clock offset");
            }

            GpuClockOffsetCapabilities restored = QueryCapabilities(device);
            return restored.Current == prior;
        }
        catch
        {
            return false;
        }
    }

    private static NvmlClockOffset CreateWrite(
        NvmlClockType type,
        int currentMegahertz) => new()
        {
            Version = ClockOffsetStructVersion,
            Type = type,
            PerformanceState = PerformanceStateZero,
            // NVML exposes GDDR memory offsets at twice the editable P-state
            // delta reported by NVAPI and displayed by tuning applications.
            CurrentMegahertz = type == NvmlClockType.Memory
                ? checked(currentMegahertz * NvmlMemoryOffsetMultiplier)
                : currentMegahertz,
        };

    private static void EnsureSuccess(int status, string action)
    {
        if (status != Success)
        {
            throw new NvmlClockOffsetException(
                status,
                $"Unable to {action}; NVIDIA NVML returned status {status}.");
        }
    }

    private static bool IsAvailabilityFailure(Exception exception) =>
        exception is DllNotFoundException
            or EntryPointNotFoundException
            or BadImageFormatException
            or NvmlClockOffsetException;

    private static bool IsOperationalFailure(Exception exception) =>
        IsAvailabilityFailure(exception);
}

internal sealed class NvmlClockOffsetException(int status, string message)
    : Exception(message)
{
    internal int Status { get; } = status;
}

internal enum NvmlClockType
{
    Graphics = 0,
    Memory = 2,
}

[StructLayout(LayoutKind.Sequential)]
internal struct NvmlClockOffset
{
    public uint Version;
    public NvmlClockType Type;
    public int PerformanceState;
    public int CurrentMegahertz;
    public int MinimumMegahertz;
    public int MaximumMegahertz;
}

internal interface INvidiaNvmlClockOffsetApi
{
    int Initialize();

    int Shutdown();

    int GetDeviceCount(out uint count);

    int GetDeviceHandleByIndex(uint index, out nint device);

    int GetDeviceName(nint device, StringBuilder name, uint length);

    int GetClockOffsets(nint device, ref NvmlClockOffset info);

    int SetClockOffsets(nint device, ref NvmlClockOffset info);
}

internal sealed class NvidiaNvmlClockOffsetApi : INvidiaNvmlClockOffsetApi
{
    internal static NvidiaNvmlClockOffsetApi Instance { get; } = new();

    public int Initialize() => NvmlInit();

    public int Shutdown() => NvmlShutdown();

    public int GetDeviceCount(out uint count) => NvmlDeviceGetCount(out count);

    public int GetDeviceHandleByIndex(uint index, out nint device) =>
        NvmlDeviceGetHandleByIndex(index, out device);

    public int GetDeviceName(nint device, StringBuilder name, uint length) =>
        NvmlDeviceGetName(device, name, length);

    public int GetClockOffsets(nint device, ref NvmlClockOffset info) =>
        NvmlDeviceGetClockOffsets(device, ref info);

    public int SetClockOffsets(nint device, ref NvmlClockOffset info) =>
        NvmlDeviceSetClockOffsets(device, ref info);

    [DllImport("nvml.dll", EntryPoint = "nvmlInit_v2", CallingConvention = CallingConvention.Cdecl)]
    private static extern int NvmlInit();

    [DllImport("nvml.dll", EntryPoint = "nvmlShutdown", CallingConvention = CallingConvention.Cdecl)]
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
    private static extern int NvmlDeviceGetHandleByIndex(uint index, out nint device);

    [DllImport(
        "nvml.dll",
        EntryPoint = "nvmlDeviceGetName",
        CallingConvention = CallingConvention.Cdecl,
        CharSet = CharSet.Ansi)]
    private static extern int NvmlDeviceGetName(nint device, StringBuilder name, uint length);

    [DllImport(
        "nvml.dll",
        EntryPoint = "nvmlDeviceGetClockOffsets",
        CallingConvention = CallingConvention.Cdecl)]
    private static extern int NvmlDeviceGetClockOffsets(
        nint device,
        ref NvmlClockOffset info);

    [DllImport(
        "nvml.dll",
        EntryPoint = "nvmlDeviceSetClockOffsets",
        CallingConvention = CallingConvention.Cdecl)]
    private static extern int NvmlDeviceSetClockOffsets(
        nint device,
        ref NvmlClockOffset info);
}
