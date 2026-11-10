using System.Buffers.Binary;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;

namespace OpenBlade.Deployment;

// Restrict the SDK FDI callbacks to one in-memory cabinet and one bounded output.
// The raw table is reconciled before decompression because a name-keyed extractor
// alone cannot distinguish duplicate members or overlapping file ranges.
internal sealed class BurnFixtureCabinet
{
    private const int FileTableOffset = 44;
    private const int FileHeaderSize = 16;
    private const int BlockHeaderSize = 8;
    private const int MaxBlockSize = 32 * 1024;
    private const int MaxCompressedBlockSize = MaxBlockSize + 6144;
    private const int MaxMembers = 512;
    private const int Lzx18 = 0x1203;
    private const int ArchiveAttribute = 0x20;
    private readonly byte[] bytes;
    private readonly Dictionary<string, int> members;

    private BurnFixtureCabinet(byte[] bytes, Dictionary<string, int> members)
    {
        this.bytes = bytes;
        this.members = members;
    }

    internal int Count => members.Count;
    internal bool Contains(string name) => members.ContainsKey(name);

    internal int SizeOf(string name) => members.TryGetValue(name, out int size)
        ? size : throw Invalid("A declared Burn cabinet member is missing.");

    internal static BurnFixtureCabinet Open(byte[] bytes, bool ux, int maxExpandedBytes)
    {
        // CFHEADER plus exactly one CFFOLDER. Continuations, reserves, alternate
        // compression and multi-folder cabinets are outside the pinned fixture shape.
        if (bytes.Length < FileTableOffset || !bytes.AsSpan(0, 4).SequenceEqual("MSCF"u8)
            || U32(bytes, 4) != 0 || U32(bytes, 8) != bytes.Length || U32(bytes, 12) != 0
            || U32(bytes, 16) != FileTableOffset || U32(bytes, 20) != 0
            || bytes[24] != 3 || bytes[25] != 1 || U16(bytes, 26) != 1
            || U16(bytes, 30) != 0 || U16(bytes, 32) != 0 || U16(bytes, 34) != 0)
        {
            throw Invalid("The Burn cabinet header is outside the verified fixture layout.");
        }

        int memberCount = U16(bytes, 28);
        uint dataOffset = U32(bytes, 36);
        int blockCount = U16(bytes, 40);
        int compression = U16(bytes, 42);
        if (memberCount is < 1 or > MaxMembers || dataOffset > bytes.Length || dataOffset < FileTableOffset
            || blockCount == 0 || blockCount > (maxExpandedBytes + MaxBlockSize - 1) / MaxBlockSize
            || compression is not (0 or Lzx18))
        {
            throw Invalid("The Burn cabinet folder exceeds the fixture bounds or uses unsupported compression.");
        }

        int cursor = FileTableOffset;
        long expanded = 0;
        var members = new Dictionary<string, int>(memberCount, StringComparer.Ordinal);
        for (int index = 0; index < memberCount; index++)
        {
            if (cursor + FileHeaderSize >= dataOffset || U32(bytes, cursor + 4) != expanded
                || U16(bytes, cursor + 8) != 0 || U16(bytes, cursor + 14) != ArchiveAttribute)
            {
                throw Invalid("Burn cabinet members overlap or use unsupported metadata.");
            }

            uint size = U32(bytes, cursor);
            expanded += size;
            cursor += FileHeaderSize;
            ReadOnlySpan<byte> nameBytes = bytes.AsSpan(cursor, Math.Min(6, (int)dataOffset - cursor));
            int terminator = nameBytes.IndexOf((byte)0);
            if (terminator <= 0 || size == 0 || expanded > maxExpandedBytes)
            {
                throw Invalid("A Burn cabinet member name or expanded size is invalid.");
            }

            string name = Encoding.Latin1.GetString(nameBytes[..terminator]);
            string expected = ux && index == 0
                ? "0"
                : (ux ? "u" : "a") + (ux ? index - 1 : index).ToString(CultureInfo.InvariantCulture);
            if (name != expected)
            {
                throw Invalid("The Burn cabinet contains a duplicate, aliased or out-of-order physical member.");
            }

            members.Add(name, (int)size);
            cursor += terminator + 1;
        }

        if (cursor != dataOffset)
        {
            throw Invalid("The Burn cabinet file table does not end at the folder data.");
        }

        long totalOutput = 0;
        for (int index = 0; index < blockCount; index++)
        {
            if (cursor > bytes.Length - BlockHeaderSize)
            {
                throw Invalid("A Burn cabinet data block header is truncated.");
            }

            int compressed = U16(bytes, cursor + 4);
            int output = U16(bytes, cursor + 6);
            if (compressed is < 1 or > MaxCompressedBlockSize || output is < 1 or > MaxBlockSize
                || (index < blockCount - 1 && output != MaxBlockSize)
                || (compression == 0 && compressed != output))
            {
                throw Invalid("A Burn cabinet data block has an invalid size.");
            }

            cursor += BlockHeaderSize + compressed;
            totalOutput += output;
        }

        if (cursor != bytes.Length || totalOutput != expanded)
        {
            throw Invalid("The Burn cabinet blocks do not cover the declared file ranges.");
        }

        return new(bytes, members);
    }

