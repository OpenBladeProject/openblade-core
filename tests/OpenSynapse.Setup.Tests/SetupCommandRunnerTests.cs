using OpenSynapse.Setup;

namespace OpenSynapse.Setup.Tests;

public sealed class SetupCommandRunnerTests
{
    [Fact]
    public async Task Install_CopiesPayloadRegistersServiceAndCreatesStartupShortcut()
    {
        RecordingSetupPlatform platform = new();
        SetupCommandRunner runner = new(platform);

        int exitCode = await runner.RunAsync(["install"], CancellationToken.None);

        Assert.Equal(0, exitCode);
        Assert.Equal(["service-prepare", "copy", "sid:S-1-5-21-test", "service-install", "service-health", "shortcut-install"], platform.Operations);
    }

    [Fact]
    public async Task Uninstall_PreservesProfilesUnlessPurgeIsExplicit()
    {
        RecordingSetupPlatform platform = new();
        SetupCommandRunner runner = new(platform);

        int exitCode = await runner.RunAsync(["uninstall"], CancellationToken.None);

        Assert.Equal(0, exitCode);
        Assert.Equal(["shortcut-remove", "service-remove", "tray-stop", "binaries-remove"], platform.Operations);
        Assert.DoesNotContain("data-remove", platform.Operations);
    }

    [Fact]
    public async Task UninstallPurge_RemovesMachineAndUserData()
    {
        RecordingSetupPlatform platform = new();
        SetupCommandRunner runner = new(platform);

        int exitCode = await runner.RunAsync(["uninstall", "--purge"], CancellationToken.None);

        Assert.Equal(0, exitCode);
        Assert.Equal(["shortcut-remove", "service-remove", "tray-stop", "binaries-remove", "data-remove"], platform.Operations);
    }

    [Fact]
    public async Task InvalidCommand_PrintsShippedExecutableSyntax()
    {
        RecordingSetupPlatform platform = new();
        SetupCommandRunner runner = new(platform);
        StringWriter errors = new();
        TextWriter original = Console.Error;
        Console.SetError(errors);
        try
        {
            int exitCode = await runner.RunAsync([], CancellationToken.None);

            Assert.Equal(2, exitCode);
            Assert.Contains("OpenSynapse.Setup.exe install", errors.ToString(), StringComparison.Ordinal);
            Assert.Contains("OpenSynapse.Setup.exe uninstall [--purge]", errors.ToString(), StringComparison.Ordinal);
        }
        finally
        {
            Console.SetError(original);
        }
    }

    private sealed class RecordingSetupPlatform : ISetupPlatform
    {
        public List<string> Operations { get; } = [];

        public string CurrentUserSid => "S-1-5-21-test";

        public Task PrepareServiceForInstallAsync(CancellationToken cancellationToken) => AddAsync("service-prepare");
        public Task CopyPayloadAsync(CancellationToken cancellationToken) => AddAsync("copy");
        public Task WriteAuthorizedUserSidAsync(string sid, CancellationToken cancellationToken) => AddAsync($"sid:{sid}");
        public Task InstallServiceAsync(CancellationToken cancellationToken) => AddAsync("service-install");
        public Task VerifyServiceHealthAsync(CancellationToken cancellationToken) => AddAsync("service-health");
        public Task InstallStartupShortcutAsync(CancellationToken cancellationToken) => AddAsync("shortcut-install");
        public Task RemoveStartupShortcutAsync(CancellationToken cancellationToken) => AddAsync("shortcut-remove");
        public Task RemoveServiceAsync(CancellationToken cancellationToken) => AddAsync("service-remove");
        public Task StopTrayAsync(CancellationToken cancellationToken) => AddAsync("tray-stop");
        public Task RemoveBinariesAsync(CancellationToken cancellationToken) => AddAsync("binaries-remove");
        public Task RemoveDataAsync(CancellationToken cancellationToken) => AddAsync("data-remove");

        private Task AddAsync(string operation)
        {
            Operations.Add(operation);
            return Task.CompletedTask;
        }
    }
}
