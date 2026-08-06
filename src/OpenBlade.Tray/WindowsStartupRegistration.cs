using OpenBlade.Setup;

namespace OpenBlade.Tray;

internal sealed class WindowsStartupRegistration : IStartupRegistration
{
    private const string DisabledMarkerFileName = "tray-startup-disabled.flag";
    private readonly IStartupShortcutWriter shortcutWriter;
    private readonly string shortcutPath;
    private readonly string commonShortcutPath;
    private readonly string legacyShortcutPath;
    private readonly string disabledMarkerPath;
    private readonly string executablePath;
    private readonly string workingDirectory;

    internal WindowsStartupRegistration()
        : this(
            new WindowsStartupShortcutWriter(),
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
        string startupDirectory,
        string commonStartupDirectory,
        string userDataDirectory,
        string executablePath,
        string workingDirectory)
    {
        this.shortcutWriter = shortcutWriter
            ?? throw new ArgumentNullException(nameof(shortcutWriter));
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

    internal bool ShouldStartForLaunchArguments(string? arguments) =>
        !string.Equals(
            arguments?.Trim(),
            StartupShortcutArguments.Tray,
            StringComparison.OrdinalIgnoreCase)
        || !File.Exists(disabledMarkerPath);

    public Task<bool> IsEnabledAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(
            !File.Exists(disabledMarkerPath)
            && HasStartupShortcut());
    }

    public async Task SetEnabledAsync(bool enabled, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (enabled)
        {
            if (!HasStartupShortcut())
            {
                await shortcutWriter.CreateAsync(
                    new ShortcutRequest(
                        shortcutPath,
                        executablePath,
                        workingDirectory,
                        Arguments: StartupShortcutArguments.Tray),
                    cancellationToken).ConfigureAwait(false);
            }

            File.Delete(disabledMarkerPath);
            return;
        }

        await WriteDisabledMarkerAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            File.Delete(shortcutPath);
            File.Delete(legacyShortcutPath);
        }
        catch (Exception deletionException)
        {
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

    private bool HasStartupShortcut() =>
        File.Exists(commonShortcutPath)
        || File.Exists(shortcutPath)
        || File.Exists(legacyShortcutPath);

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
            File.Delete(temporaryPath);
        }
    }
}
