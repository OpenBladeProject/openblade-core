using System.Buffers.Binary;

namespace OpenBlade.Tray;

/// <summary>Extracts fixed reviewed NSIS data blocks; never interprets or executes an installer.</summary>
internal static class RazerKeyboardDriverVendorPackage
{
    internal static async Task ExtractVerifiedAsync(Stream archive, string destination,
        RazerKeyboardDriverRecipe recipe, CancellationToken cancellationToken)
    {
        ValidateHeader(archive, recipe.VendorHeaderOffset);
        foreach (RazerDriverArchiveBlock block in recipe.VendorBlocks
            ?? throw new InvalidDataException("A reviewed vendor archive recipe is required."))
        {
            cancellationToken.ThrowIfCancellationRequested();
            RazerKeyboardDriverPackage.PackageFile file = recipe.Files.Single(value => value.RelativePath == block.RelativePath);
            string root = Path.Combine(destination, recipe.PackageDirectory);
            string path = OutputPath(root, file.RelativePath);
            RazerKeyboardDriverPackage.EnsureNoReparsePoints(path);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            RazerKeyboardDriverPackage.EnsureNoReparsePoints(path);
            using Stream decoded = DecodeBlock(archive, block, cancellationToken);
            await using var output = new FileStream(path, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, 16384, true);
            await RazerKeyboardDriverPackage.CopyExactAsync(decoded, output, file.Length, cancellationToken).ConfigureAwait(false);
            output.Position = 0;
            await RazerKeyboardDriverPackage.VerifyHashAsync(output, file.Length, file.Sha256, cancellationToken).ConfigureAwait(false);
        }
    }

    internal static Stream DecodeBlock(Stream archive, RazerDriverArchiveBlock block, CancellationToken cancellationToken = default)
    {
        // Inputs are whole-carrier hash-pinned before reaching this decoder. These additional
        // bounds reject malformed spans and keep decoder allocation independent of archive data.
        if (!archive.CanSeek || block.Offset < 4 || block.CompressedLength < 10
            || block.CompressedLength > 2 * 1024 * 1024
            || block.Offset > archive.Length - block.CompressedLength || !block.Compressed)
        {
            throw new InvalidDataException("The vendor driver block is outside the reviewed archive.");
        }
        archive.Position = block.Offset - 4;
        Span<byte> prefix = stackalloc byte[4];
        archive.ReadExactly(prefix);
        if (BinaryPrimitives.ReadUInt32LittleEndian(prefix) != (0x80000000u | (uint)block.CompressedLength))
        {
            throw new InvalidDataException("The vendor driver block header changed.");
        }
        byte[] data = new byte[block.CompressedLength];
        archive.ReadExactly(data);
        // All five reviewed carriers use lc=3/lp=0/pb=2 with an 8 MiB dictionary.
        if (!data.AsSpan(0, 5).SequenceEqual(new byte[] { 0x5D, 0, 0, 0x80, 0 }))
        {
            throw new InvalidDataException("The vendor driver compression properties changed.");
        }
        using var compressed = new MemoryStream(data, 5, data.Length - 5, writable: false);
        using var input = new CheckedDecoderInput(compressed, cancellationToken);
        var output = new BoundedDecoderOutput(cancellationToken);
        try
        {
            var decoder = new SevenZip.Compression.LZMA.Decoder();
            decoder.SetDecoderProperties(data[..5]);
            decoder.Code(input, output, data.Length - 5, -1, null);
            output.Position = 0;
            return output;
        }
        catch (Exception exception) when (exception is SevenZip.DataErrorException or SevenZip.InvalidParamException
            or EndOfStreamException)
        {
            output.Dispose();
            throw new InvalidDataException("The reviewed vendor driver block could not be decoded.", exception);
        }
        catch
        {
            output.Dispose();
            throw;
        }
    }

