using System.Runtime.InteropServices;

namespace OpenBlade.Service;

internal interface IGpuActivitySource
{
    bool IsActive();
}

internal sealed class InactiveGpuActivitySource : IGpuActivitySource
{
    internal static InactiveGpuActivitySource Instance { get; } = new();

    public bool IsActive() => false;
}

/// <summary>
/// Reads Windows' existing WDDM GPU-engine counters and never calls NVIDIA's
/// driver API. This lets the temperature source decide whether NVML is safe to
/// initialize without waking an idle discrete GPU as part of the decision.
/// </summary>
internal sealed class WindowsGpuActivitySource : IGpuActivitySource, IDisposable
{
    private const string CounterPath = @"\GPU Engine(*)\Utilization Percentage";
    private const double ActiveThresholdPercent = 0.1;
    private const uint PdhFormatDouble = 0x00000200;
    private const uint PdhFormatNoCap100 = 0x00008000;
    private const uint PdhMoreData = 0x800007D2;
    private const uint PdhStatusValidData = 0;
    private const uint PdhStatusNewData = 1;

    private readonly object sync = new();
    private readonly string? nvidiaLuidToken;
    private nint query;
    private nint counter;
    private bool disposed;

    public WindowsGpuActivitySource()
    {
        nvidiaLuidToken = NvidiaAdapterLocator.TryGetLuidToken();
        if (nvidiaLuidToken is null
            || PdhOpenQueryW(null, 0, out query) != 0
            || PdhAddEnglishCounterW(query, CounterPath, 0, out counter) != 0)
        {
            CloseQuery();
        }
    }

    public bool IsActive()
    {
        lock (sync)
        {
            if (disposed || query == 0 || counter == 0 || nvidiaLuidToken is null)
            {
                return false;
            }

            try
            {
                if (PdhCollectQueryData(query) != 0)
                {
                    return false;
                }

                uint bufferSize = 0;
                uint itemCount = 0;
                uint status = PdhGetFormattedCounterArrayW(
                    counter,
                    PdhFormatDouble | PdhFormatNoCap100,
                    ref bufferSize,
                    out itemCount,
                    0);
                if (status != PdhMoreData || bufferSize == 0 || itemCount == 0)
                {
                    return false;
                }

                nint buffer = Marshal.AllocHGlobal(checked((int)bufferSize));
                try
                {
                    status = PdhGetFormattedCounterArrayW(
                        counter,
                        PdhFormatDouble | PdhFormatNoCap100,
                        ref bufferSize,
                        out itemCount,
                        buffer);
                    if (status != 0)
                    {
                        return false;
                    }

                    int itemSize = Marshal.SizeOf<PdhFormattedCounterValueItem>();
                    for (uint index = 0; index < itemCount; index++)
                    {
                        PdhFormattedCounterValueItem item =
                            Marshal.PtrToStructure<PdhFormattedCounterValueItem>(
                                nint.Add(buffer, checked((int)index * itemSize)));
                        string? instance = Marshal.PtrToStringUni(item.Name);
                        if (instance?.Contains(
                                nvidiaLuidToken,
                                StringComparison.OrdinalIgnoreCase) == true
                            && item.Value.Status is PdhStatusValidData or PdhStatusNewData
                            && double.IsFinite(item.Value.DoubleValue)
                            && item.Value.DoubleValue > ActiveThresholdPercent)
                        {
                            return true;
                        }
                    }
                    return false;
                }
                finally
                {
                    Marshal.FreeHGlobal(buffer);
                }
            }
            catch (DllNotFoundException)
            {
                return false;
            }
            catch (EntryPointNotFoundException)
            {
                return false;
            }
        }
    }

    public void Dispose()
    {
        lock (sync)
        {
            if (disposed)
            {
                return;
            }
            disposed = true;
            CloseQuery();
        }
    }

