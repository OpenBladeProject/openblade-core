using System.Buffers;
using System.ComponentModel;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using System.Security.Cryptography;

namespace OpenBlade.Tray;

/// <summary>Extracts a reviewed flat driver CAB through the Windows data-only cabinet API.</summary>
internal static class RazerKeyboardDriverCabinet
{
    private const uint CabinetInfoNotification = 0x00000010;
    private const uint FileInCabinetNotification = 0x00000011;
    private const uint NeedNewCabinetNotification = 0x00000012;
    private const uint FileExtractedNotification = 0x00000013;
    private const uint FileOperationAbort = 0;
    private const uint FileOperationDoIt = 1;
    private const uint NoError = 0;
    private const uint ErrorInvalidData = 13;
    private const int MaximumPath = 260;
    private const int MaximumFiles = 128;

    private static readonly FileCallback Callback = OnCabinetNotification;
    private static readonly int FullTargetNameOffset =
        Marshal.OffsetOf<FileInCabinetInfoPrefix>(nameof(FileInCabinetInfoPrefix.FullTargetName)).ToInt32();

    internal sealed record OutputPlan(string SourceName, string RelativePath, long Length, string Sha256);

    internal static void ExtractVerified(string cabinetPath, string destination,
        RazerKeyboardDriverRecipe recipe, CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("Windows is required to extract the driver cabinet.");
        }
        ArgumentNullException.ThrowIfNull(recipe);
        cancellationToken.ThrowIfCancellationRequested();

        string cabinet = Path.GetFullPath(cabinetPath);
        string root = Path.GetFullPath(destination);
        RazerKeyboardDriverPackage.EnsureNoReparsePoints(cabinet);
        RazerKeyboardDriverPackage.EnsureNoReparsePoints(root);
        if (!File.Exists(cabinet) || !Directory.Exists(root) || cabinet.Length >= MaximumPath)
        {
            throw new IOException("The reviewed driver cabinet or protected destination is unavailable.");
        }
        if (!IsFlatName(recipe.PackageDirectory))
        {
            throw new InvalidDataException("The driver package directory is invalid.");
        }

        IReadOnlyList<RazerKeyboardDriverPackage.PackageFile> cabinetFiles = recipe.CabinetFiles
            ?? throw new InvalidDataException("The driver recipe does not define a reviewed cabinet inventory.");
        IReadOnlyList<OutputPlan> plan = CreatePlan(cabinetFiles, recipe.Files);
        var expected = cabinetFiles.ToDictionary(file => file.RelativePath, StringComparer.Ordinal);
        string scratch = CreateScratch(root);
        var state = new ExtractionState(expected, cabinet, scratch, cancellationToken);
        var stateHandle = GCHandle.Alloc(state);
        bool succeeded;
        int nativeError;
        try
        {
            succeeded = SetupIterateCabinet(cabinet, 0, Callback, GCHandle.ToIntPtr(stateHandle));
            nativeError = Marshal.GetLastWin32Error();
        }
        finally
        {
            stateHandle.Free();
        }

        if (state.Error is not null)
        {
            ExceptionDispatchInfo.Capture(state.Error).Throw();
        }
        if (!succeeded)
        {
            throw new Win32Exception(nativeError, "Windows could not extract the reviewed driver cabinet.");
        }
        cancellationToken.ThrowIfCancellationRequested();
        if (state.Seen.Count != expected.Count || state.Extracted.Count != expected.Count)
        {
            throw new InvalidDataException("The driver cabinet did not produce its complete reviewed inventory.");
        }

