using System.Buffers.Binary;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace OpenBlade.Deployment;

// Restrict the SDK FDI callbacks to one range of the held snapshot and one bounded output.
// The raw table is reconciled before decompression because a name-keyed extractor
// alone cannot distinguish duplicate members or overlapping file ranges.
internal sealed class BurnFixtureCabinet
{
    private const int CabinetHeaderSize = 36;
    private const int FolderHeaderSize = 8;
    private const int FileHeaderSize = 16;
    private const int BlockHeaderSize = 8;
    private const int MaxBlockSize = 32 * 1024;
    private const int MaxCompressedBlockSize = MaxBlockSize + 6144;
    private const int MaxMembers = 512;
    private const int Lzx18 = 0x1203;
    private const int ArchiveAttribute = 0x20;
    private readonly Stream snapshot;
    private readonly int offset;
    private readonly int length;
    private readonly Dictionary<string, int> members;

    private BurnFixtureCabinet(Stream snapshot, int offset, int length, Dictionary<string, int> members)
    {
        this.snapshot = snapshot;
        this.offset = offset;
        this.length = length;
        this.members = members;
    }

    internal int Count => members.Count;
    internal bool Contains(string name) => members.ContainsKey(name);

    internal int SizeOf(string name) => members.TryGetValue(name, out int size)
        ? size : throw Invalid("A declared Burn cabinet member is missing.");

    internal static BurnFixtureCabinet Open(Stream snapshot, int offset, int length, bool ux,
        int maxExpandedBytes, int maxMemberBytes, CancellationToken cancellationToken, bool authoredUx = false)
    {
        var members = new Dictionary<string, int>(StringComparer.Ordinal);
        var cabinet = new BurnFixtureCabinet(snapshot, offset, length, members);

        // Authored WiX UX can use two folders and deduplicate identical UX files.
        // Setup payloads retain the single-folder, disjoint-member contract.
        if (length < CabinetHeaderSize + FolderHeaderSize)
        {
            throw Invalid("The Burn cabinet header is truncated.");
        }

        Span<byte> header = stackalloc byte[CabinetHeaderSize];
        cabinet.ReadAt(0, header, cancellationToken);
        int folderCount = U16(header, 26);
        int fileTableOffset = CabinetHeaderSize + folderCount * FolderHeaderSize;
        if (!header[..4].SequenceEqual("MSCF"u8)
            || U32(header, 4) != 0 || U32(header, 8) != length || U32(header, 12) != 0
            || U32(header, 16) != fileTableOffset || U32(header, 20) != 0
            || header[24] != 3 || header[25] != 1 || folderCount < 1
            || folderCount > (ux && authoredUx ? 2 : 1) || fileTableOffset > length
            || U16(header, 30) != 0 || U16(header, 32) != 0 || U16(header, 34) != 0)
        {
            throw Invalid("The Burn cabinet header is outside the verified fixture layout.");
        }

        int memberCount = U16(header, 28);
        var folders = new Folder[folderCount];
        Span<byte> folderHeader = stackalloc byte[FolderHeaderSize];
        for (int index = 0; index < folderCount; index++)
        {
            cabinet.ReadAt(CabinetHeaderSize + index * FolderHeaderSize, folderHeader, cancellationToken);
            uint offsetInCabinet = U32(folderHeader, 0);
            int blocks = U16(folderHeader, 4);
            int compression = U16(folderHeader, 6);
            if (offsetInCabinet > length || offsetInCabinet < fileTableOffset
                || blocks == 0 || blocks > maxExpandedBytes / MaxBlockSize
                || compression is not (0 or Lzx18))
            {
                throw Invalid("The Burn cabinet folder exceeds the fixture bounds or uses unsupported compression.");
            }
            folders[index] = new((int)offsetInCabinet, blocks, compression);
        }

        if (memberCount is < 1 or > MaxMembers)
        {
            throw Invalid("The Burn cabinet folder exceeds the fixture bounds or uses unsupported compression.");
        }

        int cursor = fileTableOffset;
        int currentFolder = 0;
        int previousOffset = -1, previousSize = 0;
        long totalDeclared = 0;
        long expanded = 0;
        Span<byte> file = stackalloc byte[FileHeaderSize + 6];
        for (int index = 0; index < memberCount; index++)
        {
            int available = folders[0].Offset - cursor;
            if (available <= FileHeaderSize)
            {
                throw Invalid("A Burn cabinet file header is truncated.");
            }

            Span<byte> entry = file[..Math.Min(file.Length, available)];
            cabinet.ReadAt(cursor, entry, cancellationToken);
            int folder = U16(entry, 8);
            if (folder == currentFolder + 1 && folder < folderCount && expanded > 0)
            {
                folders[currentFolder].Expanded = (int)expanded;
                currentFolder = folder;
                expanded = 0;
                previousOffset = -1;
            }

            uint memberOffset = U32(entry, 4);
            uint size = U32(entry, 0);
            bool deduplicatedUx = ux && authoredUx && index > 1
                && memberOffset == previousOffset && size == previousSize;
            if (folder != currentFolder || (memberOffset != expanded && !deduplicatedUx)
                || U16(entry, 14) != ArchiveAttribute)
            {
                throw Invalid("Burn cabinet members overlap or use unsupported metadata.");
            }

            totalDeclared += size;
            if (!deduplicatedUx)
            {
                expanded += size;
            }
            ReadOnlySpan<byte> nameBytes = entry[FileHeaderSize..];
            int terminator = nameBytes.IndexOf((byte)0);
            if (terminator <= 0 || size == 0 || size > maxMemberBytes || totalDeclared > maxExpandedBytes)
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
            previousOffset = (int)memberOffset;
            previousSize = (int)size;
            cursor += FileHeaderSize + terminator + 1;
        }

        if (cursor != folders[0].Offset || currentFolder != folderCount - 1)
        {
            throw Invalid("The Burn cabinet file table does not end at the folder data.");
        }

        folders[currentFolder].Expanded = (int)expanded;
        Span<byte> block = stackalloc byte[BlockHeaderSize];
        foreach (Folder folder in folders)
        {
            if (cursor != folder.Offset)
            {
                throw Invalid("The Burn cabinet folder ranges overlap or leave gaps.");
            }

            long totalOutput = 0;
            for (int index = 0; index < folder.Blocks; index++)
            {
                if (cursor > length - BlockHeaderSize)
                {
                    throw Invalid("A Burn cabinet data block header is truncated.");
                }

                cabinet.ReadAt(cursor, block, cancellationToken);
                int compressed = U16(block, 4);
                int output = U16(block, 6);
                if (compressed is < 1 or > MaxCompressedBlockSize || output is < 1 or > MaxBlockSize
                    || (index < folder.Blocks - 1 && output != MaxBlockSize)
                    || (folder.Compression == 0 && compressed != output))
                {
                    throw Invalid("A Burn cabinet data block has an invalid size.");
                }

                cursor += BlockHeaderSize + compressed;
                totalOutput += output;
            }

            if (totalOutput != folder.Expanded)
            {
                throw Invalid("The Burn cabinet blocks do not cover the declared file ranges.");
            }
        }

        if (cursor != length)
        {
            throw Invalid("The Burn cabinet blocks do not cover the declared file ranges.");
        }

        return cabinet;
    }

