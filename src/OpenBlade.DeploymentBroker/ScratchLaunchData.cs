#if OPENBLADE_CANDIDATE_RECEIPT_FIXTURE
using System.Diagnostics;

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
        IDisposable? parent = null, dataLease = null;
        bool retained = false;
        try
        {
            ScratchFixtureFailure.Run(() =>
            {
                parent = pinDirectory(root);
                if (Directory.Exists(path) || File.Exists(path)) { throw new IOException("Fresh launch data is already present."); }
                Directory.CreateDirectory(path);
                retained = true;
                try { dataLease = pinDirectory(path); }
                catch (Exception failure)
                {
                    // No child exists yet. Re-admit the exact directory once;
                    // never traverse unknown contents after failed acquisition.
                    ScratchFixtureFailure.Cleanup(failure, () =>
                    {
                        IDisposable? cleanupPin = null;
                        ScratchFixtureFailure.Run(() =>
                        {
                            RequireDeletionTarget(root, path);
                            cleanupPin = pinDirectory(path);
                            if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
                            { throw new IOException("Reparse launch data; partial cleanup refused."); }
                        }, () => cleanupPin?.Dispose());
                        Directory.Delete(path, recursive: false);
                        if (Directory.Exists(path) || File.Exists(path))
                        { throw new IOException("Partial launch data removal was not confirmed."); }
                        retained = false;
                    });
                    throw;
                }
            }, () => parent?.Dispose());
            return new(root, path, pinDirectory, dataLease!);
        }
        catch (Exception failure)
        {
            // A failed parent release also prevents ownership transfer to the
            // caller. Release any acquired data pin once and retain its path.
            try { ScratchFixtureFailure.Cleanup(failure, () => dataLease?.Dispose()); }
            catch (Exception error) when (retained)
            { throw new ScratchFixtureFailure.RetainedDataException("Launch data acquisition failed; data retained at " + path, error); }
            throw;
        }
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

    internal void Finish(bool retirementConfirmed, CleanupBudget? budget = null)
    {
        if (completed) { throw new InvalidOperationException("Launch data retirement was already attempted."); }
        completed = true;
        try
        {
            IDisposable? parent = null;
            Exception? failure = null;
            try
            {
                if (!retirementConfirmed)
                { throw new IOException("Scratch retirement is unresolved; launch data retained at " + Path); }
                RequireDeletionTarget(root, Path);
                // Keep the original namespace pinned throughout content cleanup.
                // Release it only for final nonrecursive empty-directory removal.
                parent = pinDirectory(root);
                budget ??= new CleanupBudget();
                DeleteContents(Path, budget, 0);
                budget.Check();
            }
            catch (Exception error) { failure = error; }
            // Preserve the operation and every release failure before classifying.
            ScratchFixtureFailure.Cleanup(failure, () => parent?.Dispose(), lease.Dispose);

            IDisposable? finalParent = null;
            failure = null;
            try
            {
                finalParent = pinDirectory(root);
                budget!.Check();
                Directory.Delete(Path, recursive: false);
            }
            catch (Exception error) { failure = error; }
            ScratchFixtureFailure.Cleanup(failure, () => finalParent?.Dispose());
        }
        catch (Exception failure)
        { throw new ScratchFixtureFailure.RetainedDataException("Launch data cleanup incomplete; data retained at " + Path, failure); }
    }

    private void DeleteContents(string directory, CleanupBudget budget, int depth)
    {
        // Never use recursive deletion: each directory is checked and retained
        // while its entries are visited, and each final deletion is nonrecursive.
        if (directory != Path && !directory.StartsWith(Path + System.IO.Path.DirectorySeparatorChar, StringComparison.Ordinal))
        { throw new IOException("Launch data child escaped its private directory."); }
        budget.Enter(depth);
        IDisposable? pin = null;
        IEnumerator<string>? entries = null;
        ScratchFixtureFailure.Run(() =>
        {
            pin = pinDirectory(directory);
            entries = Directory.EnumerateFileSystemEntries(directory).GetEnumerator();
            while (true)
            {
                budget.Check(); // Best effort between calls, not preemption of blocked I/O.
                if (!entries.MoveNext()) { break; }
                budget.TakeEntry();
                string child = entries.Current;
                FileAttributes attributes = File.GetAttributes(child);
                if ((attributes & FileAttributes.ReparsePoint) != 0)
                { throw new IOException("Reparse entry in launch data; cleanup refused at " + child); }
                if ((attributes & FileAttributes.Directory) != 0)
                {
                    DeleteContents(child, budget, depth + 1);
                    budget.Check();
                    Directory.Delete(child, recursive: false);
                }
                else
                {
                    budget.Check();
                    File.Delete(child);
                }
            }
        }, () => entries?.Dispose(), () => pin?.Dispose());
    }

    // One budget for the entire tree, including all siblings. The storage pin's
    // path admission already bounds recursion; this tighter cap also bounds held
    // leases. Time/cancellation checks cannot interrupt a blocked filesystem call.
    internal sealed class CleanupBudget
    {
        private readonly int maxEntries;
        private readonly int maxDepth;
        private readonly TimeSpan timeout;
        private readonly CancellationToken cancellationToken;
        private readonly Func<TimeSpan> elapsed;
        internal int Entries { get; private set; }

        internal CleanupBudget(int maxEntries = 1024, int maxDepth = 16, TimeSpan? timeout = null,
            CancellationToken cancellationToken = default, Func<TimeSpan>? elapsed = null)
        {
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxEntries);
            ArgumentOutOfRangeException.ThrowIfNegative(maxDepth);
            this.maxEntries = maxEntries;
            this.maxDepth = maxDepth;
            this.timeout = timeout ?? TimeSpan.FromSeconds(2);
            ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(this.timeout, TimeSpan.Zero);
            this.cancellationToken = cancellationToken;
            var watch = Stopwatch.StartNew();
            this.elapsed = elapsed ?? (() => watch.Elapsed);
        }

        internal void Check()
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (elapsed() >= timeout) { throw new IOException("Launch data cleanup deadline exhausted."); }
        }

        internal void Enter(int depth)
        {
            Check();
            if (depth > maxDepth) { throw new IOException("Launch data cleanup depth budget exhausted."); }
        }

        internal void TakeEntry()
        {
            Check();
            if (Entries >= maxEntries) { throw new IOException("Launch data cleanup entry budget exhausted."); }
            Entries++;
        }
    }
}
#endif
