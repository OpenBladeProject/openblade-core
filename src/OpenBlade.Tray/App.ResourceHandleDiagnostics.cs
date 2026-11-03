#if OPENBLADE_DEVELOPER_HARNESS
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace OpenBlade.Tray;

public partial class App
{
    private static ResourceHandleSnapshot CaptureResourceHandleTypes()
    {
        const int informationClass = 51;
        const int maximumBytes = 8 * 1024 * 1024;
        long started = Stopwatch.GetTimestamp();
        DateTimeOffset capturedAtUtc = DateTimeOffset.UtcNow;
        int attempts = 0;
        ResourceHandleSnapshot Unavailable(string reason) => new(false, capturedAtUtc,
            Stopwatch.GetElapsedTime(started).TotalMilliseconds, attempts, null, null, reason);
        // PHNT ntpsapi.h PROCESS_HANDLE_SNAPSHOT_INFORMATION and
        // PROCESS_HANDLE_TABLE_ENTRY_INFO. This internal API is diagnostic-only:
        // https://raw.githubusercontent.com/winsiderss/phnt/master/ntpsapi.h
        if (!OperatingSystem.IsWindows() || RuntimeInformation.ProcessArchitecture != Architecture.X64
            || Marshal.SizeOf<ResourceNativeHandleHeader>() != 16
            || Marshal.SizeOf<ResourceNativeHandleEntry>() != 40
            || Marshal.OffsetOf<ResourceNativeHandleEntry>(nameof(ResourceNativeHandleEntry.ObjectTypeIndex)).ToInt32() != 28)
        {
            return Unavailable("Unsupported current-process snapshot ABI.");
        }
        try
        {
            int bytes = 64 * 1024;
            while (attempts < 4)
            {
                if (Stopwatch.GetElapsedTime(started) > TimeSpan.FromMilliseconds(500))
                {
                    return Unavailable("Current-process handle snapshot work budget exceeded.");
                }
                nint buffer = Marshal.AllocHGlobal(bytes);
                try
                {
                    attempts++;
                    int status = QueryResourceProcessInformation(new nint(-1), informationClass,
                        buffer, checked((uint)bytes), out uint returned);
                    if (status is unchecked((int)0xC0000004) or unchecked((int)0xC0000023))
                    {
                        long next = Math.Max((long)bytes * 2, returned);
                        if (next > maximumBytes) { return Unavailable("Current-process handle snapshot exceeds its buffer bound."); }
                        bytes = checked((int)next);
                        continue;
                    }
                    if (status < 0) { return Unavailable($"Current-process handle query returned 0x{unchecked((uint)status):X8}."); }
                    if (returned < 16 || returned > bytes) { return Unavailable("Invalid current-process handle snapshot length."); }
                    nuint count = Marshal.PtrToStructure<ResourceNativeHandleHeader>(buffer).NumberOfHandles;
                    if (count > (returned - 16) / 40) { return Unavailable("Invalid current-process handle snapshot count."); }
                    var counts = new SortedDictionary<uint, int>();
                    for (int index = 0; index < checked((int)count); index++)
                    {
                        if (index % 1024 == 0 && Stopwatch.GetElapsedTime(started) > TimeSpan.FromMilliseconds(500))
                        {
                            return Unavailable("Current-process handle parsing work budget exceeded.");
                        }
                        uint type = unchecked((uint)Marshal.ReadInt32(buffer, checked(16 + index * 40 + 28)));
                        counts[type] = counts.GetValueOrDefault(type) + 1;
                    }
                    return new ResourceHandleSnapshot(true, capturedAtUtc,
                        Stopwatch.GetElapsedTime(started).TotalMilliseconds, attempts,
                        checked((int)count), counts, null);
                }
                finally { Marshal.FreeHGlobal(buffer); }
            }
            return Unavailable("Current-process handle snapshot retry bound reached.");
        }
        catch (Exception exception) when (exception is DllNotFoundException or EntryPointNotFoundException or OverflowException)
        {
            return Unavailable(exception.Message);
        }
    }

    // No process opening, handle duplication, object-name query or system-wide
    // enumeration. Work/allocations/retries are bounded; an in-progress OS syscall
    // cannot be canceled, so the runner's owned-process deadline remains the guard.
    [DllImport("ntdll.dll", EntryPoint = "NtQueryInformationProcess")]
    private static extern int QueryResourceProcessInformation(nint process, int informationClass,
        nint buffer, uint bytes, out uint returnedBytes);