    internal byte[] Extract(string name, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var extraction = new Extraction(this, name, cancellationToken);
        var error = new FdiError();
        var alloc = new Allocate(Alloc);
        var free = new Free(Marshal.FreeHGlobal);
        var open = new OpenFile(extraction.Open);
        var read = new Transfer(extraction.Read);
        var write = new Transfer(extraction.Write);
        var close = new CloseFile(extraction.Close);
        var seek = new SeekFile(extraction.Seek);
        nint context = FDICreate(alloc, free, open, read, write, close, seek, -1, ref error);
        if (context == 0)
        {
            throw Invalid("Windows Cabinet FDI could not initialize.");
        }

        bool copied;
        bool destroyed;
        try
        {
            copied = FDICopy(context, "snapshot.cab", "", 0, extraction.Notify, 0, 0);
        }
        finally
        {
            destroyed = FDIDestroy(context);
            // FDI retains these callback pointers from FDICreate through FDIDestroy.
            GC.KeepAlive(alloc);
            GC.KeepAlive(free);
            GC.KeepAlive(open);
            GC.KeepAlive(read);
            GC.KeepAlive(write);
            GC.KeepAlive(close);
            GC.KeepAlive(seek);
        }

        cancellationToken.ThrowIfCancellationRequested();
        if (!copied || !destroyed || error.HasError != 0 || !extraction.Complete)
        {
            throw Invalid($"Windows Cabinet FDI could not read the selected Burn member (error {error.Operation}).");
        }

        return extraction.Output;
    }

    private sealed class Extraction(BurnFixtureCabinet cabinet, string target, CancellationToken token)
    {
        private const int MaxInputHandles = 4;
        private const int Closed = -1;
        private const int OutputHandle = MaxInputHandles + 1;
        // FDI can open the same cabinet more than once with independent cursors.
        private readonly int[] cursors = [Closed, Closed, Closed, Closed];
        private int written;
        private bool selected;
        private bool finished;
        private readonly HashSet<string> seen = new(cabinet.Count, StringComparer.Ordinal);

        internal byte[] Output { get; } = new byte[cabinet.SizeOf(target)];
        internal bool Complete => finished && seen.Count == cabinet.Count;

        internal nint Open(nint name, int flags, int mode)
        {
            if ((flags & 3) != 0 || token.IsCancellationRequested || name == 0
                || Marshal.PtrToStringAnsi(name) != "snapshot.cab")
            {
                return -1;
            }

            int slot = Array.IndexOf(cursors, Closed);
            if (slot < 0)
            {
                return -1;
            }

            cursors[slot] = 0;
            return slot + 1;
        }

        internal uint Read(nint handle, nint buffer, uint count)
        {
            if (!TryGetInputSlot(handle, out int slot) || token.IsCancellationRequested)
            {
                return uint.MaxValue;
            }

            int position = cursors[slot];
            int available = (int)Math.Min(count, cabinet.bytes.Length - position);
            Marshal.Copy(cabinet.bytes, position, buffer, available);
            cursors[slot] += available;
            return (uint)available;
        }

