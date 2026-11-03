using System.Security;
using OpenBlade.Contracts;

namespace OpenBlade.Tray;

internal sealed class ProvisioningPackageInUseException(Exception acquisitionFailure, Exception stopFailure)
    : IOException("Package extraction exit could not be confirmed.", new AggregateException(acquisitionFailure, stopFailure))
{
    internal Exception AcquisitionFailure { get; } = acquisitionFailure;
}

internal sealed class ProvisioningPackageDirectory : IDisposable
{
    internal const int MaximumEntries = 4096;
    internal const int MaximumDepth = 32;

    private readonly string parent;
    private readonly string destination;
    private readonly Action<string> createProtectedDirectory;
    private readonly Action<string> validateProtectedDirectory;
    private readonly Func<string, FileAttributes> getAttributes;
    private bool owned;
    private bool retained;

    internal ProvisioningPackageDirectory(
        string parent,
        string destination,
        Action<string> createProtectedDirectory,
        Action<string> validateProtectedDirectory,
        Func<string, FileAttributes>? getAttributes = null)
    {
        this.parent = Path.GetFullPath(parent);
        this.destination = Path.GetFullPath(destination);
        this.createProtectedDirectory = createProtectedDirectory;
        this.validateProtectedDirectory = validateProtectedDirectory;
        this.getAttributes = getAttributes ?? File.GetAttributes;
    }

    internal void BeginAcquisition()
    {
        if (owned || retained)
        {
            throw new InvalidOperationException("The package directory cannot begin another acquisition.");
        }
        ValidateDestination();
        RejectReparseAncestors(parent);
        validateProtectedDirectory(parent);
        if (TryGetAttributes(destination, out _))
        {
            throw new IOException("Package acquisition requires a new attempt directory.");
        }
        // A failed creation or security check does not establish ownership. Leave
        // an uncertain directory intact rather than remove preexisting bytes.
        createProtectedDirectory(destination);
        RejectReparseAncestors(destination);
        validateProtectedDirectory(destination);
        owned = true;
    }

    internal void Retain() => retained = true;

    public void Dispose()
    {
        if (!owned || retained)
        {
            return;
        }
        owned = false;
        try
        {
            DeleteOwnedDirectory();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
            or InvalidDataException or SecurityException)
        {
            // Cleanup must not replace the acquisition failure or refusal result.
            DiagnosticTrace.TraceException(nameof(ProvisioningPackageDirectory), "Cleanup", exception);
        }
    }

    private void DeleteOwnedDirectory()
    {
        ValidateDestination();
        RejectReparseAncestors(parent);
        validateProtectedDirectory(parent);
        if (!TryGetAttributes(destination, out _))
        {
            return;
        }
        var pending = new Stack<(string Path, int Depth)>();
        var entries = new List<(string Path, bool Directory)>();
        pending.Push((destination, 0));
        while (pending.TryPop(out var directory))
        {
            RejectReparseAncestors(directory.Path);
            validateProtectedDirectory(directory.Path);
            foreach (string entry in Directory.EnumerateFileSystemEntries(directory.Path))
            {
                string path = Path.GetFullPath(entry);
                if (!path.StartsWith(destination + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
                    || !TryGetAttributes(path, out FileAttributes attributes)
                    || (attributes & FileAttributes.ReparsePoint) != 0)
                {
                    throw new InvalidDataException("The package cleanup inventory is no longer a regular owned tree.");
                }
                if (entries.Count == MaximumEntries)
                {
                    throw new InvalidDataException("Package cleanup exceeds its entry bound.");
                }
                bool isDirectory = (attributes & FileAttributes.Directory) != 0;
                entries.Add((path, isDirectory));
                if (isDirectory)
                {
                    if (directory.Depth == MaximumDepth)
                    {
                        throw new InvalidDataException("Package cleanup exceeds its depth bound.");
                    }
                    pending.Push((path, directory.Depth + 1));
                }
            }
        }

        // Validate the complete bounded tree before removing anything. Delete
        // individual entries so no recursive API can follow an unchecked link.
        for (int index = entries.Count - 1; index >= 0; index--)
        {
            var entry = entries[index];
            RejectReparseAncestors(entry.Path);
            if (entry.Directory)
            {
                validateProtectedDirectory(entry.Path);
                Directory.Delete(entry.Path, recursive: false);
            }
            else
            {
                FileAttributes attributes = getAttributes(entry.Path);
                if ((attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0)
                {
                    throw new InvalidDataException("The owned cleanup file is no longer a regular file.");
                }
                if ((attributes & FileAttributes.ReadOnly) != 0)
                {
                    // Cabinet extraction can preserve ReadOnly. Change only that
                    // bit on this freshly admitted file from this owned attempt.
                    File.SetAttributes(entry.Path, attributes & ~FileAttributes.ReadOnly);
                }
                File.Delete(entry.Path);
            }
        }
        RejectReparseAncestors(destination);
        validateProtectedDirectory(destination);
        Directory.Delete(destination, recursive: false);
    }

    private void ValidateDestination()
    {
        if (!string.Equals(Path.GetDirectoryName(destination), parent, StringComparison.OrdinalIgnoreCase)
            || !Guid.TryParseExact(Path.GetFileName(destination), "N", out _))
        {
            throw new InvalidDataException("Package cleanup requires one direct GUID attempt directory.");
        }
    }

    private void RejectReparseAncestors(string path)
    {
        for (string? current = path; current is not null; current = Path.GetDirectoryName(current))
        {
            if (TryGetAttributes(current, out FileAttributes attributes)
                && (attributes & FileAttributes.ReparsePoint) != 0)
            {
                throw new InvalidDataException("Package cleanup paths cannot contain links or reparse points.");
            }
        }
    }

    private bool TryGetAttributes(string path, out FileAttributes attributes)
    {
        try
        {
            attributes = getAttributes(path);
            return true;
        }
        catch (FileNotFoundException) { }
        catch (DirectoryNotFoundException) { }
        attributes = default;
        return false;
    }
}