    [StructLayout(LayoutKind.Sequential)]
    private struct ResourceNativeHandleHeader
    {
        public nuint NumberOfHandles;
        public nuint Reserved;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ResourceNativeHandleEntry
    {
        public nint HandleValue;
        public nuint HandleCount;
        public nuint PointerCount;
        public uint GrantedAccess;
        public uint ObjectTypeIndex;
        public uint HandleAttributes;
        public uint Reserved;
    }

    private sealed record ResourceHandleSnapshot(bool Available, DateTimeOffset CapturedAtUtc,
        double ElapsedMilliseconds, int Attempts, int? HandleCount,
        SortedDictionary<uint, int>? TypeCounts, string? UnavailableReason);

    private static ResourceHandleTypeNames CaptureResourceHandleTypeNames()
    {
        ResourceHandleTypeNames Unavailable(string reason) => new(false, null, reason);
        if (!OperatingSystem.IsWindowsVersionAtLeast(6, 3) || RuntimeInformation.ProcessArchitecture != Architecture.X64)
        {
            return Unavailable("Unsupported object-type metadata ABI.");
        }
        // PHNT ntobapi.h and System Informer hndlinfo.h: first record aligned
        // after ULONG count; x64 record104, TypeIndex90, next104+aligned MaxLength.
        // This enumerates kernel TYPE metadata, never objects or foreign handles.
        int bytes = 16 * 1024;
        long started = Stopwatch.GetTimestamp();
        try
        {
            for (int attempt = 0; attempt < 4; attempt++)
            {
                if (Stopwatch.GetElapsedTime(started) > TimeSpan.FromMilliseconds(500))
                {
                    return Unavailable("Object-type metadata work budget exceeded.");
                }
                nint buffer = Marshal.AllocHGlobal(bytes);
                try
                {
                    int status = QueryResourceObjectInformation(0, 3, buffer, checked((uint)bytes), out uint returned);
                    if (status is unchecked((int)0xC0000004) or unchecked((int)0xC0000023))
                    {
                        long next = Math.Max((long)bytes * 2, returned);
                        if (next > 1024 * 1024) { return Unavailable("Object-type metadata exceeds its buffer bound."); }
                        bytes = checked((int)next);
                        continue;
                    }
                    if (status < 0) { return Unavailable($"Object-type metadata query returned 0x{unchecked((uint)status):X8}."); }
                    if (returned < 8 || returned > bytes) { return Unavailable("Invalid object-type metadata length."); }
                    uint count = unchecked((uint)Marshal.ReadInt32(buffer));
                    if (count is 0 or > 256) { return Unavailable("Object-type metadata count exceeds its bound."); }
                    var names = new SortedDictionary<uint, string>();
                    int offset = 8;
                    for (int index = 0; index < count; index++)
                    {
                        if (offset > (long)returned - 104) { return Unavailable("Truncated object-type metadata record."); }
                        ushort length = unchecked((ushort)Marshal.ReadInt16(buffer, offset));
                        ushort maximum = unchecked((ushort)Marshal.ReadInt16(buffer, offset + 2));
                        nint pointer = Marshal.ReadIntPtr(buffer, offset + 8);
                        long stringOffset = checked(pointer.ToInt64() - buffer.ToInt64());
                        if (length == 0 || length % 2 != 0 || maximum % 2 != 0 || length > maximum
                            || stringOffset < offset + 104 || stringOffset > returned || maximum > returned - stringOffset)
                        {
                            return Unavailable("Invalid object-type metadata string.");
                        }
                        uint type = Marshal.ReadByte(buffer, offset + 90);
                        if (type == 0 || !names.TryAdd(type, Marshal.PtrToStringUni(pointer, length / 2)!))
                        {
                            return Unavailable("Ambiguous object-type metadata index.");
                        }
                        offset = checked(offset + 104 + ((maximum + 7) & ~7));
                    }
                    return new ResourceHandleTypeNames(true, names, null);
                }
                finally { Marshal.FreeHGlobal(buffer); }
            }
            return Unavailable("Object-type metadata retry bound reached.");
        }
        catch (Exception exception) when (exception is DllNotFoundException or EntryPointNotFoundException or OverflowException)
        {
            return Unavailable(exception.Message);
        }
    }

    [DllImport("ntdll.dll", EntryPoint = "NtQueryObject")]
    private static extern int QueryResourceObjectInformation(nint handle, int informationClass,
        nint buffer, uint bytes, out uint returnedBytes);

    private sealed record ResourceHandleTypeNames(bool Available, SortedDictionary<uint, string>? Names, string? UnavailableReason);
}
#endif
