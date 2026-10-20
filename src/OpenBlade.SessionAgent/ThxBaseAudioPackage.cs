using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using OpenBlade.Tray;

namespace OpenBlade.SessionAgent;

internal sealed record ThxBaseAudioFile(string Name, long Length, string Sha256);
internal sealed record ThxBaseAudioCatalog(string FileName, IReadOnlyList<string> Members);
internal sealed record ThxBaseAudioDriverPackage(string InfName, string CatalogFile, IReadOnlyList<ThxBaseAudioFile> Files);
internal sealed record ThxBaseAudioRecipe(string Family, Uri Source, long Length, string Sha256,
    string RootInfName, int CabinetFileCount, long CabinetExpandedLength,
    IReadOnlyList<ThxBaseAudioFile> Files, IReadOnlyList<ThxBaseAudioCatalog> Catalogs,
    IReadOnlyList<ThxBaseAudioDriverPackage> Packages, IReadOnlyList<string> RootHardwareIds);
internal sealed record ThxCabinetFile(string Path, long Length);

/// <summary>Acquires the pinned base-audio closure. Never installs or binds a driver.</summary>
internal static class ThxBaseAudioPackage
{
    private const long MaximumCabinetLength = 64 * 1024 * 1024;
    private const long MaximumExpandedLength = 256 * 1024 * 1024;
    private const int MaximumFiles = 512;
    private static readonly IReadOnlyDictionary<string, ThxBaseAudioRecipe> Recipes = LoadRecipes();

    internal static ThxBaseAudioRecipe ForModel(string modelNumber) => modelNumber switch
    {
        "RZ09-0528" or "RZ09-0530" => Recipes["realtek"],
        "RZ09-0581" => Recipes["intel"],
        _ => throw new ArgumentOutOfRangeException(nameof(modelNumber)),
    };