    private sealed class CheckedDecoderInput(Stream inner, CancellationToken cancellationToken) : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => inner.Length;
        public override long Position { get => inner.Position; set => throw new NotSupportedException(); }
        public override int ReadByte()
        {
            cancellationToken.ThrowIfCancellationRequested();
            int value = inner.ReadByte();
            return value < 0 ? throw new EndOfStreamException("The compressed driver block ended unexpectedly.") : value;
        }
        public override int Read(byte[] buffer, int offset, int count)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int read = inner.Read(buffer, offset, count);
            return read == 0 && count != 0 ? throw new EndOfStreamException("The compressed driver block ended unexpectedly.") : read;
        }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private sealed class BoundedDecoderOutput(CancellationToken cancellationToken) : MemoryStream
    {
        public override void Write(byte[] buffer, int offset, int count)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (Length + count > 2 * 1024 * 1024)
            {
                throw new InvalidDataException("The vendor driver block exceeds its expansion bound.");
            }
            base.Write(buffer, offset, count);
        }
    }

    internal static void ValidateHeader(Stream archive, int offset)
    {
        if (!archive.CanSeek || offset < 0 || offset > archive.Length - 28)
        {
            throw new InvalidDataException("The reviewed vendor header is missing.");
        }
        archive.Position = offset;
        Span<byte> header = stackalloc byte[28];
        archive.ReadExactly(header);
        ReadOnlySpan<byte> signature = [2, 0, 0, 0, 0xEF, 0xBE, 0xAD, 0xDE,
            0x4E, 0x75, 0x6C, 0x6C, 0x73, 0x6F, 0x66, 0x74, 0x49, 0x6E, 0x73, 0x74];
        if (!header[..20].SequenceEqual(signature)
            || BinaryPrimitives.ReadUInt32LittleEndian(header[20..]) is 0 or > 65536
            || BinaryPrimitives.ReadUInt32LittleEndian(header[24..]) > archive.Length - offset)
        {
            throw new InvalidDataException("The reviewed non-solid vendor archive header changed.");
        }
    }

    internal static async Task AcquireCommonAsync(string destination, RazerKeyboardDriverRecipe recipe,
        HttpClient client, CancellationToken cancellationToken)
    {
        // This is the independently reviewed generic RAZER\DeviceContol package, not
        // another model's filter. Its exact signed contract and each model's creation
        // of that child are recorded in docs/snap-tap-vendor-artifacts.md.
        RazerKeyboardDriverRecipe commonSource = RazerKeyboardDriverRecipes.Pid02C6;
        string archivePath = Path.Combine(destination, commonSource.ArchiveName);
        await using var archive = new FileStream(archivePath, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, 16384, true);
        await RazerKeyboardDriverPackage.DownloadAsync(client, archive, commonSource, cancellationToken).ConfigureAwait(false);
        archive.Position = 0;
        await RazerKeyboardDriverPackage.VerifyHashAsync(archive, commonSource.ArchiveLength,
            commonSource.ArchiveSha256, cancellationToken).ConfigureAwait(false);
        archive.Position = 0;
        // Validate the complete ZIP inventory, but extract only the three generic common
        // files and its notice. No 02C6 model package is extracted or submitted here.
        await RazerKeyboardDriverPackage.ExtractVerifiedAsync(archive, destination, commonSource,
            commonSource.Files, cancellationToken, "rzcommonu/", recipe.PackageDirectory).ConfigureAwait(false);
        archive.Position = 0;
        await RazerKeyboardDriverPackage.ExtractVerifiedAsync(archive, destination, commonSource,
            commonSource.Files, cancellationToken, "THIRD-PARTY-NOTICE.txt", recipe.PackageDirectory).ConfigureAwait(false);
    }

    private static string OutputPath(string root, string relative)
    {
        if (relative.Contains('\\') || relative.Contains(':') || relative.Split('/').Any(part => part is "" or "." or ".."))
        {
            throw new InvalidDataException("The vendor driver output path is invalid.");
        }
        return Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar));
    }
}
