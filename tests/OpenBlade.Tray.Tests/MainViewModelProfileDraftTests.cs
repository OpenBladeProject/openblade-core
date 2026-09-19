using OpenBlade.Contracts;
using OpenBlade.Tray;

namespace OpenBlade.Tray.Tests;

public sealed partial class MainViewModelTests
{
    [Fact]
    public async Task SubsystemProfileActions_SaveAndDiscardOnlyTheirOwnChanges()
    {
        var client = new FakeServiceClient();
        await using var viewModel = new MainViewModel(client, new RecordingDispatcher());
        await viewModel.StartAsync();
        ServiceStatusSnapshot baseline = CreateStatus(
            verified: true,
            blocked: false,
            capabilities: DeviceCapabilities.SafeTargetDefaults() with
            {
                LightingEffects = new HashSet<LightingEffect>(
                    Enum.GetValues<LightingEffect>()),
                ChargeLimits = ChargeLimits(),
            });
        client.Publish(baseline);
        await WaitUntilAsync(() => viewModel.IsConnected);
        LightingSettings lighting = LightingSettingsFactory.Static(
            new RgbColor(12, 34, 56),
            brightness: 67);

        await Assert.IsType<AsyncRelayCommand>(viewModel.SetPerformanceCommand)
            .ExecuteAsync(PerformancePreset.Performance);
        await Assert.IsType<AsyncRelayCommand>(viewModel.SetLightingCommand)
            .ExecuteAsync(lighting);

        Assert.True(viewModel.HasThermalProfileDraft);
        Assert.True(viewModel.HasLightingProfileDraft);
        Assert.Equal(
            "Temporary change not saved to this profile",
            viewModel.ThermalProfileRelationshipStatus);
        Assert.True(viewModel.SaveThermalProfileDraftCommand.CanExecute(null));

        await Assert.IsType<AsyncRelayCommand>(viewModel.SaveThermalProfileDraftCommand)
            .ExecuteAsync(null);

        Assert.Equal(PerformancePreset.Performance, viewModel.Profiles.PluggedIn.Performance);
        Assert.Equal(baseline.Profiles.PluggedIn.Lighting, viewModel.Profiles.PluggedIn.Lighting);
        Assert.Null(client.PerformanceOverride);
        Assert.NotNull(client.LightingOverride);
        Assert.False(viewModel.HasThermalProfileDraft);
        Assert.True(viewModel.HasLightingProfileDraft);

        await Assert.IsType<AsyncRelayCommand>(viewModel.DiscardLightingProfileDraftCommand)
            .ExecuteAsync(null);

        Assert.Null(client.LightingOverride);
        Assert.False(viewModel.HasLightingProfileDraft);
        Assert.False(viewModel.HasActiveProfileDraft);
    }

    [Fact]
    public async Task DashboardDiscard_ClearsThermalAndLightingOverridesTogether()
    {
        var client = new FakeServiceClient();
        await using var viewModel = new MainViewModel(client, new RecordingDispatcher());
        await viewModel.StartAsync();
        ServiceStatusSnapshot baseline = CreateStatus(
            verified: true,
            blocked: false,
            capabilities: DeviceCapabilities.SafeTargetDefaults() with
            {
                LightingEffects = new HashSet<LightingEffect>(
                    Enum.GetValues<LightingEffect>()),
            });
        client.Publish(baseline);
        await WaitUntilAsync(() => viewModel.CanControlHardware);

        await Assert.IsType<AsyncRelayCommand>(viewModel.SetPerformanceCommand)
            .ExecuteAsync(PerformancePreset.Performance);
        await Assert.IsType<AsyncRelayCommand>(viewModel.SetLightingCommand)
            .ExecuteAsync(LightingSettingsFactory.Static(
                new RgbColor(12, 34, 56),
                brightness: 67));

        Assert.True(viewModel.HasThermalProfileDifference);
        Assert.True(viewModel.HasLightingProfileDraft);

        await Assert.IsType<AsyncRelayCommand>(viewModel.DiscardProfileDraftCommand)
            .ExecuteAsync(null);

        Assert.Contains("performance:<clear>", client.MutationOperations);
        Assert.Contains("lighting:<clear>", client.MutationOperations);
        Assert.Null(client.PerformanceOverride);
        Assert.Null(client.LightingOverride);
        Assert.False(viewModel.HasActiveProfileDraft);
    }
}