    private void CloseQuery()
    {
        if (query != 0)
        {
            _ = PdhCloseQuery(query);
        }
        query = 0;
        counter = 0;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PdhFormattedCounterValueItem
    {
        public nint Name;
        public PdhFormattedCounterValue Value;
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct PdhFormattedCounterValue
    {
        [FieldOffset(0)]
        public uint Status;

        [FieldOffset(8)]
        public double DoubleValue;
    }

    [DllImport("pdh.dll", CharSet = CharSet.Unicode)]
    private static extern uint PdhOpenQueryW(
        string? dataSource,
        nuint userData,
        out nint query);

    [DllImport("pdh.dll", CharSet = CharSet.Unicode)]
    private static extern uint PdhAddEnglishCounterW(
        nint query,
        string fullCounterPath,
        nuint userData,
        out nint counter);

    [DllImport("pdh.dll")]
    private static extern uint PdhCollectQueryData(nint query);

    [DllImport("pdh.dll", CharSet = CharSet.Unicode)]
    private static extern uint PdhGetFormattedCounterArrayW(
        nint counter,
        uint format,
        ref uint bufferSize,
        out uint itemCount,
        nint itemBuffer);

    [DllImport("pdh.dll")]
    private static extern uint PdhCloseQuery(nint query);
}

internal static class NvidiaAdapterLocator
{
    private const uint NvidiaVendorId = 0x10DE;
    private const int EnumAdapters1VtableSlot = 12;
    private const int GetDescription1VtableSlot = 10;
    private static readonly Guid DxgiFactory1Interface =
        new("770AAE78-F26F-4DBA-A829-253C83D1B387");

    public static string? TryGetLuidToken()
    {
        nint factory = 0;
        try
        {
            Guid interfaceId = DxgiFactory1Interface;
            if (CreateDXGIFactory1(ref interfaceId, out factory) < 0 || factory == 0)
            {
                return null;
            }

            EnumAdapters1Delegate enumerate = GetVtableDelegate<EnumAdapters1Delegate>(
                factory,
                EnumAdapters1VtableSlot);
            for (uint index = 0; ; index++)
            {
                int result = enumerate(factory, index, out nint adapter);
                if (result < 0 || adapter == 0)
                {
                    return null;
                }

                try
                {
                    GetDescription1Delegate getDescription =
                        GetVtableDelegate<GetDescription1Delegate>(
                            adapter,
                            GetDescription1VtableSlot);
                    if (getDescription(adapter, out DxgiAdapterDescription1 description) >= 0
                        && description.VendorId == NvidiaVendorId)
                    {
                        return FormattableString.Invariant(
                            $"luid_0x{unchecked((uint)description.AdapterLuid.HighPart):x8}_0x{description.AdapterLuid.LowPart:x8}");
                    }
                }
                finally
                {
                    _ = Marshal.Release(adapter);
                }
            }
        }
        catch (DllNotFoundException)
        {
            return null;
        }
        catch (EntryPointNotFoundException)
        {
            return null;
        }
        catch (MarshalDirectiveException)
        {
            return null;
        }
        finally
        {
            if (factory != 0)
            {
                _ = Marshal.Release(factory);
            }
        }
    }

    private static TDelegate GetVtableDelegate<TDelegate>(nint instance, int slot)
        where TDelegate : Delegate
    {
        nint vtable = Marshal.ReadIntPtr(instance);
        nint method = Marshal.ReadIntPtr(vtable, checked(slot * nint.Size));
        return Marshal.GetDelegateForFunctionPointer<TDelegate>(method);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct AdapterLuid
    {
        public uint LowPart;
        public int HighPart;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DxgiAdapterDescription1
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string Description;
        public uint VendorId;
        public uint DeviceId;
        public uint SubSystemId;
        public uint Revision;
        public nuint DedicatedVideoMemory;
        public nuint DedicatedSystemMemory;
        public nuint SharedSystemMemory;
        public AdapterLuid AdapterLuid;
        public uint Flags;
    }

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int EnumAdapters1Delegate(
        nint factory,
        uint adapterIndex,
        out nint adapter);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int GetDescription1Delegate(
        nint adapter,
        out DxgiAdapterDescription1 description);

    [DllImport("dxgi.dll", ExactSpelling = true)]
    private static extern int CreateDXGIFactory1(
        ref Guid interfaceId,
        out nint factory);
}