        internal uint Write(nint handle, nint buffer, uint count)
        {
            if (handle != OutputHandle || !selected || finished || token.IsCancellationRequested
                || count > Output.Length - written)
            {
                return uint.MaxValue;
            }

            Marshal.Copy(buffer, Output, written, (int)count);
            written += (int)count;
            return count;
        }

        internal int Close(nint handle)
        {
            if (handle == OutputHandle)
            {
                return 0;
            }

            if (!TryGetInputSlot(handle, out int slot))
            {
                return -1;
            }

            cursors[slot] = Closed;
            return 0;
        }

        internal int Seek(nint handle, int distance, int origin)
        {
            if (!TryGetInputSlot(handle, out int slot) || token.IsCancellationRequested)
            {
                return -1;
            }

            long basis = origin switch { 0 => 0, 1 => cursors[slot], 2 => cabinet.bytes.Length, _ => -1 };
            long offset = basis + distance;
            if (basis < 0 || offset < 0 || offset > cabinet.bytes.Length)
            {
                return -1;
            }

            cursors[slot] = (int)offset;
            return cursors[slot];
        }

        private bool TryGetInputSlot(nint handle, out int slot)
        {
            slot = (int)handle - 1;
            return handle is > 0 and <= MaxInputHandles && cursors[slot] != Closed;
        }

        internal nint Notify(int kind, ref Notification notification)
        {
            if (token.IsCancellationRequested)
            {
                return -1;
            }

            switch (kind)
            {
                case 0: // fdintCABINET_INFO
                case 5: // fdintENUMERATE
                    return 0;
                case 2: // fdintCOPY_FILE
                    string? member = Marshal.PtrToStringAnsi(notification.Name);
                    if (member is null || !cabinet.members.TryGetValue(member, out int size)
                        || notification.Size != size || !seen.Add(member))
                    {
                        return -1;
                    }

                    if (member != target)
                    {
                        return 0;
                    }

                    selected = true;
                    return OutputHandle;
                case 3: // fdintCLOSE_FILE_INFO
                    if (notification.Handle != OutputHandle || !selected || finished || written != Output.Length)
                    {
                        return 0;
                    }

                    finished = true;
                    return 1;
                default: // Partial files and next cabinets are unsupported.
                    return -1;
            }
        }
    }

    private static nint Alloc(uint size)
    {
        if (size is 0 or > 4 * 1024 * 1024)
        {
            return 0;
        }

        try
        {
            return Marshal.AllocHGlobal((int)size);
        }
        catch (OutOfMemoryException)
        {
            return 0;
        }
    }

    private static InvalidDataException Invalid(string message) => new(message);
    private static ushort U16(ReadOnlySpan<byte> bytes, int offset) =>
        BinaryPrimitives.ReadUInt16LittleEndian(bytes[offset..]);
    private static uint U32(ReadOnlySpan<byte> bytes, int offset) =>
        BinaryPrimitives.ReadUInt32LittleEndian(bytes[offset..]);

    // Windows SDK 10.0.26100.0 um/fdi.h and fdi_fci_types.h.
    [StructLayout(LayoutKind.Sequential)]
    private struct FdiError
    {
        internal int Operation, Type, HasError;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Notification
    {
        internal int Size;
        internal nint Name, Name2, Name3, User, Handle;
        internal ushort Date, Time, Attributes, SetId, Cabinet, Folder;
        internal int Error;
    }

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate nint Allocate(uint size);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void Free(nint pointer);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate nint OpenFile(nint name, int flags, int mode);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate uint Transfer(nint handle, nint buffer, uint count);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int CloseFile(nint handle);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int SeekFile(nint handle, int distance, int origin);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate nint NotifyFile(int kind, ref Notification notification);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("cabinet.dll", CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    private static extern nint FDICreate(Allocate alloc, Free free, OpenFile open, Transfer read,
        Transfer write, CloseFile close, SeekFile seek, int cpu, ref FdiError error);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("cabinet.dll", CallingConvention = CallingConvention.Cdecl, ExactSpelling = true, CharSet = CharSet.Ansi)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool FDICopy(nint context, string cabinet, string path, int flags,
        NotifyFile notify, nint decrypt, nint user);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("cabinet.dll", CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool FDIDestroy(nint context);
}
