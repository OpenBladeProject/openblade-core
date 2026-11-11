#if OPENBLADE_CANDIDATE_RECEIPT_FIXTURE
using System.Security.Cryptography;
using System.Text.Json;
using OpenBlade.Deployment;

namespace OpenBlade.DeploymentBroker;

// App-local FDD closure only: the installed shared framework is not claimed as
// fixture code. Native assets and undeclared loader inputs are refused.
// Every declared local assembly and loader file stays read-only until commit.
internal sealed class ScratchReceiverClosure : IDisposable
{
    internal const string Stem = "OpenBlade.CandidateReceipt.Scratch";
    private const int MaximumFiles = 128;
    private const int MaximumFileBytes = 64 * 1024 * 1024;
    private const string ReadinessImage = "openblade-scratch-candidate-receipt.exe";
    private readonly WindowsCandidateReceiptStorage storage;
    private readonly string directory;
    private readonly WindowsCandidateReceiptStorage.DirectoryLease directoryLease;
    private readonly SortedDictionary<string, FileStream> files = new(StringComparer.Ordinal);
    private readonly byte[] digest;
    private bool disposed;
    private readonly List<IDisposable> namespaceSeals = [];
    private readonly Dictionary<string, WindowsCandidateReceiptStorage.DirectoryLease> subdirectories = new(StringComparer.Ordinal);