    private sealed record Folder(int Offset, int Blocks, int Compression)
    {
        internal int Expanded { get; set; }
    }

    private void ReadAt(int position, Span<byte> destination, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        snapshot.Position = (long)offset + position;
        snapshot.ReadExactly(destination);
    }

    internal byte[] Extract(string name, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        byte[] output = new byte[SizeOf(name)];
        Expand(name, output, null, null, cancellationToken);
        return output;
    }

    internal (byte[] Sha512, byte[] Sha256) HashMember(string name, CancellationToken cancellationToken)
    {
        using var sha512 = IncrementalHash.CreateHash(HashAlgorithmName.SHA512);
        using var sha256 = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        Expand(name, null, sha512, sha256, cancellationToken);
        return (sha512.GetHashAndReset(), sha256.GetHashAndReset());
    }

    private void Expand(string name, byte[]? output, IncrementalHash? sha512, IncrementalHash? sha256,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var extraction = new Extraction(this, name, output, sha512, sha256, cancellationToken);
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
        if (extraction.ReadError is { } readError)
        {
            throw new InvalidDataException("Could not read the held Burn cabinet range.", readError);
        }

        if (!copied || !destroyed || error.HasError != 0 || !extraction.Complete)
        {
            throw Invalid($"Windows Cabinet FDI could not read the selected Burn member (error {error.Operation}).");
        }
    }

    private sealed class Extraction(BurnFixtureCabinet cabinet, string target, byte[]? output,
        IncrementalHash? sha512, IncrementalHash? sha256, CancellationToken token)
    {
        private const int MaxInputHandles = 4;
        private const int Closed = -1;
        private const int OutputHandle = MaxInputHandles + 1;
        // FDI can open the same cabinet more than once with independent cursors.
        private readonly int[] cursors = [Closed, Closed, Closed, Closed];
        private readonly byte[] input = new byte[64 * 1024];
        private int written;
        private bool selected;
        private bool finished;
        private readonly HashSet<string> seen = new(cabinet.Count, StringComparer.Ordinal);

        internal Exception? ReadError { get; private set; }
        private readonly int outputLength = cabinet.SizeOf(target);
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
            int available = (int)Math.Min(count, cabinet.length - position);
            try
            {
                for (int copied = 0; copied < available;)
                {
                    int chunk = Math.Min(input.Length, available - copied);
                    cabinet.ReadAt(position + copied, input.AsSpan(0, chunk), token);
                    Marshal.Copy(input, 0, buffer + copied, chunk);
                    copied += chunk;
                }
            }
            catch (Exception exception)
            {
                // Stream I/O and cancellation must not unwind through native FDI.
                ReadError ??= exception;
                return uint.MaxValue;
            }

            cursors[slot] += available;
            return (uint)available;
        }

        internal uint Write(nint handle, nint buffer, uint count)
        {
            if (handle != OutputHandle || !selected || finished || token.IsCancellationRequested
                || count > outputLength - written)
            {
                return uint.MaxValue;
            }

            try
            {
                if (output is not null)
                {
                    Marshal.Copy(buffer, output, written, (int)count);
                }
                else
                {
                    for (int offset = 0; offset < count;)
                    {
                        int chunk = Math.Min(input.Length, (int)count - offset);
                        Marshal.Copy(buffer + offset, input, 0, chunk);
                        sha512!.AppendData(input, 0, chunk);
                        sha256!.AppendData(input, 0, chunk);
                        offset += chunk;
                    }
                }
                written += (int)count;
                return count;
            }
            catch (CryptographicException exception)
            {
                ReadError = exception;
                return uint.MaxValue;
            }
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

            long basis = origin switch { 0 => 0, 1 => cursors[slot], 2 => cabinet.length, _ => -1 };
            long offset = basis + distance;
            if (basis < 0 || offset < 0 || offset > cabinet.length)
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
                    if (notification.Handle != OutputHandle || !selected || finished || written != outputLength)
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
