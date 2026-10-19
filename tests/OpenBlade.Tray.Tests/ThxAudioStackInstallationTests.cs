using System.ComponentModel;
using OpenBlade.Session;

namespace OpenBlade.Tray.Tests;

public sealed class ThxAudioStackInstallationTests
{
    [Fact]
    public async Task SetupRequiresExplicitActionAndDoesNotReadOrApplyAudio()
    {
        int downloads = 0, installs = 0;
        var adapter = new MissingStackAdapter();
        await using var viewModel = new ThxAudioViewModel(adapter,
            _ => { downloads++; return Task.FromResult("verified"); }, () => false,
            _ => { installs++; return Task.FromResult(ThxAudioProvisioningResult.Installed); }, () => true);
        await viewModel.InstallStackAsync();
        await viewModel.SetEnabledAsync(true);
        Assert.Equal(0, installs);
        Assert.Equal(0, downloads);
        Assert.True(viewModel.CanInstallStack);
        await viewModel.InstallStackAsync();
        Assert.Equal(1, downloads);
        Assert.Equal(1, installs);
        Assert.Equal(1, adapter.ConnectCalls);
        Assert.Equal(0, adapter.ReadCalls);
        Assert.False(viewModel.CanEdit);
        Assert.False(viewModel.IsStackInstallationVisible);
        Assert.True(viewModel.IsReadSettingsVisible);
        Assert.Contains("Read the current settings", viewModel.StatusMessage);
    }

    [Fact]
    public async Task MissingClientAloneIsFixedWithoutElevation()
    {
        int installs = 0;
        var adapter = new MissingStackAdapter();
        await using var viewModel = new ThxAudioViewModel(adapter,
            _ => { adapter.Available = true; return Task.FromResult("verified"); }, () => false,
            _ => { installs++; return Task.FromResult(ThxAudioProvisioningResult.Installed); }, () => true);
        await viewModel.SetEnabledAsync(true);
        Assert.True(viewModel.IsStackInstallationVisible);
        Assert.False(viewModel.IsComponentDownloadVisible);
        await viewModel.InstallStackAsync();
        Assert.Equal(0, installs);
        Assert.Equal(0, adapter.ReadCalls);
        Assert.False(viewModel.IsStackInstallationVisible);
        Assert.True(viewModel.IsReadSettingsVisible);
    }

    [Theory]
    [InlineData(ThxAudioProvisioningResult.RecoveryRequired)]
    [InlineData(ThxAudioProvisioningResult.RestartRequired)]
    [InlineData(ThxAudioProvisioningResult.ExistingInstallation)]
    public async Task SetupThatCannotSafelyRepeatStaysPausedAfterExplicitRead(ThxAudioProvisioningResult result)
    {
        int installs = 0;
        await using var viewModel = new ThxAudioViewModel(new MissingStackAdapter(),
            hasVerifiedComponent: () => true,
            installStack: _ => { installs++; return Task.FromResult(result); }, canInstallStack: () => true);
        await viewModel.SetEnabledAsync(true);
        await viewModel.InstallStackAsync();
        await viewModel.RefreshAsync();
        await viewModel.InstallStackAsync();
        Assert.False(viewModel.CanInstallStack);
        Assert.Equal(1, installs);
    }

    [Fact]
    public async Task ClientVerificationFailureNeverRequestsElevation()
    {
        int installs = 0;
        await using var viewModel = new ThxAudioViewModel(new MissingStackAdapter(),
            _ => throw new InvalidDataException("Signature rejected"), () => false,
            _ => { installs++; return Task.FromResult(ThxAudioProvisioningResult.Installed); }, () => true);
        await viewModel.SetEnabledAsync(true);
        await viewModel.InstallStackAsync();
        Assert.Equal(0, installs);
        Assert.True(viewModel.CanInstallStack);
        Assert.Contains("before changes were submitted", viewModel.StatusMessage);
    }

    [Fact]
    public async Task CancellationAfterHelperRequestPausesRepeatedInstallation()
    {
        await using var viewModel = new ThxAudioViewModel(new MissingStackAdapter(),
            hasVerifiedComponent: () => true, installStack: _ => throw new OperationCanceledException(),
            canInstallStack: () => true);
        await viewModel.SetEnabledAsync(true);
        await viewModel.InstallStackAsync();
        Assert.False(viewModel.CanInstallStack);
        Assert.Contains("Do not run setup again", viewModel.StatusMessage);
    }

