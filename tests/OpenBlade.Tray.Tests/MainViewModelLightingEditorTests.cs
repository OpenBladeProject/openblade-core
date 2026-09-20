using OpenBlade.Contracts;
using OpenBlade.Tray;

namespace OpenBlade.Tray.Tests;

public sealed partial class MainViewModelTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task LightingModeRegression_BoundModeChangeCommitsIntentionalBlack(
        bool starlight,
        bool startsDual)
    {
        var client = new FakeServiceClient();
        await using var viewModel = new MainViewModel(client, new RecordingDispatcher());
        await viewModel.StartAsync();
        var secondary = new RgbColor(78, 90, 123);
        LightingSettings lighting = starlight
            ? startsDual
                ? LightingSettingsFactory.StarlightDual(default, secondary, 63, StarlightLightingDuration.Long)
                : LightingSettingsFactory.StarlightSingle(default, 63, StarlightLightingDuration.Long)
            : startsDual
                ? LightingSettingsFactory.BreathingDual(default, secondary, 63)
                : LightingSettingsFactory.BreathingSingle(default, 63);
        ServiceStatusSnapshot initial = CreateStatus(verified: true, blocked: false,
            capabilities: DeviceCapabilities.SafeTargetDefaults() with
            {
                LightingEffects = new HashSet<LightingEffect>(Enum.GetValues<LightingEffect>()),
            });
        client.Publish(initial with
        {
            ConfirmedState = initial.ConfirmedState! with { Lighting = lighting },
        });
        await WaitUntilAsync(() => viewModel.HasAppliedServiceState);
        Assert.Equal(lighting, viewModel.LightingDraft);

        if (starlight)
        {
            viewModel.LightingStarlightColorModeEditor = startsDual
                ? StarlightColorMode.Single : StarlightColorMode.Dual;
        }
        else
        {
            viewModel.LightingBreathingColorModeEditor = startsDual
                ? BreathingColorMode.Single : BreathingColorMode.Dual;
        }

        Assert.True(viewModel.LightingShowsPrimaryColor);
        Assert.Equal(default, viewModel.LightingPrimaryColorEditor);
        LightingSettings draft = viewModel.LightingDraft;
        await Assert.IsType<AsyncRelayCommand>(viewModel.SetLightingCommand).ExecuteAsync(draft);
        Assert.Equal(draft, client.LightingOverride?.Value);
        Assert.Equal(default, client.LightingOverride!.Value.Color);
    }

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

    [Fact]
    public async Task LightingCompletion_DoesNotReloadAnIntermediateOwnershipSnapshot()
    {
        var client = new FakeServiceClient { BlockMutations = true };
        await using var viewModel = new MainViewModel(client, new RecordingDispatcher());
        await viewModel.StartAsync();
        LightingSettings prior = LightingSettingsFactory.Off();
        ServiceStatusSnapshot initial = CreateStatus(
            verified: true,
            blocked: false,
            capabilities: DeviceCapabilities.SafeTargetDefaults() with
            {
                LightingEffects = new HashSet<LightingEffect>(Enum.GetValues<LightingEffect>()),
                ChargeLimits = ChargeLimits(),
            });
        initial = initial with
        {
            ConfirmedState = initial.ConfirmedState! with { Lighting = prior },
            DesiredState = initial.DesiredState with
            {
                Lighting = new ResolvedSetting<LightingSettings>(
                    prior, SettingValueSource.TemporaryOverride),
            },
            LightingOverride = new ManualOverride<LightingSettings>(
                prior, OverrideLifetime.UntilPowerSourceChange),
            LightingRendererOwnership = LightingRendererOwnershipState.None,
        };
        client.Publish(initial);
        await WaitUntilAsync(() => viewModel.HasAppliedServiceState);
        Assert.Equal(prior, viewModel.LightingDraft);

        viewModel.LightingEffectEditor = LightingEffect.Static;
        viewModel.LightingBrightnessEditor = 65;
        viewModel.LightingPrimaryColorEditor = new RgbColor(12, 34, 56);
        LightingSettings requested = viewModel.LightingDraft;
        Task apply = Assert.IsType<AsyncRelayCommand>(viewModel.SetLightingCommand)
            .ExecuteAsync(requested);
        await client.MutationStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));

        // ReconcileScopedIntentReadbackAsync publishes renderer ownership before
        // the full snapshot; its confirmed and published intent fields are still old.
        client.Publish(initial with
        {
            Revision = initial.Revision + 1,
            Message = "Renderer ownership changed before final lighting status",
            LightingRendererOwnership = LightingRendererOwnershipState.Static,
        });
        await WaitUntilAsync(() => viewModel.ConnectionStatus
            == "Renderer ownership changed before final lighting status");
        Assert.Equal(requested, viewModel.LightingDraft);

        // The independent mutation response can overtake final status delivery.
        client.CompleteMutation();
        await apply;

        Assert.Equal(requested, viewModel.LightingDraft);
        Assert.False(viewModel.IsLightingApplyPending);
        client.Publish(initial with
        {
            Revision = initial.Revision + 2,
            Message = "Final correlated lighting status",
            LightingRendererOwnership = LightingRendererOwnershipState.Static,
            ConfirmedState = initial.ConfirmedState! with { Lighting = requested },
            DesiredState = initial.DesiredState with
            {
                Lighting = new ResolvedSetting<LightingSettings>(
                    requested, SettingValueSource.TemporaryOverride),
            },
            LightingOverride = new ManualOverride<LightingSettings>(
                requested, OverrideLifetime.UntilPowerSourceChange),
        });
        await WaitUntilAsync(() => viewModel.ConnectionStatus == "Final correlated lighting status");
        Assert.Equal(requested, viewModel.LightingDraft);
        Assert.False(viewModel.IsLightingApplyPending);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LightingCompletion_OffKeepsDirtyComparisonAgainstTheActualSubmittedValue(
        bool legacyRequest)
    {
        var client = new FakeServiceClient { BlockMutations = true };
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
        client.Publish(initial with
        {
            ConfirmedState = initial.ConfirmedState! with
            {
                Lighting = LightingSettingsFactory.Static(new RgbColor(12, 34, 56), 65),
            },
        });
        await WaitUntilAsync(() => viewModel.HasAppliedServiceState);
        viewModel.LightingEffectEditor = LightingEffect.Off;
        LightingSettings draft = viewModel.LightingDraft;
        LightingSettings submitted = legacyRequest
            ? new LightingSettings(LightingEffect.Off, default, 0)
            : draft;
        LightingSettings serviceIntent = LightingSettingsFactory.NormalizeLegacyOff(submitted);
        Assert.Equal(draft, serviceIntent);
        Assert.True(viewModel.IsLightingApplyPending);
        Task apply = Assert.IsType<AsyncRelayCommand>(viewModel.SetLightingCommand)
            .ExecuteAsync(submitted);
        await client.MutationStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        LightingSettings confirmed = LightingSettingsFactory.SpectrumCycling(40);
        client.Publish(initial with
        {
            Revision = initial.Revision + 1,
            Message = "Off intent with differing confirmed readback",
            ConfirmedState = initial.ConfirmedState! with { Lighting = confirmed },
            DesiredState = initial.DesiredState with
            {
                Lighting = new ResolvedSetting<LightingSettings>(
                    serviceIntent, SettingValueSource.TemporaryOverride),
            },
            LightingOverride = new ManualOverride<LightingSettings>(
                serviceIntent, OverrideLifetime.UntilPowerSourceChange),
        });
        await WaitUntilAsync(() => viewModel.ConnectionStatus
            == "Off intent with differing confirmed readback");

        client.CompleteMutation();
        await apply;

        Assert.Equal(submitted, client.LightingOverride?.Value);
        // Service normalization must not make a different editor value count as
        // the actual submitted draft when deciding whether to release the guard.
        Assert.Equal(legacyRequest, viewModel.IsLightingApplyPending);
        Assert.Equal(legacyRequest ? draft : confirmed, viewModel.LightingDraft);
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