    internal static async Task<string> AcquireVerifiedAsync(string destination, string modelNumber,
        CancellationToken cancellationToken)
    {
        ThxBaseAudioRecipe recipe = ForModel(modelNumber);
        cancellationToken.ThrowIfCancellationRequested();
        string staging = Path.GetFullPath(destination);
        ThxAudioProvisioningJournal.ValidateProtectedDirectory(Path.GetDirectoryName(staging)!);
        CreateProtectedNewDirectory(staging);
        string cabinet = Path.Combine(staging, "source.cab");
        using var handler = new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false };
        using var client = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromMinutes(5));
        using (HttpResponseMessage response = await client.GetAsync(recipe.Source,
            HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false))
        {
            // Redirects, including to another Microsoft URL, require new reviewed pins.
            if (response.StatusCode != System.Net.HttpStatusCode.OK
                || response.RequestMessage?.RequestUri != recipe.Source
                || response.Content.Headers.ContentLength is long length && length != recipe.Length)
            {
                throw new InvalidDataException("Unexpected base-audio download response.");
            }
            await using Stream input = await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false);
            await using var output = new FileStream(cabinet, FileMode.CreateNew, FileAccess.ReadWrite,
                FileShare.None, 16384, FileOptions.Asynchronous | FileOptions.WriteThrough);
            await CopyExactAsync(input, output, recipe.Length, timeout.Token).ConfigureAwait(false);
            VerifyFile(output, recipe.Length, recipe.Sha256);
            output.Flush(flushToDisk: true);
        }
        // Hold the exact archive against replacement until expansion has exited.
        using var lockedCabinet = new FileStream(cabinet, FileMode.Open, FileAccess.Read, FileShare.Read);
        VerifyFile(lockedCabinet, recipe.Length, recipe.Sha256);
        IReadOnlyList<ThxCabinetFile> table = ReadCabinetTable(lockedCabinet);
        ValidateCabinetRecipe(table, recipe);
        string scratch = Path.Combine(staging, "expanded");
        CreateProtectedNewDirectory(scratch);
        await ExpandAsync(cabinet, scratch, staging, timeout.Token).ConfigureAwait(false);
        ValidateExtractedInventory(scratch, table);
        string packageRoot = Path.Combine(staging, "package");
        CreateProtectedNewDirectory(packageRoot);
        foreach (ThxBaseAudioFile file in recipe.Files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string source = SafePath(scratch, file.Name);
            string target = SafePath(packageRoot, file.Name);
            EnsureProtectedParents(packageRoot, file.Name);
            using var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read);
            VerifyFile(input, file.Length, file.Sha256);
            input.Position = 0;
            using var output = new FileStream(target, FileMode.CreateNew, FileAccess.ReadWrite,
                FileShare.None, 16384, FileOptions.WriteThrough);
            await CopyExactAsync(input, output, file.Length, cancellationToken).ConfigureAwait(false);
            VerifyFile(output, file.Length, file.Sha256);
            output.Flush(flushToDisk: true);
        }
        using IDisposable lease = VerifyPackage(packageRoot, recipe, cancellationToken, cacheOnly: false);
        return packageRoot;
    }

    internal static IDisposable VerifyForExecution(string root, string modelNumber, CancellationToken cancellationToken) =>
        VerifyPackage(root, ForModel(modelNumber), cancellationToken, cacheOnly: true);

    private static IDisposable VerifyPackage(string root, ThxBaseAudioRecipe recipe,
        CancellationToken cancellationToken, bool cacheOnly)
    {
        ThxAudioProvisioningJournal.ValidateProtectedDirectory(root);
        foreach (ThxBaseAudioFile file in recipe.Files)
        {
            for (string? directory = Path.GetDirectoryName(SafePath(root, file.Name));
                directory is not null && !string.Equals(directory, Path.GetFullPath(root), StringComparison.OrdinalIgnoreCase);
                directory = Path.GetDirectoryName(directory))
            {
                ThxAudioProvisioningJournal.ValidateProtectedDirectory(directory);
            }
        }
        IDisposable lease = LockVerifiedFiles(root, recipe.Files, cancellationToken);
        try
        {
            foreach (ThxBaseAudioCatalog catalog in recipe.Catalogs)
            {
                RazerDriverPackageTrust.VerifyCatalogMembers(root, catalog.FileName, catalog.Members,
                    cancellationToken, cacheOnly);
            }
            return lease;
        }
        catch { lease.Dispose(); throw; }
    }

    // Kept separate from Windows catalog trust so mutation/lease behavior can be tested offline.
    internal static IDisposable LockVerifiedFiles(string root, IReadOnlyList<ThxBaseAudioFile> files,
        CancellationToken cancellationToken)
    {
        var streams = new List<FileStream>();
        try
        {
            ValidateExtractedInventory(root, files.Select(file => new ThxCabinetFile(file.Name, file.Length)).ToArray());
            foreach (ThxBaseAudioFile file in files)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var stream = new FileStream(SafePath(root, file.Name), FileMode.Open, FileAccess.Read, FileShare.Read);
                streams.Add(stream);
                VerifyFile(stream, file.Length, file.Sha256);
            }
            return new FileLease(streams);
        }
        catch { foreach (FileStream stream in streams) { stream.Dispose(); } throw; }
    }

    internal static void ValidateCabinetRecipe(IReadOnlyList<ThxCabinetFile> table, ThxBaseAudioRecipe recipe)
    {
        if (table.Count != recipe.CabinetFileCount || table.Sum(file => file.Length) != recipe.CabinetExpandedLength)
        {
            throw new InvalidDataException("Base-audio cabinet inventory changed.");
        }

        var byName = table.ToDictionary(file => file.Path, StringComparer.OrdinalIgnoreCase);
        foreach (ThxBaseAudioFile file in recipe.Files)
        {
            if (!byName.TryGetValue(file.Name, out ThxCabinetFile? entry) || entry.Length != file.Length)
            {
                throw new InvalidDataException("Base-audio cabinet is missing a pinned closure member.");
            }
        }
    }

    // MS-CAB CFHEADER/CFFOLDER/CFFILE/CFDATA. cbCabinet excludes an Authenticode trailer.
    // The complete file (including that trailer) is independently size/hash pinned before use.
    internal static IReadOnlyList<ThxCabinetFile> ReadCabinetTable(Stream stream)
    {
        if (!stream.CanSeek || stream.Length < 36 || stream.Length > MaximumCabinetLength)
        {
            throw new InvalidDataException("Invalid cabinet size.");
        }

        stream.Position = 0;
        using var reader = new BinaryReader(stream, Encoding.ASCII, leaveOpen: true);
        byte[] header = reader.ReadBytes(36);
        uint U32(int offset) => System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(offset));
        ushort U16(int offset) => System.Buffers.Binary.BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(offset));
        uint cabinetLength = U32(8), fileOffset = U32(16);
        int folders = U16(26), count = U16(28), flags = U16(30);
        if (!header.AsSpan(0, 4).SequenceEqual("MSCF"u8) || U32(4) != 0 || U32(12) != 0 || U32(20) != 0
            || header[24] != 3 || header[25] != 1 || cabinetLength < 36 || cabinetLength > stream.Length
            || folders is < 1 or > 32 || count is < 1 or > MaximumFiles || (flags & ~4) != 0 || U16(34) != 0)
        {
            throw new InvalidDataException("Unsupported or continued cabinet.");
        }

        void Require(long bytes)
        {
            if (bytes < 0 || stream.Position > cabinetLength - bytes)
            {
                throw new InvalidDataException("Truncated cabinet table.");
            }
        }
        int folderReserve = 0, dataReserve = 0;
        if ((flags & 4) != 0)
        {
            Require(4);
            int headerReserve = reader.ReadUInt16();
            folderReserve = reader.ReadByte();
            dataReserve = reader.ReadByte();
            Require(headerReserve);
            stream.Position += headerReserve;
        }
        var folderRecords = new List<(uint Offset, ushort Blocks)>();
        for (int index = 0; index < folders; index++)
        {
            Require(8 + folderReserve);
            uint offset = reader.ReadUInt32();
            ushort blocks = reader.ReadUInt16(), compression = reader.ReadUInt16();
            if (blocks == 0 || (compression & 15) > 3 || offset >= cabinetLength)
            {
                throw new InvalidDataException("Invalid cabinet folder.");
            }

            folderRecords.Add((offset, blocks));
            stream.Position += folderReserve;
        }
        if (fileOffset < stream.Position || fileOffset >= cabinetLength)
        {
            throw new InvalidDataException("Invalid cabinet file-table offset.");
        }

        stream.Position = fileOffset;
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var result = new List<ThxCabinetFile>();
        var folderEnds = new long[folders];
        long expanded = 0;
        for (int index = 0; index < count; index++)
        {
            Require(16);
            uint length = reader.ReadUInt32(), offset = reader.ReadUInt32();
            ushort folder = reader.ReadUInt16();
            _ = reader.ReadUInt16(); // DOS date/time are not trusted paths or sizes.
            _ = reader.ReadUInt16();
            ushort attributes = reader.ReadUInt16();
            var name = new StringBuilder();
            for (int character = 0; ; character++)
            {
                Require(1);
                byte value = reader.ReadByte();
                if (value == 0)
                {
                    break;
                }

                if (character >= 240 || value < 32 || value > 126)
                {
                    throw new InvalidDataException("Unsupported cabinet member name.");
                }

                name.Append((char)value);
            }
            string relative = ValidateRelativePath(name.ToString());
            if (!names.Add(relative) || folder >= folders || (attributes & 0x10) != 0
                || length > MaximumExpandedLength || offset < folderEnds[folder]
                || (long)offset + length > MaximumExpandedLength)
            {
                throw new InvalidDataException("Duplicate, overlapping, oversized or continued cabinet member.");
            }

            folderEnds[folder] = (long)offset + length;
            expanded += length;
            if (expanded > MaximumExpandedLength)
            {
                throw new InvalidDataException("Cabinet expansion exceeds its bound.");
            }

            result.Add(new(relative, length));
        }
        long tableEnd = stream.Position, previousDataEnd = tableEnd, decompressed = 0;
        for (int index = 0; index < folders; index++)
        {
            var folder = folderRecords[index];
            if (folder.Offset < previousDataEnd)
            {
                throw new InvalidDataException("Overlapping cabinet data.");
            }

            stream.Position = folder.Offset;
            long folderLength = 0;
            for (int block = 0; block < folder.Blocks; block++)
            {
                Require(8 + dataReserve);
                _ = reader.ReadUInt32();
                ushort compressed = reader.ReadUInt16(), uncompressed = reader.ReadUInt16();
                if (compressed == 0 || uncompressed is 0 or > 32768)
                {
                    throw new InvalidDataException("Split or invalid cabinet data block.");
                }

                Require(dataReserve + compressed);
                stream.Position += dataReserve + compressed;
                folderLength += uncompressed;
            }
            decompressed += folderLength;
            if (folderEnds[index] > folderLength || decompressed > MaximumExpandedLength)
            {
                throw new InvalidDataException("Cabinet folder expansion exceeds its bound.");
            }

            previousDataEnd = stream.Position;
        }
        return result.AsReadOnly();
    }

    internal static string ValidateRelativePath(string value)
    {
        string path = value.Replace('/', '\\');
        if (path.Length is 0 or > 240)
        {
            throw new InvalidDataException("Invalid package member path.");
        }

        foreach (string part in path.Split('\\'))
        {
            string stem = part.Split('.')[0].ToUpperInvariant();
            if (part.Length == 0 || part is "." or ".." || part.EndsWith(' ') || part.EndsWith('.')
                || part.Any(c => c < 32 || c > 126 || "<>:\"|?*".Contains(c))
                || stem is "CON" or "PRN" or "AUX" or "NUL" or "CONIN$" or "CONOUT$"
                || stem.Length == 4 && (stem.StartsWith("COM", StringComparison.Ordinal)
                    || stem.StartsWith("LPT", StringComparison.Ordinal)) && stem[3] is >= '0' and <= '9')
            {
                throw new InvalidDataException("Unsafe package member path.");
            }
        }
        return path;
    }

    private static string SafePath(string root, string relative)
    {
        string fullRoot = Path.GetFullPath(root), path = Path.GetFullPath(Path.Combine(fullRoot, ValidateRelativePath(relative)));
        if (!path.StartsWith(fullRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("Package path escaped its root.");
        }

        ThxAudioProvisioningJournal.RejectReparse(path);
        return path;
    }

    private static void ValidateExtractedInventory(string root, IReadOnlyList<ThxCabinetFile> expected)
    {
        var remaining = expected.ToDictionary(file => ValidateRelativePath(file.Path), file => file.Length,
            StringComparer.OrdinalIgnoreCase);
        var pending = new Stack<string>();
        pending.Push(root);
        int directories = 0;
        while (pending.TryPop(out string? directory))
        {
            ThxAudioProvisioningJournal.RejectReparse(directory);
            if (++directories > 512)
            {
                throw new InvalidDataException("Package directory count exceeded.");
            }

            foreach (string entry in Directory.EnumerateFileSystemEntries(directory))
            {
                ThxAudioProvisioningJournal.RejectReparse(entry);
                if (Directory.Exists(entry)) { pending.Push(entry); continue; }
                string relative = ValidateRelativePath(Path.GetRelativePath(root, entry));
                if (!remaining.Remove(relative, out long length) || new FileInfo(entry).Length != length)
                {
                    throw new InvalidDataException("Unexpected extracted package member.");
                }
            }
        }
        if (remaining.Count != 0)
        {
            throw new InvalidDataException("Missing extracted package member.");
        }
    }

    private static void CreateProtectedNewDirectory(string path)
    {
        ThxAudioProvisioningJournal.RejectReparse(path);
        if (Path.Exists(path))
        {
            throw new IOException("Base-audio staging must be new.");
        }

        ThxAudioProvisioningJournal.EnsureProtectedDirectory(path);
    }

    private static void EnsureProtectedParents(string root, string relative)
    {
        string? parent = Path.GetDirectoryName(ValidateRelativePath(relative));
        string current = root;
        if (string.IsNullOrEmpty(parent))
        {
            return;
        }

        foreach (string part in parent.Split(Path.DirectorySeparatorChar))
        {
            current = Path.Combine(current, part);
            ThxAudioProvisioningJournal.EnsureProtectedDirectory(current);
        }
    }

    private static void VerifyFile(Stream stream, long length, string sha256)
    {
        if (stream.Length != length)
        {
            throw new InvalidDataException("Pinned base-audio file length changed.");
        }

        stream.Position = 0;
        if (!CryptographicOperations.FixedTimeEquals(Convert.FromHexString(sha256), SHA256.HashData(stream)))
        {
            throw new InvalidDataException("Pinned base-audio file hash changed.");
        }
    }

    private static async Task CopyExactAsync(Stream input, Stream output, long length, CancellationToken cancellationToken)
    {
        byte[] buffer = new byte[16384];
        long copied = 0;
        int read;
        while ((read = await input.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) != 0)
        {
            copied += read;
            if (copied > length)
            {
                throw new InvalidDataException("Base-audio payload exceeded its pinned length.");
            }

            await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
        }
        if (copied != length)
        {
            throw new InvalidDataException("Base-audio payload is truncated.");
        }
    }

    private static async Task ExpandAsync(string cabinet, string scratch, string staging, CancellationToken cancellationToken)
    {
        string temp = Path.Combine(staging, "temp");
        CreateProtectedNewDirectory(temp);
        ProcessStartInfo start = WindowsThxAudioProvisioningPlatform.CreateStartInfo(
            Path.Combine(Environment.SystemDirectory, "expand.exe"), staging, temp, ["-F:*", cabinet, scratch]);
        using Process process = Process.Start(start) ?? throw new IOException("Windows cabinet extraction did not start.");
        using var drains = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        Task output = DrainAsync(process.StandardOutput, drains.Token);
        Task error = DrainAsync(process.StandardError, drains.Token);
        try
        {
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            await Task.WhenAll(output, error).ConfigureAwait(false);
            if (process.ExitCode != 0)
            {
                throw new InvalidDataException("Windows cabinet extraction failed.");
            }
        }
        catch
        {
            // Only terminate this extraction process; no vendor/installer process is launched here.
            await drains.CancelAsync().ConfigureAwait(false);
            try
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                }
            }
            catch (InvalidOperationException) { }
            using var stopTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            await process.WaitForExitAsync(stopTimeout.Token).ConfigureAwait(false);
            try { await Task.WhenAll(output, error).ConfigureAwait(false); } catch (OperationCanceledException) { }
            throw;
        }
    }

    private static async Task DrainAsync(StreamReader reader, CancellationToken cancellationToken)
    {
        char[] buffer = new char[4096];
        while (await reader.ReadAsync(buffer, cancellationToken).ConfigureAwait(false) != 0) { }
    }

    private static IReadOnlyDictionary<string, ThxBaseAudioRecipe> LoadRecipes()
    {
        using Stream stream = typeof(ThxBaseAudioPackage).Assembly.GetManifestResourceStream(
            "OpenBlade.SessionAgent.ThxBaseAudioPackage.json") ?? throw new InvalidDataException("Missing base-audio pins.");
        using JsonDocument document = JsonDocument.Parse(stream);
        var recipes = new Dictionary<string, ThxBaseAudioRecipe>(StringComparer.Ordinal);
        foreach (JsonProperty property in document.RootElement.EnumerateObject())
        {
            JsonElement item = property.Value;
            string Text(string name) => item.GetProperty(name).GetString()!;
            var files = item.GetProperty("files").EnumerateArray().Select(file => new ThxBaseAudioFile(
                ValidateRelativePath(file.GetProperty("path").GetString()!), file.GetProperty("length").GetInt64(),
                file.GetProperty("sha256").GetString()!)).ToArray();
            var catalogs = item.GetProperty("catalogs").EnumerateArray().Select(catalog => new ThxBaseAudioCatalog(
                ValidateRelativePath(catalog.GetProperty("fileName").GetString()!),
                Array.AsReadOnly(catalog.GetProperty("members").EnumerateArray()
                    .Select(member => ValidateRelativePath(member.GetString()!)).ToArray()))).ToArray();
            var packages = item.GetProperty("packages").EnumerateArray().Select(package => new ThxBaseAudioDriverPackage(
                package.GetProperty("infName").GetString()!, package.GetProperty("catalogFile").GetString()!,
                Array.AsReadOnly(package.GetProperty("members").EnumerateArray().Select(member =>
                    files.Single(file => string.Equals(file.Name, member.GetString(), StringComparison.OrdinalIgnoreCase))).ToArray()))).ToArray();
            var source = new Uri(Text("source"));
            if (source.Scheme != Uri.UriSchemeHttps || source.Host != "catalog.s.download.windowsupdate.com"
                || source.UserInfo.Length != 0 || source.Fragment.Length != 0 || !source.IsDefaultPort)
            {
                throw new InvalidDataException("Unreviewed base-audio source.");
            }

            recipes.Add(property.Name, new(Text("family"), source, item.GetProperty("length").GetInt64(),
                Text("sha256"), ValidateRelativePath(Text("rootInf")), item.GetProperty("cabinetFileCount").GetInt32(),
                item.GetProperty("cabinetExpandedLength").GetInt64(), Array.AsReadOnly(files), Array.AsReadOnly(catalogs), Array.AsReadOnly(packages),
                Array.AsReadOnly(item.GetProperty("rootHardwareIds").EnumerateArray().Select(id => id.GetString()!).ToArray())));
        }
        return new System.Collections.ObjectModel.ReadOnlyDictionary<string, ThxBaseAudioRecipe>(recipes);
    }

    private sealed class FileLease(List<FileStream> streams) : IDisposable
    {
        public void Dispose()
        {
            foreach (FileStream stream in streams)
            {
                stream.Dispose();
            }
        }
    }
}