    [Fact]
    public async Task NoSourceAndUncertainAudioNeverOfferSetup()
    {
        await using var noSource = new ThxAudioViewModel(new MissingStackAdapter(), canInstallStack: () => false);
        await noSource.SetEnabledAsync(true);
        Assert.False(noSource.IsStackInstallationVisible);
        await using var uncertain = new ThxAudioViewModel(new MissingStackAdapter { Indeterminate = true }, canInstallStack: () => true);
        await uncertain.SetEnabledAsync(true);
        Assert.False(uncertain.IsStackInstallationVisible);
    }

    [Fact]
    public void LauncherUsesOnlyInstalledSessionAgentAndFixedElevatedAction()
    {
        var start = ThxAudioInstallLauncher.CreateStartInfo();
        Assert.Equal(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            "OpenBlade", "OpenBlade.SessionAgent.exe"), start.FileName);
        Assert.Equal("--request-thx-audio-provisioning", start.Arguments);
        Assert.False(start.UseShellExecute);
        Assert.True(start.CreateNoWindow);
        Assert.Empty(start.Verb);
        Assert.Equal(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "cmd.exe"),
            start.Environment["ComSpec"], ignoreCase: true);
        Assert.DoesNotContain(start.Environment.Keys, name => name.StartsWith("DOTNET_", StringComparison.OrdinalIgnoreCase)
            || name.StartsWith("CORECLR_", StringComparison.OrdinalIgnoreCase) || name.StartsWith("COR_", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(System.Diagnostics.ProcessWindowStyle.Hidden, start.WindowStyle);
        Assert.True(ThxAudioInstallLauncher.MatchesHost("Razer", "Blade 16 - RZ09-0528", "Razer", "Blade 16 - RZ09-0528"));
        Assert.True(ThxAudioInstallLauncher.MatchesHost("Razer", "Blade 14 - RZ09-0530", "Razer", "Blade 14 - RZ09-0530"));
        Assert.False(ThxAudioInstallLauncher.MatchesHost("Razer", "Blade 16 - RZ09-0581", "Razer", "Blade 16 - RZ09-0581"));
        Assert.False(ThxAudioInstallLauncher.MatchesHost("Razer", "Blade 16 - RZ09-0528", "Razer", "Blade 14 - RZ09-0530"));
    }

    [Fact]
    public async Task LauncherDistinguishesUacCancellationFromLostExitObservation()
    {
        Assert.Equal(ThxAudioProvisioningResult.Canceled,
            await new ThxAudioInstallLauncher((_, _) => throw new Win32Exception(1223)).RequestAsync(CancellationToken.None));
        Assert.Equal(ThxAudioProvisioningResult.RecoveryRequired,
            await new ThxAudioInstallLauncher((_, _) => throw new OperationCanceledException()).RequestAsync(CancellationToken.None));
        Assert.Equal(ThxAudioProvisioningResult.RecoveryRequired,
            await new ThxAudioInstallLauncher((_, _) => Task.FromResult(987)).RequestAsync(CancellationToken.None));
    }

    private sealed class MissingStackAdapter : IThxAudioAdapter
    {
        public bool Indeterminate { get; init; }
        public bool Available { get; set; }
        public int ConnectCalls { get; private set; }
        public int ReadCalls { get; private set; }
        public Task<SessionAudioResponse> DiscoverAsync(CancellationToken token) => Task.FromResult(new SessionAudioResponse(
            1, Guid.NewGuid(), Indeterminate ? ThxAudioOperationStatus.Indeterminate
                : Available ? ThxAudioOperationStatus.Succeeded : ThxAudioOperationStatus.Unavailable,
            Available ? ThxAudioAvailability.Available : ThxAudioAvailability.ComponentsUnavailable,
            Available ? null : "Missing driver package", null));
        public Task<SessionAudioResponse> ConnectAsync(ThxAudioEndpoint endpoint, CancellationToken token)
        { ConnectCalls++; return DiscoverAsync(token); }
        public Task<SessionAudioResponse> ReadAsync(ThxAudioEndpoint endpoint, CancellationToken token)
        { ReadCalls++; return DiscoverAsync(token); }
        public Task<SessionAudioResponse> SetSpatialAsync(ThxAudioEndpoint e, bool v, CancellationToken t) => throw new InvalidOperationException();
        public Task<SessionAudioResponse> SetEqualizerAsync(ThxAudioEndpoint e, bool v, CancellationToken t) => throw new InvalidOperationException();
        public Task<SessionAudioResponse> SelectPresetAsync(ThxAudioEndpoint e, string p, IReadOnlyList<double> g, CancellationToken t) => throw new InvalidOperationException();
        public Task<SessionAudioResponse> SetEnhancementAsync(ThxAudioEndpoint e, ThxAudioEnhancementKind k, bool v, int? l, CancellationToken t) => throw new InvalidOperationException();
    }
}
