using System.ComponentModel;

namespace OpenBlade.Service;

// Opt-in only. The provider pins the enrolled root and acquires its existing
// native-compatible exclusive lock without creating or modifying the root.
internal sealed class MachineConfigurationWriterBinding
{
    private readonly string root;
    private readonly Func<string, CancellationToken, IDisposable> acquireVerifiedWriterLease;

    internal MachineConfigurationWriterBinding(
        string machineRoot,
        Func<string, CancellationToken, IDisposable> acquireVerifiedWriterLease)
    {
        root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(machineRoot));
        this.acquireVerifiedWriterLease = acquireVerifiedWriterLease
            ?? throw new ArgumentNullException(nameof(acquireVerifiedWriterLease));
    }

    internal string ProfilePath => Path.Combine(root, "profiles.json");
    internal string PreferencesPath => Path.Combine(root, "battery-preferences.json");
    internal string MarkerPath => Path.Combine(root, "battery-configuration.commit.json");
    internal string OverridePath => Path.Combine(root, "manual-overrides.json");
    internal string RunMarkerPath => Path.Combine(root, "service-running.json");

    internal bool Matches(MachineConfigurationWriterBinding other) =>
        Matches(other.root, other.acquireVerifiedWriterLease);

    internal bool Matches(string expectedRoot, Func<string, CancellationToken, IDisposable> acquire) =>
        string.Equals(root, expectedRoot, StringComparison.Ordinal) && acquireVerifiedWriterLease == acquire;

    internal UserConfigurationWriterScope Acquire(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            IDisposable lease = acquireVerifiedWriterLease(root, cancellationToken)
                ?? throw new InvalidOperationException("The verified machine configuration writer lease was not supplied.");

            // Order shared with bootstrap: native exclusion, scope gate, then
            // the store gate. Dispose in reverse order before returning to IPC.
            return new UserConfigurationWriterScope(lease, MarkerPath);
        }
        catch (Win32Exception exception)
        {
            throw new IOException("Could not acquire the verified machine configuration writer lease.", exception);
        }
    }

    internal static void CheckTarget(string path)
    {
        try
        {
            const FileAttributes unsupported = FileAttributes.Directory | FileAttributes.ReparsePoint;
            if ((File.GetAttributes(path) & unsupported) != 0)
            {
                throw new IOException($"The machine configuration target is not a plain file: '{path}'.");
            }
        }
        catch (FileNotFoundException)
        {
            // An admitted write may create a missing leaf in the pinned root.
        }
    }
}
