using OpenBlade.Contracts;
using OpenBlade.Setup;

namespace OpenBlade.Tray;

internal sealed class WindowsStartupRegistration : IStartupRegistration
{
    private const string DisabledMarkerFileName = "tray-startup-disabled.flag";
    private readonly IStartupShortcutWriter shortcutWriter;
    private readonly ISignInTaskScheduler taskScheduler;
    private readonly string shortcutPath;
    private readonly string commonShortcutPath;
    private readonly string legacyShortcutPath;
    private readonly string disabledMarkerPath;
    private readonly string executablePath;
    private readonly string workingDirectory;

    internal WindowsStartupRegistration()
        : this(
            new WindowsStartupShortcutWriter(),
            new WindowsSignInTaskScheduler(),
            Environment.GetFolderPath(Environment.SpecialFolder.Startup),
            Environment.GetFolderPath(Environment.SpecialFolder.CommonStartup),
            Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "OpenBlade"),
            Environment.ProcessPath
                ?? throw new InvalidOperationException("Tray executable path is unavailable."),
            AppContext.BaseDirectory)
    {
    }

    internal WindowsStartupRegistration(
        IStartupShortcutWriter shortcutWriter,
        ISignInTaskScheduler taskScheduler,
        string startupDirectory,
        string commonStartupDirectory,
        string userDataDirectory,
        string executablePath,
        string workingDirectory)
    {
        this.shortcutWriter = shortcutWriter
            ?? throw new ArgumentNullException(nameof(shortcutWriter));
        this.taskScheduler = taskScheduler
            ?? throw new ArgumentNullException(nameof(taskScheduler));
        ArgumentException.ThrowIfNullOrWhiteSpace(startupDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(commonStartupDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(userDataDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(executablePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(workingDirectory);
        shortcutPath = Path.Combine(startupDirectory, "OpenBlade.lnk");
        commonShortcutPath = Path.Combine(commonStartupDirectory, "OpenBlade.lnk");
        legacyShortcutPath = Path.Combine(startupDirectory, "OpenBlade.cmd");
        disabledMarkerPath = Path.Combine(userDataDirectory, DisabledMarkerFileName);
        this.executablePath = executablePath;
        this.workingDirectory = workingDirectory;
    }

    internal bool ShouldStartForLaunchArguments(IReadOnlyList<string> arguments)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        bool startupLaunch = arguments
            .Skip(1)
            .Contains(
                StartupShortcutArguments.Tray,
                StringComparer.OrdinalIgnoreCase);
        return !startupLaunch || !File.Exists(disabledMarkerPath);
    }

    public Task<bool> IsEnabledAsync(CancellationToken cancellationToken) =>
        Task.Run(
            () =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                return !File.Exists(disabledMarkerPath)
                    && HasStartupLauncher();
            },
            cancellationToken);

    public async Task SetEnabledAsync(bool enabled, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (enabled)
        {
            if (!HasStartupLauncher())
            {
                await shortcutWriter.CreateAsync(
                    new ShortcutRequest(
                        shortcutPath,
                        executablePath,
                        workingDirectory,
                        Arguments: StartupShortcutArguments.Tray),
                    cancellationToken).ConfigureAwait(false);
            }

            if (File.Exists(disabledMarkerPath))
            {
                File.Delete(disabledMarkerPath);
            }
            return;
        }

        bool wasDisabled = File.Exists(disabledMarkerPath);
        await WriteDisabledMarkerAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            File.Delete(shortcutPath);
            File.Delete(legacyShortcutPath);
        }
        catch (Exception deletionException)
        {
            if (wasDisabled)
            {
                throw;
            }

            try
            {
                File.Delete(disabledMarkerPath);
            }
            catch (Exception rollbackException)
            {
                throw new AggregateException(
                    "Could not disable tray startup or restore its prior state.",
                    deletionException,
                    rollbackException);
            }

            throw;
        }
    }

    private bool HasStartupLauncher() =>
        TrayTaskEnabled()
        || File.Exists(commonShortcutPath)
        || File.Exists(shortcutPath)
        || File.Exists(legacyShortcutPath);

    // The all-users sign-in task is registered by setup. Any failure to query it
    // (not elevated, scheduler unavailable) is treated as "no task".
    private bool TrayTaskEnabled()
    {
        try
        {
            return taskScheduler.GetState(SignInTaskNames.Tray) == SignInTaskState.Enabled;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            DiagnosticTrace.TraceException(nameof(WindowsStartupRegistration), "ReadTrayTaskState", exception);
            return false;
        }
    }

    private async Task WriteDisabledMarkerAsync(CancellationToken cancellationToken)
    {
        string directory = Path.GetDirectoryName(disabledMarkerPath)!;
        Directory.CreateDirectory(directory);
        string temporaryPath = disabledMarkerPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await File.WriteAllTextAsync(
                temporaryPath,
                "disabled",
                cancellationToken).ConfigureAwait(false);
            File.Move(temporaryPath, disabledMarkerPath, overwrite: true);
        }
        finally
        {
            try
            {
                File.Delete(temporaryPath);
            }
            catch (Exception exception)
            {
                // Best-effort cleanup must not replace the primary write failure.
                DiagnosticTrace.TraceException(nameof(WindowsStartupRegistration), "DeleteTemporaryDisabledMarker", exception);
            }
        }
    }
}
