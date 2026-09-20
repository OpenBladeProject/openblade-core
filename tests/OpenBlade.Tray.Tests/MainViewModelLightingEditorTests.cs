using OpenBlade.Contracts;
using OpenBlade.Tray;

namespace OpenBlade.Tray.Tests;

public sealed partial class MainViewModelTests
{
    [Fact]
    public async Task LightingColorRegression_RandomTidalStatusRoundTripsAndCommitsStoredPrimary()
    {
        var client = new FakeServiceClient();
        await using var viewModel = new MainViewModel(client, new RecordingDispatcher());
        await viewModel.StartAsync();
        ServiceStatusSnapshot initial = CreateStatus(verified: true, blocked: false,
            capabilities: DeviceCapabilities.SafeTargetDefaults() with
            {
                LightingEffects = new HashSet<LightingEffect>(Enum.GetValues<LightingEffect>()),
            });
        var primary = new RgbColor(12, 34, 56);
        var secondary = new RgbColor(78, 90, 123);
        LightingSettings incoming = LightingSettingsFactory.Tidal(
            primary, secondary, true, LightingDirection.Leftward, 63);
        client.Publish(initial with
        {
            ConfirmedState = initial.ConfirmedState! with { Lighting = incoming },
        });
        await WaitUntilAsync(() => viewModel.HasAppliedServiceState);

        Assert.Equal(incoming, viewModel.LightingDraft);
        Assert.False(viewModel.IsLightingApplyPending);
        Assert.False(viewModel.LightingShowsPrimaryColor);
        viewModel.LightingRandomColorsEditor = false;
        Assert.True(viewModel.LightingShowsPrimaryColor);
        Assert.Equal(primary, viewModel.LightingPrimaryColorEditor);
        LightingSettings draft = viewModel.LightingDraft;
        await Assert.IsType<AsyncRelayCommand>(viewModel.SetLightingCommand).ExecuteAsync(draft);
        Assert.Equal(LightingSettingsFactory.Tidal(
            primary, secondary, false, LightingDirection.Leftward, 63), client.LightingOverride?.Value);
    }

    [Fact]
    public async Task LightingColorRegression_WaveToStaticSelectionCommitsVisiblePrimary()
    {
        var client = new FakeServiceClient();
        await using var viewModel = new MainViewModel(client, new RecordingDispatcher());
        await viewModel.StartAsync();
        ServiceStatusSnapshot initial = CreateStatus(verified: true, blocked: false,
            capabilities: DeviceCapabilities.SafeTargetDefaults() with
            {
                LightingEffects = new HashSet<LightingEffect>(Enum.GetValues<LightingEffect>()),
            });
        client.Publish(initial with
        {
            ConfirmedState = initial.ConfirmedState! with
            {
                Lighting = LightingSettingsFactory.Wave(LightingDirection.Leftward, 63),
            },
        });
        await WaitUntilAsync(() => viewModel.HasAppliedServiceState);
        Assert.False(viewModel.LightingShowsPrimaryColor);

        viewModel.SelectedLightingEffectOption = viewModel.AvailableLightingEffectOptions
            .Single(option => option.Effect == LightingEffect.Static);

        Assert.True(viewModel.LightingShowsPrimaryColor);
        Assert.Equal(LightingSettingsFactory.DefaultVisibleColor, viewModel.LightingPrimaryColorEditor);
        LightingSettings draft = viewModel.LightingDraft;
        await Assert.IsType<AsyncRelayCommand>(viewModel.SetLightingCommand).ExecuteAsync(draft);
        Assert.Equal(LightingSettingsFactory.Static(LightingSettingsFactory.DefaultVisibleColor, 63),
            client.LightingOverride?.Value);
    }

