#if OPENBLADE_CANDIDATE_RECEIPT_FIXTURE
namespace OpenBlade.DeploymentBroker;

// One fresh private directory per launch, outside the retained code closure.
// The supplied pin is the storage owner's checked, non-reparse directory lease.
internal sealed class ScratchLaunchData
{
    private const string Prefix = "launch-data-";
    private readonly string root;
    private readonly Func<string, IDisposable> pinDirectory;
    private readonly IDisposable lease;
    private bool completed;
    internal string Path { get; }

    private ScratchLaunchData(string root, string path, Func<string, IDisposable> pinDirectory, IDisposable lease)
    { this.root = root; Path = path; this.pinDirectory = pinDirectory; this.lease = lease; }

    internal static ScratchLaunchData Create(string root, string code, Func<string, IDisposable> pinDirectory)
    {
        root = System.IO.Path.TrimEndingDirectorySeparator(System.IO.Path.GetFullPath(root));
        string path = System.IO.Path.Combine(root, Prefix + Guid.NewGuid().ToString("N"));
        _ = ScratchNativeReceiverLaunch.CreateEnvironment(code, path);
        RequireDeletionTarget(root, path);
        using var parent = pinDirectory(root);
        if (Directory.Exists(path) || File.Exists(path)) { throw new IOException("Fresh launch data is already present."); }
        Directory.CreateDirectory(path);
        return new(root, path, pinDirectory, pinDirectory(path));
    }

    internal static void RequireDeletionTarget(string root, string path)
    {
        if (!System.IO.Path.IsPathFullyQualified(root) || !System.IO.Path.IsPathFullyQualified(path)
            || root != System.IO.Path.TrimEndingDirectorySeparator(System.IO.Path.GetFullPath(root))
            || path != System.IO.Path.GetFullPath(path) || System.IO.Path.GetDirectoryName(path) != root
            || !System.IO.Path.GetFileName(path).StartsWith(Prefix, StringComparison.Ordinal)
            || !Guid.TryParseExact(System.IO.Path.GetFileName(path)[Prefix.Length..], "N", out var id) || id == Guid.Empty)
        { throw new IOException("Launch data deletion target is outside its exact private root."); }
    }

    internal void Finish(bool retirementConfirmed)
    {
        if (completed) { throw new InvalidOperationException("Launch data retirement was already attempted."); }
        completed = true;
        try
        {
            if (!retirementConfirmed)
            { throw new IOException("Scratch retirement is unresolved; launch data retained at " + Path); }
            RequireDeletionTarget(root, Path);
            // Keep the original data namespace pinned throughout content cleanup.
            // Release it only for the final, nonrecursive empty-directory removal.
            using var parent = pinDirectory(root);
            DeleteContents(Path);
        }
        finally { lease.Dispose(); }
        using var finalParent = pinDirectory(root);
        Directory.Delete(Path, recursive: false);
    }

    private void DeleteContents(string directory)
    {
        // Never use recursive deletion: each directory is checked and retained
        // while its entries are visited, and each final deletion is nonrecursive.
        if (directory != Path && !directory.StartsWith(Path + System.IO.Path.DirectorySeparatorChar, StringComparison.Ordinal))
        { throw new IOException("Launch data child escaped its private directory."); }
        using var pin = pinDirectory(directory);
        foreach (string child in Directory.EnumerateFileSystemEntries(directory))
        {
            FileAttributes attributes = File.GetAttributes(child);
            if ((attributes & FileAttributes.ReparsePoint) != 0)
            { throw new IOException("Reparse entry in launch data; cleanup refused at " + child); }
            if ((attributes & FileAttributes.Directory) != 0)
            {
                DeleteContents(child);
                Directory.Delete(child, recursive: false);
            }
            else { File.Delete(child); }
        }
    }
}
#endif