    private ScratchReceiverClosure(WindowsCandidateReceiptStorage storage)
    {
        this.storage = storage;
        directory = Path.GetDirectoryName(ScratchCandidateLaunch.ReceiverPath(storage))!;
        directoryLease = storage.PinDirectory(directory);
        try
        {
            foreach (string name in RequiredFiles(Read(Stem + ".deps.json")))
            {
                if (!files.ContainsKey(name))
                {
                    string path = Path.Combine(directory, name);
                    string parent = Path.GetDirectoryName(path)!;
                    if (parent != directory && !subdirectories.ContainsKey(parent))
                    {
                        subdirectories.Add(parent, storage.PinDirectory(parent));
                    }
                    files.Add(name, storage.OpenFile(path));
                }
            }
            // These are the only supported FDD loader options. In particular,
            // no dev probing paths, startup hooks or additional deps are admitted.
            using var config = JsonDocument.Parse(Read(Stem + ".runtimeconfig.json"));
            var options = config.RootElement.GetProperty("runtimeOptions");
            foreach (var property in options.EnumerateObject())
            {
                if (property.Name is not ("tfm" or "framework" or "frameworks" or "configProperties"))
                {
                    throw new InvalidDataException("The scratch receiver has an unsupported loader option.");
                }
            }
            if (options.TryGetProperty("configProperties", out var switches))
            {
                foreach (var property in switches.EnumerateObject())
                {
                    if (property.Name is not ("System.Reflection.Metadata.MetadataUpdater.IsSupported"
                        or "System.Runtime.Serialization.EnableUnsafeBinaryFormatterSerialization"
                        or "CSWINRT_USE_WINDOWS_UI_XAML_PROJECTIONS")
                        || property.Value.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                    {
                        throw new InvalidDataException("The scratch receiver has an unsupported runtime switch.");
                    }
                }
            }
            foreach (string variable in new[] { "DOTNET_ADDITIONAL_DEPS", "DOTNET_SHARED_STORE", "DOTNET_STARTUP_HOOKS" })
            {
                if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable(variable)))
                {
                    throw new InvalidDataException("The scratch receiver has an external loader override.");
                }
            }
            foreach (string path in files.Keys.Select(name => Path.GetDirectoryName(Path.Combine(directory, name))!)
                .SelectMany(ReceiverAncestors).Distinct(StringComparer.Ordinal).OrderDescending(StringComparer.Ordinal))
            {
                namespaceSeals.Add(storage.SealReceiverDirectory(path));
            }
            RequireMembers();
            digest = ComputeDigest();
        }
        catch (Exception failure)
        {
            try { Dispose(); }
            catch (Exception cleanupFailure) { throw new AggregateException(failure, cleanupFailure); }
            throw;
        }
    }

    internal static ScratchReceiverClosure Retain(WindowsCandidateReceiptStorage storage) => new(storage);

    // Used by the fixture to stage exactly the build's declared local closure,
    // rather than copying arbitrary test-runner DLLs beside the apphost.
    internal static string[] RequiredFiles(byte[] dependencies)
    {
        var names = new SortedSet<string>(StringComparer.Ordinal)
        {
            ScratchCandidateLaunch.ReceiverName, Stem + ".dll", Stem + ".deps.json", Stem + ".runtimeconfig.json",
        };
        using var deps = JsonDocument.Parse(dependencies);
        var targets = deps.RootElement.GetProperty("targets");
        if (targets.EnumerateObject().Count() != 1)
        {
            throw new InvalidDataException("The scratch receiver requires one framework-dependent target.");
        }
        foreach (var library in targets.EnumerateObject().Single().Value.EnumerateObject())
        {
            if (library.Value.TryGetProperty("native", out _))
            {
                throw new InvalidDataException("The scratch receiver requires managed runtime assets.");
            }
            if (library.Value.TryGetProperty("runtime", out var runtime))
            {
                foreach (var asset in runtime.EnumerateObject())
                {
                    Add(asset.Name.Split('/')[^1], allowEntryAssembly: true);
                }
            }
            if (library.Value.TryGetProperty("resources", out var resources))
            {
                foreach (var asset in resources.EnumerateObject())
                {
                    string[] parts = asset.Name.Split('/');
                    if (parts.Length != 2 || parts[0] != asset.Value.GetProperty("locale").GetString())
                    {
                        throw new InvalidDataException("The scratch receiver has an unsupported satellite asset.");
                    }
                    Add(asset.Name.Replace('/', Path.DirectorySeparatorChar));
                }
            }
            // Ordinary FDD build output retains the Windows managed RID assets
            // at their declared paths. Hash and hold these as well as fallbacks.
            if (library.Value.TryGetProperty("runtimeTargets", out var targetsForLibrary))
            {
                foreach (var asset in targetsForLibrary.EnumerateObject())
                {
                    if (asset.Value.GetProperty("rid").GetString() != "win"
                        || asset.Value.GetProperty("assetType").GetString() != "runtime"
                        || !asset.Name.StartsWith("runtimes/win/lib/", StringComparison.Ordinal))
                    {
                        throw new InvalidDataException("The scratch receiver has an unsupported RID asset.");
                    }
                    Add(asset.Name.Replace('/', Path.DirectorySeparatorChar));
                }
            }
        }
        return names.ToArray();

        void Add(string name, bool allowEntryAssembly = false)
        {
            string[] segments = name.Split(Path.DirectorySeparatorChar);
            if (!name.EndsWith(".dll", StringComparison.Ordinal) || name.Length > 192
                || segments.Any(part => part.Length == 0 || part is "." or ".."
                    || part.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not ('.' or '-' or '_')))
                || names.Any(existing => string.Equals(existing, name, StringComparison.OrdinalIgnoreCase)))
            {
                if (allowEntryAssembly && name == Stem + ".dll")
                {
                    return;
                }
                throw new InvalidDataException("The scratch receiver has an unexpected or duplicate runtime asset.");
            }
            names.Add(name);
            if (names.Count > MaximumFiles)
            {
                throw new InvalidDataException("The scratch receiver closure exceeds its file bound.");
            }
        }
    }

    private IEnumerable<string> ReceiverAncestors(string path)
    {
        for (string? current = path; current is not null && current.Length >= directory.Length;
            current = Path.GetDirectoryName(current))
        {
            yield return current;
        }
    }

    private byte[] Read(string name)
    {
        if (!files.TryGetValue(name, out var file))
        {
            file = storage.OpenFile(Path.Combine(directory, name));
            files.Add(name, file);
        }
        file.Position = 0;
        return WindowsCandidateReceiptStorage.ReadBounded(file, 256 * 1024);
    }

    private void RequireMembers()
    {
        // Readiness is a separate, independently pinned executable; it is never
        // a receiver load asset. All other unexpected files/directories refuse.
        var expectedDirectories = new SortedSet<string>(StringComparer.Ordinal);
        foreach (string name in files.Keys)
        {
            for (string? parent = Path.GetDirectoryName(name); !string.IsNullOrEmpty(parent); parent = Path.GetDirectoryName(parent))
            {
                expectedDirectories.Add(parent);
            }
        }
        string[] actual = expectedDirectories.Prepend("")
            .SelectMany(parent => Directory.EnumerateFileSystemEntries(Path.Combine(directory, parent)))
            .Select(path => Path.GetRelativePath(directory, path))
            .Where(name => name != ReadinessImage).Order(StringComparer.Ordinal).ToArray();
        if (!actual.SequenceEqual(files.Keys.Concat(expectedDirectories).Order(StringComparer.Ordinal)))
        {
            throw new InvalidDataException("The scratch receiver closure contains missing or unexpected members.");
        }
    }

    private byte[] ComputeDigest()
    {
        using var encoded = new MemoryStream();
        using var writer = new BinaryWriter(encoded);
        writer.Write("OpenBlade.ScratchReceiverClosure.v1");
        foreach (var (name, file) in files)
        {
            if (file.Length is < 1 or > MaximumFileBytes)
            {
                throw new InvalidDataException("The scratch receiver closure has an invalid member size.");
            }
            writer.Write(name);
            writer.Write(file.Length);
            file.Position = 0;
            writer.Write(SHA256.HashData(file));
        }
        writer.Flush();
        return SHA256.HashData(encoded.ToArray());
    }

    internal byte[] RequireUnchanged()
    {
        RequireMembers();
        if (!CryptographicOperations.FixedTimeEquals(digest, ComputeDigest()))
        {
            throw new InvalidDataException("The retained scratch receiver closure changed.");
        }
        return digest.ToArray();
    }

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }
        disposed = true;
        DisposeRetainedResources(namespaceSeals.AsEnumerable().Reverse()
            .Concat(files.Values.Reverse()).Concat(subdirectories.Values.Reverse()).Append(directoryLease));
    }

    // Explicit in-memory cleanup seam: no process, IPC or filesystem authority.
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
            throw new AggregateException("The scratch receiver closure could not release every retained resource.", failures);
        }
    }
}
#endif