        using VerifiedSources sources = VerifySources(scratch, expected, cancellationToken);
        string packageRoot = Path.Combine(root, recipe.PackageDirectory);
        RazerKeyboardDriverPackage.EnsureNoReparsePoints(packageRoot);
        if (Directory.Exists(packageRoot) || File.Exists(packageRoot))
        {
            throw new IOException("The driver package destination already exists.");
        }
        Directory.CreateDirectory(packageRoot);
        RazerKeyboardDriverPackage.EnsureNoReparsePoints(packageRoot);
        foreach (OutputPlan output in plan)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string target = SafeOutputPath(packageRoot, output.RelativePath);
            RazerKeyboardDriverPackage.EnsureNoReparsePoints(target);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            RazerKeyboardDriverPackage.EnsureNoReparsePoints(target);
            FileStream source = sources.Files[output.SourceName];
            source.Position = 0;
            using var destinationFile = new FileStream(target, FileMode.CreateNew, FileAccess.ReadWrite,
                FileShare.None, 16384, FileOptions.None);
            CopyExact(source, destinationFile, output.Length, cancellationToken);
            destinationFile.Flush();
            if (!Hash(destinationFile, cancellationToken).Equals(output.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException("An extracted driver output differs from its reviewed hash.");
            }
        }
    }

    internal static IReadOnlyList<OutputPlan> CreatePlan(
        IReadOnlyList<RazerKeyboardDriverPackage.PackageFile> cabinetFiles,
        IReadOnlyList<RazerKeyboardDriverPackage.PackageFile> outputFiles)
    {
        ArgumentNullException.ThrowIfNull(cabinetFiles);
        ArgumentNullException.ThrowIfNull(outputFiles);
        if (cabinetFiles.Count is < 1 or > MaximumFiles || outputFiles.Count is < 1 or > MaximumFiles)
        {
            throw new InvalidDataException("The driver cabinet inventory is outside its reviewed bound.");
        }

        var sources = new Dictionary<string, RazerKeyboardDriverPackage.PackageFile>(StringComparer.OrdinalIgnoreCase);
        foreach (RazerKeyboardDriverPackage.PackageFile file in cabinetFiles)
        {
            ValidateFile(file);
            if (!IsFlatName(file.RelativePath) || !sources.TryAdd(file.RelativePath, file))
            {
                throw new InvalidDataException("The driver cabinet contains an invalid or duplicate member name.");
            }
        }

        var outputs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var usedSources = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var plan = new List<OutputPlan>(outputFiles.Count);
        foreach (RazerKeyboardDriverPackage.PackageFile file in outputFiles)
        {
            ValidateFile(file);
            ValidateRelativePath(file.RelativePath);
            if (!outputs.Add(file.RelativePath))
            {
                throw new InvalidDataException("The driver package maps more than one output to the same path.");
            }
            string name = Path.GetFileName(file.RelativePath);
            if (!sources.TryGetValue(name, out RazerKeyboardDriverPackage.PackageFile? source)
                || source.Length != file.Length
                || !source.Sha256.Equals(file.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException("A driver package output has no matching reviewed cabinet member.");
            }
            usedSources.Add(source.RelativePath);
            plan.Add(new(source.RelativePath, file.RelativePath, file.Length, file.Sha256));
        }
        if (usedSources.Count != sources.Count)
        {
            throw new InvalidDataException("The driver package does not consume the complete reviewed cabinet inventory.");
        }
        return plan.AsReadOnly();
    }

    private static uint OnCabinetNotification(nint context, uint notification, nuint param1, nuint param2)
    {
        ExtractionState state = (ExtractionState)GCHandle.FromIntPtr(context).Target!;
        if (state.Error is not null)
        {
            return notification == FileInCabinetNotification ? FileOperationAbort : ErrorInvalidData;
        }
        try
        {
            state.CancellationToken.ThrowIfCancellationRequested();
            switch (notification)
            {
                case CabinetInfoNotification:
                    return NoError; // The single hash-pinned source cabinet; additional cabinets remain refused.
                case FileInCabinetNotification:
                    return AdmitEntry(state, (nint)param1);
                case FileExtractedNotification:
                    return ConfirmExtraction(state, (nint)param1);
                case NeedNewCabinetNotification:
                    throw new InvalidDataException("The reviewed driver package requires an unexpected additional cabinet.");
                default:
                    throw new InvalidDataException("Windows reported an unexpected cabinet notification.");
            }
        }
        catch (Exception exception)
        {
            state.Error ??= exception;
            return notification == FileInCabinetNotification ? FileOperationAbort : ErrorInvalidData;
        }
    }

    private static uint AdmitEntry(ExtractionState state, nint parameter)
    {
        FileInCabinetInfoPrefix info = Marshal.PtrToStructure<FileInCabinetInfoPrefix>(parameter);
        string name = Marshal.PtrToStringUni(info.NameInCabinet)
            ?? throw new InvalidDataException("The driver cabinet member name is unavailable.");
        if (info.Win32Error != NoError)
        {
            throw new Win32Exception((int)info.Win32Error);
        }
        if (!state.Expected.TryGetValue(name, out RazerKeyboardDriverPackage.PackageFile? expected)
            || !state.Seen.Add(name) || info.FileSize != expected.Length || !IsFlatName(name))
        {
            throw new InvalidDataException("The driver cabinet contains an unapproved member or size.");
        }

        string target = Path.Combine(state.Scratch, name);
        if (target.Length >= MaximumPath || File.Exists(target) || Directory.Exists(target))
        {
            throw new InvalidDataException("The driver cabinet target path is unavailable.");
        }
        RazerKeyboardDriverPackage.EnsureNoReparsePoints(target);
        char[] buffer = new char[MaximumPath];
        target.CopyTo(0, buffer, 0, target.Length);
        Marshal.Copy(buffer, 0, parameter + FullTargetNameOffset, buffer.Length);
        state.Targets.Add(target);
        return FileOperationDoIt;
    }

    private static uint ConfirmExtraction(ExtractionState state, nint parameter)
    {
        FilePaths paths = Marshal.PtrToStructure<FilePaths>(parameter);
        if (paths.Win32Error != NoError)
        {
            throw new Win32Exception((int)paths.Win32Error);
        }
        string target = Marshal.PtrToStringUni(paths.Target)
            ?? throw new InvalidDataException("Windows did not report the extracted cabinet target.");
        string source = Marshal.PtrToStringUni(paths.Source)
            ?? throw new InvalidDataException("Windows did not report the cabinet source.");
        string normalizedTarget = Path.GetFullPath(target);
        if (!state.Targets.Contains(normalizedTarget) || !state.Extracted.Add(normalizedTarget)
            || !File.Exists(normalizedTarget) || Directory.Exists(normalizedTarget)
            || !Path.GetFullPath(source).Equals(state.CabinetName, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("Windows extracted an unexpected cabinet target.");
        }
        RazerKeyboardDriverPackage.EnsureNoReparsePoints(normalizedTarget);
        state.CancellationToken.ThrowIfCancellationRequested();
        return NoError;
    }

    private static VerifiedSources VerifySources(string scratch,
        IReadOnlyDictionary<string, RazerKeyboardDriverPackage.PackageFile> expected,
        CancellationToken cancellationToken)
    {
        string[] actual = Directory.GetFileSystemEntries(scratch, "*", SearchOption.TopDirectoryOnly);
        if (actual.Length != expected.Count)
        {
            throw new InvalidDataException("The extracted cabinet inventory contains unexpected entries.");
        }
        var actualNames = new HashSet<string>(
            actual.Select(path => Path.GetFileName(path)), StringComparer.Ordinal);
        if (actualNames.Count != expected.Count || expected.Keys.Any(name => !actualNames.Contains(name)))
        {
            throw new InvalidDataException("The extracted cabinet inventory names changed.");
        }

        var sources = new Dictionary<string, FileStream>(StringComparer.OrdinalIgnoreCase);
        try
        {
            foreach ((string name, RazerKeyboardDriverPackage.PackageFile file) in expected)
            {
                cancellationToken.ThrowIfCancellationRequested();
                string path = Path.Combine(scratch, name);
                RazerKeyboardDriverPackage.EnsureNoReparsePoints(path);
                var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
                sources.Add(name, stream);
                if (stream.Length != file.Length
                    || !Hash(stream, cancellationToken).Equals(file.Sha256, StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidDataException("An extracted cabinet member differs from its reviewed hash.");
                }
            }
            return new(sources);
        }
        catch
        {
            foreach (FileStream stream in sources.Values)
            {
                stream.Dispose();
            }
            throw;
        }
    }

    private static string CreateScratch(string root)
    {
        for (int attempt = 0; attempt < 8; attempt++)
        {
            string path = Path.Combine(root, ".cabinet-" + Guid.NewGuid().ToString("N"));
            if (Directory.Exists(path) || File.Exists(path))
            {
                continue;
            }
            RazerKeyboardDriverPackage.EnsureNoReparsePoints(path);
            Directory.CreateDirectory(path);
            RazerKeyboardDriverPackage.EnsureNoReparsePoints(path);
            if (!Directory.EnumerateFileSystemEntries(path).Any())
            {
                return path;
            }
        }
        throw new IOException("A unique driver cabinet scratch directory could not be created.");
    }

    private static string SafeOutputPath(string root, string relative)
    {
        ValidateRelativePath(relative);
        string path = Path.GetFullPath(Path.Combine(root,
            relative.Replace('/', Path.DirectorySeparatorChar)));
        string prefix = root.EndsWith(Path.DirectorySeparatorChar) ? root : root + Path.DirectorySeparatorChar;
        if (!path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("The driver package output path escapes its protected root.");
        }
        return path;
    }

    private static void ValidateRelativePath(string relative)
    {
        if (relative.Contains('\\') || relative.Contains(':')
            || relative.Split('/').Any(part => part is "" or "." or ".."))
        {
            throw new InvalidDataException("The driver package output path is invalid.");
        }
    }

    private static bool IsFlatName(string value) => !string.IsNullOrEmpty(value)
        && value == Path.GetFileName(value) && !value.Contains('\\') && !value.Contains('/')
        && !value.Contains(':') && value is not "." and not "..";

    private static void ValidateFile(RazerKeyboardDriverPackage.PackageFile file)
    {
        if (file.Length < 0 || file.Length > uint.MaxValue || file.Sha256.Length != 64)
        {
            throw new InvalidDataException("The reviewed cabinet file pin is invalid.");
        }
        try
        {
            if (Convert.FromHexString(file.Sha256).Length != 32)
            {
                throw new InvalidDataException("The reviewed cabinet file hash is invalid.");
            }
        }
        catch (FormatException exception)
        {
            throw new InvalidDataException("The reviewed cabinet file hash is invalid.", exception);
        }
    }

    private static void CopyExact(Stream source, Stream destination, long expectedLength,
        CancellationToken cancellationToken)
    {
        byte[] buffer = ArrayPool<byte>.Shared.Rent(16384);
        try
        {
            long total = 0;
            int read;
            while ((read = source.Read(buffer, 0, buffer.Length)) != 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                total += read;
                if (total > expectedLength)
                {
                    throw new InvalidDataException("An extracted cabinet member exceeded its reviewed size.");
                }
                destination.Write(buffer, 0, read);
            }
            if (total != expectedLength)
            {
                throw new InvalidDataException("An extracted cabinet member was incomplete.");
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private static string Hash(Stream stream, CancellationToken cancellationToken)
    {
        stream.Position = 0;
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        byte[] buffer = ArrayPool<byte>.Shared.Rent(16384);
        try
        {
            int read;
            while ((read = stream.Read(buffer, 0, buffer.Length)) != 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                hash.AppendData(buffer, 0, read);
            }
            return Convert.ToHexString(hash.GetHashAndReset());
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private sealed class ExtractionState(
        IReadOnlyDictionary<string, RazerKeyboardDriverPackage.PackageFile> expected,
        string cabinetName, string scratch, CancellationToken cancellationToken)
    {
        internal IReadOnlyDictionary<string, RazerKeyboardDriverPackage.PackageFile> Expected { get; } = expected;
        internal string CabinetName { get; } = cabinetName;
        internal string Scratch { get; } = scratch;
        internal CancellationToken CancellationToken { get; } = cancellationToken;
        internal HashSet<string> Seen { get; } = new(StringComparer.OrdinalIgnoreCase);
        internal HashSet<string> Targets { get; } = new(StringComparer.OrdinalIgnoreCase);
        internal HashSet<string> Extracted { get; } = new(StringComparer.OrdinalIgnoreCase);
        internal Exception? Error { get; set; }
    }

    private sealed class VerifiedSources(Dictionary<string, FileStream> files) : IDisposable
    {
        internal Dictionary<string, FileStream> Files { get; } = files;
        public void Dispose()
        {
            foreach (FileStream stream in Files.Values)
            {
                stream.Dispose();
            }
        }
    }

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate uint FileCallback(nint context, uint notification, nuint param1, nuint param2);

    [StructLayout(LayoutKind.Sequential)]
    private struct FileInCabinetInfoPrefix
    {
        internal nint NameInCabinet;
        internal uint FileSize;
        internal uint Win32Error;
        internal ushort DosDate;
        internal ushort DosTime;
        internal ushort DosAttribs;
        internal ushort FullTargetName;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FilePaths
    {
        internal nint Target;
        internal nint Source;
        internal uint Win32Error;
        internal uint Flags;
    }

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("setupapi.dll", EntryPoint = "SetupIterateCabinetW", CharSet = CharSet.Unicode,
        ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetupIterateCabinet(string cabinetFile, uint reserved,
        FileCallback callback, nint context);
}
