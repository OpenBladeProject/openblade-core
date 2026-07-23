using System.Runtime.InteropServices;
using System.Text;

namespace OpenBlade.Capture;

public sealed record GpuClockOffsetRange(
    int MinimumMegahertz,
    int MaximumMegahertz,
    int CurrentMegahertz);

public sealed record GpuClockOffsetCapabilities(
    string DeviceName,
    GpuClockOffsetRange Core,
    GpuClockOffsetRange Memory);

public interface IGpuClockOffsetProbe
{
    GpuClockOffsetCapabilities Query();
}

public sealed class NvidiaNvmlGpuClockOffsetProbe : IGpuClockOffsetProbe
{
    private const int Success = 0;
    private const uint ClockOffsetStructVersion = (1u << 24) | 24u;
    private const int PerformanceStateZero = 0;

    public GpuClockOffsetCapabilities Query()
    {
        EnsureSuccess(NvmlInit(), "initialize NVML");
        try
        {
            EnsureSuccess(NvmlDeviceGetCount(out uint count), "enumerate NVIDIA GPUs");
            if (count == 0)
            {
                throw new InvalidOperationException("NVML did not report an NVIDIA GPU.");
            }

            EnsureSuccess(NvmlDeviceGetHandleByIndex(0, out nint device), "open NVIDIA GPU 0");
            var name = new StringBuilder(96);
            EnsureSuccess(NvmlDeviceGetName(device, name, checked((uint)name.Capacity)), "read the GPU name");

            return new GpuClockOffsetCapabilities(
                name.ToString(),
                QueryRange(device, NvmlClockType.Graphics, "GPU core clock offset"),
                QueryRange(device, NvmlClockType.Memory, "GPU memory clock offset"));
        }
        finally
        {
            _ = NvmlShutdown();
        }
    }

    private static GpuClockOffsetRange QueryRange(nint device, NvmlClockType type, string label)
    {
        var info = new NvmlClockOffset
        {
            Version = ClockOffsetStructVersion,
            Type = type,
            PerformanceState = PerformanceStateZero,
        };

        EnsureSuccess(NvmlDeviceGetClockOffsets(device, ref info), $"read the {label} range");
        if (info.MinimumMegahertz > info.MaximumMegahertz ||
            info.CurrentMegahertz < info.MinimumMegahertz ||
            info.CurrentMegahertz > info.MaximumMegahertz)
        {
            throw new InvalidOperationException($"NVML returned an invalid {label} range.");
        }

        return new GpuClockOffsetRange(
            info.MinimumMegahertz,
            info.MaximumMegahertz,
            info.CurrentMegahertz);
    }

    private static void EnsureSuccess(int status, string action)
    {
        if (status != Success)
        {
            throw new InvalidOperationException($"Unable to {action}; NVML returned status {status}.");
        }
    }

    private enum NvmlClockType
    {
        Graphics = 0,
        Memory = 2,
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NvmlClockOffset
    {
        public uint Version;
        public NvmlClockType Type;
        public int PerformanceState;
        public int CurrentMegahertz;
        public int MinimumMegahertz;
        public int MaximumMegahertz;
    }

    [DllImport("nvml.dll", EntryPoint = "nvmlInit_v2", CallingConvention = CallingConvention.Cdecl)]
    private static extern int NvmlInit();

    [DllImport("nvml.dll", EntryPoint = "nvmlShutdown", CallingConvention = CallingConvention.Cdecl)]
    private static extern int NvmlShutdown();

    [DllImport("nvml.dll", EntryPoint = "nvmlDeviceGetCount_v2", CallingConvention = CallingConvention.Cdecl)]
    private static extern int NvmlDeviceGetCount(out uint count);

    [DllImport("nvml.dll", EntryPoint = "nvmlDeviceGetHandleByIndex_v2", CallingConvention = CallingConvention.Cdecl)]
    private static extern int NvmlDeviceGetHandleByIndex(uint index, out nint device);

    [DllImport("nvml.dll", EntryPoint = "nvmlDeviceGetName", CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
    private static extern int NvmlDeviceGetName(nint device, StringBuilder name, uint length);

    [DllImport("nvml.dll", EntryPoint = "nvmlDeviceGetClockOffsets", CallingConvention = CallingConvention.Cdecl)]
    private static extern int NvmlDeviceGetClockOffsets(nint device, ref NvmlClockOffset info);
}

public static class GpuClockOffsetProbeCommand
{
    public static int Run(IGpuClockOffsetProbe probe, TextWriter output)
    {
        ArgumentNullException.ThrowIfNull(probe);
        ArgumentNullException.ThrowIfNull(output);

        try
        {
            GpuClockOffsetCapabilities capabilities = probe.Query();
            output.WriteLine($"Device={capabilities.DeviceName}");
            output.WriteLine(
                $"CoreOffsetMHz={capabilities.Core.CurrentMegahertz};" +
                $"Min={capabilities.Core.MinimumMegahertz};" +
                $"Max={capabilities.Core.MaximumMegahertz}");
            output.WriteLine(
                $"MemoryOffsetMHz={capabilities.Memory.CurrentMegahertz};" +
                $"Min={capabilities.Memory.MinimumMegahertz};" +
                $"Max={capabilities.Memory.MaximumMegahertz}");
            output.WriteLine("Success=True");
            return 0;
        }
        catch (Exception exception) when (
            exception is DllNotFoundException
            or EntryPointNotFoundException
            or InvalidOperationException)
        {
            output.WriteLine($"Failure={exception.Message}");
            output.WriteLine("Success=False");
            return 2;
        }
    }
}