    [Theory]
    [InlineData(ControlOperationStatus.Failed, false)]
    [InlineData(ControlOperationStatus.Blocked, false)]
    [InlineData(ControlOperationStatus.Applied, true)]
    [InlineData(ControlOperationStatus.Stored, true)]
    public async Task LightingCompletion_PreservesFailedOrNewerDrafts(
        ControlOperationStatus outcome,
        bool editWhilePending)
    {
        var client = new FakeServiceClient
        {
            BlockMutations = true,
            NextResult = new ControlOperationResult(outcome, "Lighting completion"),
        };
        await using var viewModel = new MainViewModel(client, new RecordingDispatcher());
        await viewModel.StartAsync();
        ServiceStatusSnapshot initial = CreateStatus(
            verified: true,
            blocked: false,
            capabilities: DeviceCapabilities.SafeTargetDefaults() with
            {
                LightingEffects = new HashSet<LightingEffect>(Enum.GetValues<LightingEffect>()),
                ChargeLimits = ChargeLimits(),
            });
        client.Publish(initial);
        await WaitUntilAsync(() => viewModel.HasAppliedServiceState);
        viewModel.LightingEffectEditor = LightingEffect.Static;
        viewModel.LightingBrightnessEditor = 61;
        LightingSettings submitted = viewModel.LightingDraft;
        Task apply = Assert.IsType<AsyncRelayCommand>(viewModel.SetLightingCommand)
            .ExecuteAsync(submitted);
        await client.MutationStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        if (editWhilePending)
        {
            viewModel.LightingBrightnessEditor = 79;
        }
        LightingSettings retained = viewModel.LightingDraft;
        client.CompleteMutation();
        await apply;

        Assert.True(viewModel.IsLightingApplyPending);
        Assert.Equal(retained, viewModel.LightingDraft);
        Assert.Equal(submitted, client.LightingOverride?.Value);
        Assert.False(viewModel.IsLightingOperationInProgress);
    }

    [Fact]
    public async Task LightingEditorNotifications_PreserveDirtyAndChannelObservationOrder()
    {
        var client = new FakeServiceClient();
        await using var viewModel = new MainViewModel(client, new RecordingDispatcher());
        await viewModel.StartAsync();
        ServiceStatusSnapshot initial = CreateStatus(verified: true, blocked: false);
        LightingSettings lighting = LightingSettingsFactory.Static(new RgbColor(1, 2, 3), 60);
        client.Publish(initial with
        {
            ConfirmedState = initial.ConfirmedState! with { Lighting = lighting },
        });
        await WaitUntilAsync(() => viewModel.HasAppliedServiceState);
        var observed = new List<(string Property, RgbColor Color)>();
        viewModel.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName is nameof(MainViewModel.IsLightingApplyPending)
                or nameof(MainViewModel.LightingRedEditor)
                or nameof(MainViewModel.LightingGreenEditor)
                or nameof(MainViewModel.LightingBlueEditor))
            {
                observed.Add((args.PropertyName, viewModel.LightingDraft.Color));
            }
        };

        viewModel.LightingPrimaryColorEditor = new RgbColor(4, 5, 6);

        Assert.Equal(new[]
        {
            (nameof(MainViewModel.IsLightingApplyPending), new RgbColor(1, 2, 3)),
            (nameof(MainViewModel.LightingRedEditor), new RgbColor(4, 2, 3)),
            (nameof(MainViewModel.LightingGreenEditor), new RgbColor(4, 5, 3)),
            (nameof(MainViewModel.LightingBlueEditor), new RgbColor(4, 5, 6)),
        }, observed);
    }

    [Fact]
    public async Task LightingConfirmationChange_FansOutBeforeTheEffectEdit()
    {
        var client = new FakeServiceClient();
        await using var viewModel = new MainViewModel(client, new RecordingDispatcher());
        await viewModel.StartAsync();
        ServiceStatusSnapshot initial = CreateStatus(verified: true, blocked: false);
        client.Publish(initial with
        {
            ConfirmedState = initial.ConfirmedState! with { Lighting = null },
            LightingOverride = null,
            LightingRendererOwnership = LightingRendererOwnershipState.None,
        });
        await WaitUntilAsync(() => viewModel.HasAppliedServiceState && viewModel.IsKeyboardEffectUnconfirmed);
        var observed = new List<(string Property, bool Unconfirmed, bool Dirty)>();
        viewModel.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName is nameof(MainViewModel.IsKeyboardEffectUnconfirmed)
                or nameof(MainViewModel.KeyboardEffectDescription)
                or nameof(MainViewModel.SelectedLightingEffectOption)
                or nameof(MainViewModel.IsLightingApplyPending))
            {
                observed.Add((args.PropertyName, viewModel.IsKeyboardEffectUnconfirmed,
                    viewModel.IsLightingApplyPending));
            }
        };

        viewModel.LightingEffectEditor = viewModel.LightingEffectEditor == LightingEffect.Static
            ? LightingEffect.Spectrum : LightingEffect.Static;

        Assert.Equal(new[]
        {
            (nameof(MainViewModel.IsKeyboardEffectUnconfirmed), false, false),
            (nameof(MainViewModel.KeyboardEffectDescription), false, false),
            (nameof(MainViewModel.SelectedLightingEffectOption), false, false),
            (nameof(MainViewModel.IsLightingApplyPending), false, true),
        }, observed.Take(4));
        Assert.Equal(nameof(MainViewModel.SelectedLightingEffectOption), observed[^1].Property);
    }
}
