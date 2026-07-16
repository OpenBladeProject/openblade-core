using OpenBlade.Setup;

namespace OpenBlade.Tray;

internal sealed class WindowsStartupRegistration : IStartupRegistration
{
    private readonly IStartupShortcutWriter shortcutWriter;
    private readonly string shortcutPath;
    private readonly string legacyShortcutPath;
    private readonly string executablePath;
    private readonly string workingDirectory;

    internal WindowsStartupRegistration()
        : this(
            new WindowsStartupShortcutWriter(),
            Environment.GetFolderPath(Environment.SpecialFolder.Startup),
            Environment.ProcessPath
                ?? throw new InvalidOperationException("Tray executable path is unavailable."),
            AppContext.BaseDirectory)
    {
    }

    internal WindowsStartupRegistration(
        IStartupShortcutWriter shortcutWriter,
        string startupDirectory,
        string executablePath,
        string workingDirectory)
    {
        this.shortcutWriter = shortcutWriter
            ?? throw new ArgumentNullException(nameof(shortcutWriter));
        ArgumentException.ThrowIfNullOrWhiteSpace(startupDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(executablePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(workingDirectory);
        shortcutPath = Path.Combine(startupDirectory, "OpenBlade.lnk");
        legacyShortcutPath = Path.Combine(startupDirectory, "OpenBlade.cmd");
        this.executablePath = executablePath;
        this.workingDirectory = workingDirectory;
    }

    public Task<bool> IsEnabledAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(
            File.Exists(shortcutPath)
            || File.Exists(legacyShortcutPath));
    }

    public async Task SetEnabledAsync(bool enabled, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (enabled)
        {
            await shortcutWriter.CreateAsync(
                new ShortcutRequest(shortcutPath, executablePath, workingDirectory),
                cancellationToken).ConfigureAwait(false);
            File.Delete(legacyShortcutPath);
            return;
        }

        File.Delete(shortcutPath);
        File.Delete(legacyShortcutPath);
    }
}
