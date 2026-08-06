using OpenBlade.Setup;
using OpenBlade.Tray;

namespace OpenBlade.Tray.Tests;

public sealed class WindowsStartupRegistrationTests : IDisposable
{
    private readonly string root = Path.Combine(
        Path.GetTempPath(),
        "OpenBlade.WindowsStartupRegistrationTests",
        Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task AllUsersShortcutIsReportedAsEnabledForCurrentUser()
    {
        TestPaths paths = CreatePaths();
        WriteFile(paths.CommonShortcut);
        var writer = new RecordingShortcutWriter();
        WindowsStartupRegistration registration = CreateRegistration(paths, writer);

        Assert.True(await registration.IsEnabledAsync(CancellationToken.None));
        Assert.True(registration.ShouldStartForLaunchArguments(StartupShortcutArguments.Tray));
        Assert.Empty(writer.Requests);
    }

    [Fact]
    public async Task PerUserOptOutDisablesOnlyStartupLaunches()
    {
        TestPaths paths = CreatePaths();
        WriteFile(paths.CommonShortcut);
        WriteFile(paths.DisabledMarker);
        WindowsStartupRegistration registration = CreateRegistration(
            paths,
            new RecordingShortcutWriter());

        Assert.False(await registration.IsEnabledAsync(CancellationToken.None));
        Assert.False(registration.ShouldStartForLaunchArguments("  --STARTUP  "));
        Assert.True(registration.ShouldStartForLaunchArguments(string.Empty));
        Assert.True(registration.ShouldStartForLaunchArguments("--notification"));
    }

    [Fact]
    public async Task DisableCreatesOptOutAndRemovesOnlyPerUserLaunchers()
    {
        TestPaths paths = CreatePaths();
        WriteFile(paths.CommonShortcut);
        WriteFile(paths.UserShortcut);
        WriteFile(paths.LegacyShortcut);
        WindowsStartupRegistration registration = CreateRegistration(
            paths,
            new RecordingShortcutWriter());

        await registration.SetEnabledAsync(false, CancellationToken.None);

        Assert.True(File.Exists(paths.CommonShortcut));
        Assert.True(File.Exists(paths.DisabledMarker));
        Assert.False(File.Exists(paths.UserShortcut));
        Assert.False(File.Exists(paths.LegacyShortcut));
        Assert.False(await registration.IsEnabledAsync(CancellationToken.None));
    }

    [Fact]
    public async Task EnableClearsOptOutWhenAllUsersLauncherExists()
    {
        TestPaths paths = CreatePaths();
        WriteFile(paths.CommonShortcut);
        WriteFile(paths.DisabledMarker);
        var writer = new RecordingShortcutWriter();
        WindowsStartupRegistration registration = CreateRegistration(paths, writer);

        await registration.SetEnabledAsync(true, CancellationToken.None);

        Assert.False(File.Exists(paths.DisabledMarker));
        Assert.True(await registration.IsEnabledAsync(CancellationToken.None));
        Assert.Empty(writer.Requests);
    }

    [Fact]
    public async Task EnableCreatesPerUserFallbackBeforeClearingOptOut()
    {
        TestPaths paths = CreatePaths();
        WriteFile(paths.DisabledMarker);
        var writer = new RecordingShortcutWriter(createShortcut: true);
        WindowsStartupRegistration registration = CreateRegistration(paths, writer);

        await registration.SetEnabledAsync(true, CancellationToken.None);

        ShortcutRequest request = Assert.Single(writer.Requests);
        Assert.Equal(paths.UserShortcut, request.ShortcutPath);
        Assert.Equal(StartupShortcutArguments.Tray, request.Arguments);
        Assert.True(File.Exists(paths.UserShortcut));
        Assert.False(File.Exists(paths.DisabledMarker));
        Assert.True(await registration.IsEnabledAsync(CancellationToken.None));
    }

    [Fact]
    public async Task EnableFallbackFailureKeepsOptOut()
    {
        TestPaths paths = CreatePaths();
        WriteFile(paths.DisabledMarker);
        var writer = new RecordingShortcutWriter
        {
            Failure = new IOException("shortcut create failed"),
        };
        WindowsStartupRegistration registration = CreateRegistration(paths, writer);

        await Assert.ThrowsAsync<IOException>(() =>
            registration.SetEnabledAsync(true, CancellationToken.None));

        Assert.True(File.Exists(paths.DisabledMarker));
        Assert.False(await registration.IsEnabledAsync(CancellationToken.None));
    }

    [Fact]
    public async Task DisableCleanupFailureRestoresEnabledState()
    {
        TestPaths paths = CreatePaths();
        WriteFile(paths.CommonShortcut);
        Directory.CreateDirectory(paths.UserShortcut);
        WindowsStartupRegistration registration = CreateRegistration(
            paths,
            new RecordingShortcutWriter());

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            registration.SetEnabledAsync(false, CancellationToken.None));

        Assert.False(File.Exists(paths.DisabledMarker));
        Assert.True(await registration.IsEnabledAsync(CancellationToken.None));
    }

    public void Dispose()
    {
        if (Directory.Exists(root))
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private TestPaths CreatePaths()
    {
        string startup = Path.Combine(root, "user-startup");
        string commonStartup = Path.Combine(root, "common-startup");
        string userData = Path.Combine(root, "user-data");
        return new(
            startup,
            commonStartup,
            userData,
            Path.Combine(startup, "OpenBlade.lnk"),
            Path.Combine(startup, "OpenBlade.cmd"),
            Path.Combine(commonStartup, "OpenBlade.lnk"),
            Path.Combine(userData, "tray-startup-disabled.flag"));
    }

    private static WindowsStartupRegistration CreateRegistration(
        TestPaths paths,
        IStartupShortcutWriter writer) => new(
            writer,
            paths.StartupDirectory,
            paths.CommonStartupDirectory,
            paths.UserDataDirectory,
            Path.Combine(paths.UserDataDirectory, "OpenBlade.Tray.exe"),
            paths.UserDataDirectory);

    private static void WriteFile(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, string.Empty);
    }

    private sealed class RecordingShortcutWriter(bool createShortcut = false)
        : IStartupShortcutWriter
    {
        public List<ShortcutRequest> Requests { get; } = [];
        public Exception? Failure { get; init; }

        public Task CreateAsync(
            ShortcutRequest request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Requests.Add(request);
            if (Failure is not null)
            {
                return Task.FromException(Failure);
            }

            if (createShortcut)
            {
                WriteFile(request.ShortcutPath);
            }

            return Task.CompletedTask;
        }
    }

    private sealed record TestPaths(
        string StartupDirectory,
        string CommonStartupDirectory,
        string UserDataDirectory,
        string UserShortcut,
        string LegacyShortcut,
        string CommonShortcut,
        string DisabledMarker);
}
