#if OPENBLADE_CANDIDATE_RECEIPT_FIXTURE
using System.Security.Cryptography;
using OpenBlade.Deployment;

namespace OpenBlade.DeploymentBroker;

// Single NativeAOT code artifact. Windows' creation-time Microsoft-signing
// policy owns external image admission; namespace membership is not a loader seal.
internal sealed class ScratchReceiverClosure : IDisposable
{
    internal const string Stem = "OpenBlade.CandidateReceipt.Scratch";
    internal WindowsCandidateReceiptStorage Storage { get; }
    private readonly string directory;
    private readonly WindowsCandidateReceiptStorage.DirectoryLease directoryLease;
    private readonly FileStream image;
    private readonly byte[] digest;

    private ScratchReceiverClosure(WindowsCandidateReceiptStorage storage)
    {
        Storage = storage;
        directory = Path.GetDirectoryName(ScratchCandidateLaunch.ReceiverPath(storage))!;
        directoryLease = storage.PinDirectory(directory);
        try
        {
            image = storage.OpenFile(Path.Combine(directory, ScratchCandidateLaunch.ReceiverName));
            try
            {
                RequireNativeImage(image);
                RequireMembers();
                digest = ComputeDigest();
            }
            catch { image.Dispose(); throw; }
        }
        catch { directoryLease.Dispose(); throw; }
    }

    internal static ScratchReceiverClosure Retain(WindowsCandidateReceiptStorage storage) => new(storage);
    internal string ImagePath => Path.Combine(directory, ScratchCandidateLaunch.ReceiverName);

    internal static void RequireNativeImage(Stream stream)
    {
        if (stream.Length is < 256 or > 64 * 1024 * 1024)
        {
            throw new InvalidDataException("The scratch receiver has an invalid native image size.");
        }
        stream.Position = 0;
        byte[] bytes = new byte[checked((int)stream.Length)];
        stream.ReadExactly(bytes);
        stream.Position = 0;
        if (!AcceptNativeImage(bytes))
        {
            throw new InvalidDataException("The scratch receiver requires its native x64 AOT executable.");
        }
    }

    // Format discrimination, not authenticity. Initial build provenance and
    // retained bytes establish the fixture's admitted code artifact.
    internal static bool AcceptNativeImage(byte[] bytes)
    {
        try
        {
            ushort U16(int offset) => System.Buffers.Binary.BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(offset, 2));
            uint U32(int offset) => System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(offset, 4));
            if (U16(0) != 0x5a4d) { return false; }
            int pe = checked((int)U32(60));
            int optional = checked(pe + 24);
            int sections = U16(pe + 6);
            if (U32(pe) != 0x4550 || U16(pe + 4) != 0x8664 || sections is < 1 or > 96
                || (U16(pe + 22) & 0x2002) != 2 || U16(pe + 20) < 240
                || U16(optional) != 0x20b || U32(optional + 16) == 0 || U32(optional + 108) < 16
                || U32(optional + 112) == 0 || U32(optional + 116) == 0
                || U32(optional + 216) != 0 || U32(optional + 220) != 0
                || U32(optional + 224) != 0 || U32(optional + 228) != 0) { return false; }
            int Offset(uint rva, int size)
            {
                for (int i = 0; i < sections; i++)
                {
                    int section = checked(optional + U16(pe + 20) + 40 * i);
                    uint start = U32(section + 12), rawSize = U32(section + 16);
                    if (rva >= start && (ulong)rva - start + (uint)size <= rawSize)
                    {
                        int offset = checked((int)((ulong)U32(section + 20) + rva - start));
                        _ = bytes.AsSpan(offset, size);
                        return offset;
                    }
                }
                throw new InvalidDataException();
            }
            int exports = Offset(U32(optional + 112), 40);
            if (U32(exports + 24) != 1) { return false; }
            int names = Offset(U32(exports + 32), 4);
            int name = Offset(U32(names), 25);
            return bytes.AsSpan(name, 25).SequenceEqual("DotNetRuntimeDebugHeader\0"u8);
        }
        catch (Exception error) when (error is ArgumentOutOfRangeException or OverflowException or InvalidDataException)
        {
            return false;
        }
    }

    private void RequireMembers()
    {
        string[] actual = Directory.EnumerateFileSystemEntries(directory)
            .Select(Path.GetFileName).Order(StringComparer.Ordinal).ToArray()!;
        string[] expected = File.Exists(Path.Combine(directory, "openblade-scratch-candidate-receipt.exe"))
            ? [ScratchCandidateLaunch.ReceiverName, "openblade-scratch-candidate-receipt.exe"]
            : [ScratchCandidateLaunch.ReceiverName];
        if (!actual.SequenceEqual(expected.Order(StringComparer.Ordinal)))
        {
            throw new InvalidDataException("The scratch receiver contains obsolete or unexpected members.");
        }
    }

    private byte[] ComputeDigest()
    {
        if (image.Length is < 1 or > 64 * 1024 * 1024)
        {
            throw new InvalidDataException("The native scratch receiver exceeds its size bound.");
        }
        using var encoded = new MemoryStream();
        using var writer = new BinaryWriter(encoded);
        writer.Write("OpenBlade.ScratchNativeReceiver.v2");
        writer.Write(ScratchCandidateLaunch.ReceiverName);
        writer.Write(image.Length);
        image.Position = 0;
        writer.Write(SHA256.HashData(image));
        writer.Flush();
        return SHA256.HashData(encoded.ToArray());
    }

    internal byte[] RequireUnchanged()
    {
        RequireMembers();
        if (!CryptographicOperations.FixedTimeEquals(digest, ComputeDigest()))
        {
            throw new InvalidDataException("The retained native scratch receiver changed.");
        }
        return digest.ToArray();
    }

    public void Dispose() => DisposeRetainedResources([image, directoryLease]);

    internal static void DisposeRetainedResources(IEnumerable<IDisposable> resources)
    {
        List<Exception> failures = [];
        foreach (var resource in resources)
        {
            try { resource.Dispose(); }
            catch (Exception failure) { failures.Add(failure); }
        }
        if (failures.Count != 0)
        {
            throw new AggregateException("The native scratch receiver could not release every retained resource.", failures);
        }
    }
}
#endif
