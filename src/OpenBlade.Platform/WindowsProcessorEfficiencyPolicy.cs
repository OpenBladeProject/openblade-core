using System.Runtime.InteropServices;

namespace OpenBlade.Platform;

public sealed record ProcessorEfficiencyPolicyStatus(
    bool HybridTopologyDetected,
    int EfficientLogicalProcessorCount,
    int PerformanceLogicalProcessorCount,
    string Message);

public sealed class WindowsProcessorEfficiencyPolicy
{
    private readonly IWindowsProcessorPolicyApi api;
    private readonly uint[] efficientProcessorIds;

    private WindowsProcessorEfficiencyPolicy(
        IWindowsProcessorPolicyApi api,
        uint[] efficientProcessorIds,
        ProcessorEfficiencyPolicyStatus status)
    {
        this.api = api;
        this.efficientProcessorIds = efficientProcessorIds;
        Status = status;
    }

    public ProcessorEfficiencyPolicyStatus Status { get; }

    public static WindowsProcessorEfficiencyPolicy Discover() =>
        Discover(WindowsProcessorPolicyApi.Instance);

    internal static WindowsProcessorEfficiencyPolicy Discover(
        IWindowsProcessorPolicyApi api)
    {
        ArgumentNullException.ThrowIfNull(api);
        IReadOnlyList<WindowsCpuSet> available;
        try
        {
            available = api.GetAvailableCpuSets();
        }
        catch (Exception exception)
        {
            return Unavailable(api, $"Processor topology is unavailable: {exception.Message}");
        }

        WindowsCpuSet[] processors = available
            .Where(cpu => !cpu.Allocated || cpu.AllocatedToTargetProcess)
            .OrderBy(cpu => cpu.Group)
            .ThenBy(cpu => cpu.LogicalProcessorIndex)
            .ToArray();
        if (processors.Length == 0)
        {
            return Unavailable(api, "Windows reported no CPU sets available to OpenBlade.");
        }

        byte minimumEfficiencyClass = processors.Min(cpu => cpu.EfficiencyClass);
        byte maximumEfficiencyClass = processors.Max(cpu => cpu.EfficiencyClass);
        if (minimumEfficiencyClass == maximumEfficiencyClass)
        {
            return new WindowsProcessorEfficiencyPolicy(
                api,
                [],
                new ProcessorEfficiencyPolicyStatus(
                    HybridTopologyDetected: false,
                    EfficientLogicalProcessorCount: processors.Length,
                    PerformanceLogicalProcessorCount: 0,
                    Message: "The processor has one efficiency class; Windows scheduling remains automatic."));
        }

        uint[] efficientIds = processors
            .Where(cpu => cpu.EfficiencyClass == minimumEfficiencyClass)
            .Select(cpu => cpu.Id)
            .ToArray();
        int performanceCount = processors.Length - efficientIds.Length;

        return new WindowsProcessorEfficiencyPolicy(
            api,
            efficientIds,
            new ProcessorEfficiencyPolicyStatus(
                HybridTopologyDetected: true,
                EfficientLogicalProcessorCount: efficientIds.Length,
                PerformanceLogicalProcessorCount: performanceCount,
                Message: "Efficient processors are available for hidden tray work; process scheduling remains automatic."));
    }

    public bool TryPreferEfficientProcessorsForCurrentThread()
    {
        try
        {
            return Status.HybridTopologyDetected
                && api.SetCurrentThreadCpuSets(efficientProcessorIds);
        }
        catch
        {
            return false;
        }
    }

    public bool TryAllowAllProcessorsForCurrentThread()
    {
        try
        {
            return Status.HybridTopologyDetected
                && api.ClearCurrentThreadCpuSets();
        }
        catch
        {
            return false;
        }
    }

    public bool TrySetProcessEcoQos(bool enabled)
    {
        try
        {
            return api.SetProcessEcoQos(enabled);
        }
        catch
        {
            return false;
        }
    }

    private static WindowsProcessorEfficiencyPolicy Unavailable(
        IWindowsProcessorPolicyApi api,
        string message) => new(
            api,
            [],
            new ProcessorEfficiencyPolicyStatus(
                HybridTopologyDetected: false,
                EfficientLogicalProcessorCount: 0,
                PerformanceLogicalProcessorCount: 0,
                Message: message));
}

internal readonly record struct WindowsCpuSet(
    uint Id,
    ushort Group,
    byte LogicalProcessorIndex,
    byte EfficiencyClass,
    bool Allocated,
    bool AllocatedToTargetProcess);

internal interface IWindowsProcessorPolicyApi
{
    IReadOnlyList<WindowsCpuSet> GetAvailableCpuSets();

