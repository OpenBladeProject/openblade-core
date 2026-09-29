using System.Runtime.InteropServices;
using System.Text;

namespace OpenBlade.Capture;

// Read-only public NVAPI definitions: https://github.com/NVIDIA/nvapi/blob/main/nvapi.h
// IDs: https://github.com/NVIDIA/nvapi/blob/main/nvapi_interface.h
// No setter is imported, resolved, or invoked.

internal static class NvidiaNvapiReadOnlyNative
{
    // nvapi.h: clock entry=44, voltage entry=24, P-state=456 bytes.
    // V2 size = 20 + 16*(8 + 8*44 + 4*24) + (4 + 4*24) = 7416.
    private const int Size = 20 + 16 * (8 + 8 * 44 + 4 * 24) + 4 + 4 * 24;
    private const int Version = (3 << 16) | Size;
    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    private struct ClockLayout
    {
        public uint Domain, Type, Flags;
        public int Delta, Min, Max;
        public uint U0, U1, U2, U3, U4;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    private struct VoltageLayout
    {
        public uint Domain, Flags, Value;
        public int Delta, Min, Max;
    }

    [DllImport("nvapi64.dll", EntryPoint = "nvapi_QueryInterface", CallingConvention = CallingConvention.Cdecl)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern IntPtr QueryInterface(uint id);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int Simple();
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int Enumerate([Out, MarshalAs(UnmanagedType.LPArray, SizeConst = 64)] IntPtr[] handles, out uint count);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl, CharSet = CharSet.Ansi)] private delegate int Name(IntPtr gpu, StringBuilder name);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int Pstates(IntPtr gpu, IntPtr data);
    private static T Resolve<T>(uint id) where T : Delegate
    {
        IntPtr p = QueryInterface(id);
        if (p == IntPtr.Zero)
        {
            throw new InvalidOperationException("Missing read-only NVAPI entry 0x" + id.ToString("X8"));
        }

        return Marshal.GetDelegateForFunctionPointer<T>(p);
    }
    private static void Check(int status, string action)
    {
        if (status != 0)
        {
            throw new InvalidOperationException(action + " returned NVAPI status " + status);
        }
    }
    internal static void ValidateLayout()
    {
        if (Marshal.SizeOf(typeof(ClockLayout)) != 44 || Marshal.SizeOf(typeof(VoltageLayout)) != 24 ||
            Marshal.OffsetOf(typeof(ClockLayout), "Delta").ToInt32() != 12)
        {
            throw new InvalidOperationException("Unexpected native structure layout.");
        }
    }
    public sealed class Clock
    {
        public bool Editable;
        public int OffsetKHz, MinimumKHz, MaximumKHz;
    }
    public sealed class Result
    {
        public string Device = string.Empty;
        public Clock? Core, Memory;
    }
    private static Clock ReadClock(IntPtr data, int offset)
    {
        int flags = Marshal.ReadInt32(data, offset + 8);
        int kind = Marshal.ReadInt32(data, offset + 4);
        if ((flags & ~1) != 0 || (kind != 0 && kind != 1))
        {
            throw new InvalidOperationException("Unsupported clock layout flags/type.");
        }

        int value = Marshal.ReadInt32(data, offset + 12), min = Marshal.ReadInt32(data, offset + 16), max = Marshal.ReadInt32(data, offset + 20);
        if (min > max || value < min || value > max)
        {
            throw new InvalidOperationException("Invalid P0 offset range.");
        }

        return new Clock
        {
            Editable = (flags & 1) != 0,
            OffsetKHz = value,
            MinimumKHz = min,
            MaximumKHz = max,
        };
    }
    internal static Result Read()
    {
        ValidateLayout();
        Simple initialize = Resolve<Simple>(0x0150e828), unload = Resolve<Simple>(0xd22bdd7e);
        bool initialized = false;
        IntPtr data = IntPtr.Zero;
        try
        {
            Check(initialize(), "Initialize");
            initialized = true;
            IntPtr[] handles = new IntPtr[64];
            Check(Resolve<Enumerate>(0xe5ac921f)(handles, out uint count), "EnumPhysicalGPUs");
            if (count != 1 || handles[0] == IntPtr.Zero)
            {
                throw new InvalidOperationException("Exactly one NVIDIA physical GPU is required; count=" + count);
            }

            var name = new StringBuilder(64);
            Check(Resolve<Name>(0xceee8e9f)(handles[0], name), "GPU_GetFullName");
            if (name.Length == 0)
            {
                throw new InvalidOperationException("Missing GPU name.");
            }

            data = Marshal.AllocHGlobal(Size);
            Marshal.Copy(new byte[Size], 0, data, Size);
            Marshal.WriteInt32(data, 0, Version);
            Check(Resolve<Pstates>(0x6ff81213)(handles[0], data), "GPU_GetPstates20");
            if (Marshal.ReadInt32(data, 0) != Version)
            {
                throw new InvalidOperationException("NVAPI structure version changed.");
            }

            int flags = Marshal.ReadInt32(data, 4), states = Marshal.ReadInt32(data, 8), clocks = Marshal.ReadInt32(data, 12), volts = Marshal.ReadInt32(data, 16);
            if ((flags & ~1) != 0 || states < 1 || states > 16 || clocks < 1 || clocks > 8 || volts < 0 || volts > 4 ||
                Marshal.ReadInt32(data, 7316) < 0 || Marshal.ReadInt32(data, 7316) > 4)
            {
                throw new InvalidOperationException("Invalid NVAPI structure counts/flags.");
            }

            Result result = new() { Device = name.ToString() };
            int found = 0;
            for (int s = 0; s < states; s++)
            {
                int p = 20 + s * 456;
                if (Marshal.ReadInt32(data, p) != 0)
                {
                    continue;
                }

                found++;
                int pf = Marshal.ReadInt32(data, p + 4);
                if ((pf & ~1) != 0)
                {
                    throw new InvalidOperationException("Invalid P0 flags.");
                }

                for (int c = 0; c < clocks; c++)
                {
                    int entry = p + 8 + c * 44, domain = Marshal.ReadInt32(data, entry);
                    if (domain == 0)
                    {
                        if (result.Core != null)
                        {
                            throw new InvalidOperationException("Duplicate graphics domain.");
                        }
                        result.Core = ReadClock(data, entry);
                    }
                    if (domain == 4)
                    {
                        if (result.Memory != null)
                        {
                            throw new InvalidOperationException("Duplicate memory domain.");
                        }
                        result.Memory = ReadClock(data, entry);
                    }
                }
            }
            if (found != 1 || result.Core == null || result.Memory == null)
            {
                throw new InvalidOperationException("Unique P0 graphics and memory entries were not returned.");
            }

            if (!result.Core.Editable || !result.Memory.Editable)
            {
                throw new InvalidOperationException("P0 clock offsets are not editable.");
            }

            return result;
        }
        finally
        {
            if (data != IntPtr.Zero)
            {
                Marshal.FreeHGlobal(data);
            }

            if (initialized)
            {
                Check(unload(), "Unload");
            }
        }
    }
}

internal sealed class NvidiaNvapiGpuClockOffsetProbe : IGpuClockOffsetProbe
{
    public GpuClockOffsetCapabilities Query()
    {
        var result = NvidiaNvapiReadOnlyNative.Read();
        return new(result.Device, Convert(result.Core!), Convert(result.Memory!));
    }

    private static GpuClockOffsetRange Convert(NvidiaNvapiReadOnlyNative.Clock clock)
    {
        if (clock.OffsetKHz % 1000 != 0 || clock.MinimumKHz % 1000 != 0 || clock.MaximumKHz % 1000 != 0)
        {
            throw new InvalidOperationException("NVAPI returned offsets not representable in whole MHz.");
        }
        return new(clock.MinimumKHz / 1000, clock.MaximumKHz / 1000, clock.OffsetKHz / 1000);
    }
}