    bool SetCurrentThreadCpuSets(uint[] cpuSetIds);

    bool ClearCurrentThreadCpuSets();

    bool SetProcessEcoQos(bool enabled);
}

internal sealed class WindowsProcessorPolicyApi : IWindowsProcessorPolicyApi
{
    private const uint CpuSetInformationType = 0;
    private const byte CpuSetAllocated = 0x02;
    private const byte CpuSetAllocatedToTargetProcess = 0x04;
    private const int ProcessPowerThrottlingInformationClass = 4;
    private const uint PowerThrottlingCurrentVersion = 1;
    private const uint PowerThrottlingExecutionSpeed = 0x1;

    public static WindowsProcessorPolicyApi Instance { get; } = new();

    private WindowsProcessorPolicyApi()
    {
    }

    public IReadOnlyList<WindowsCpuSet> GetAvailableCpuSets()
    {
        _ = GetSystemCpuSetInformation(
            0,
            0,
            out uint requiredLength,
            GetCurrentProcess(),
            0);
        if (requiredLength == 0 || requiredLength > int.MaxValue)
        {
            return [];
        }

        nint buffer = Marshal.AllocHGlobal(checked((int)requiredLength));
        try
        {
            if (!GetSystemCpuSetInformation(
                    buffer,
                    requiredLength,
                    out uint returnedLength,
                    GetCurrentProcess(),
                    0))
            {
                return [];
            }

            int knownSize = Marshal.SizeOf<SystemCpuSetInformation>();
            var result = new List<WindowsCpuSet>();
            uint offset = 0;
            while (offset < returnedLength)
            {
                nint current = buffer + checked((int)offset);
                uint entrySize = unchecked((uint)Marshal.ReadInt32(current));
                if (entrySize < 8 || entrySize > returnedLength - offset)
                {
                    break;
                }

                uint type = unchecked((uint)Marshal.ReadInt32(current, 4));
                if (type == CpuSetInformationType && entrySize >= knownSize)
                {
                    SystemCpuSetInformation information =
                        Marshal.PtrToStructure<SystemCpuSetInformation>(current);
                    result.Add(new WindowsCpuSet(
                        information.CpuSet.Id,
                        information.CpuSet.Group,
                        information.CpuSet.LogicalProcessorIndex,
                        information.CpuSet.EfficiencyClass,
                        (information.CpuSet.AllFlags & CpuSetAllocated) != 0,
                        (information.CpuSet.AllFlags & CpuSetAllocatedToTargetProcess) != 0));
                }

                offset += entrySize;
            }

            return result;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    public bool SetCurrentThreadCpuSets(uint[] cpuSetIds) =>
        SetThreadSelectedCpuSets(
            GetCurrentThread(),
            cpuSetIds,
            checked((uint)cpuSetIds.Length));

    public bool ClearCurrentThreadCpuSets() =>
        SetThreadSelectedCpuSets(GetCurrentThread(), null, 0);

    public bool SetProcessEcoQos(bool enabled)
    {
        var state = new ProcessPowerThrottlingState
        {
            Version = PowerThrottlingCurrentVersion,
            ControlMask = enabled ? PowerThrottlingExecutionSpeed : 0,
            StateMask = enabled ? PowerThrottlingExecutionSpeed : 0,
        };
        return SetProcessInformation(
            GetCurrentProcess(),
            ProcessPowerThrottlingInformationClass,
            ref state,
            checked((uint)Marshal.SizeOf<ProcessPowerThrottlingState>()));
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SystemCpuSetInformation
    {
        public uint Size;
        public uint Type;
        public SystemCpuSet CpuSet;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SystemCpuSet
    {
        public uint Id;
        public ushort Group;
        public byte LogicalProcessorIndex;
        public byte CoreIndex;
        public byte LastLevelCacheIndex;
        public byte NumaNodeIndex;
        public byte EfficiencyClass;
        public byte AllFlags;
        public uint Reserved;
        public ulong AllocationTag;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessPowerThrottlingState
    {
        public uint Version;
        public uint ControlMask;
        public uint StateMask;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetSystemCpuSetInformation(
        nint information,
        uint bufferLength,
        out uint returnedLength,
        nint process,
        uint flags);

    [DllImport("kernel32.dll")]
    private static extern nint GetCurrentProcess();

    [DllImport("kernel32.dll")]
    private static extern nint GetCurrentThread();

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetThreadSelectedCpuSets(
        nint thread,
        [In] uint[]? cpuSetIds,
        uint cpuSetIdCount);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetProcessInformation(
        nint process,
        int processInformationClass,
        ref ProcessPowerThrottlingState processInformation,
        uint processInformationSize);
}
