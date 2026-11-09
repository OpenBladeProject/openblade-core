using System.Collections.Concurrent;
using OpenBlade.Contracts;
using OpenBlade.Tray;

namespace OpenBlade.Tray.Tests;

public sealed partial class MainViewModelTests
{
    [Theory]
    [InlineData(PowerSource.PluggedIn, false)]
    [InlineData(PowerSource.Battery, false)]
    [InlineData(PowerSource.UsbC, false)]
    [InlineData(PowerSource.PluggedIn, true)]
    [InlineData(PowerSource.Battery, true)]
    [InlineData(PowerSource.UsbC, true)]
    public async Task LightingStartup_ConfirmedOffOffersActiveSpectrumProfileRestore(
        PowerSource activeSource,
        bool startsWithUnknownPower)
    {
        var client = new FakeServiceClient();
        var dispatcher = new RecordingDispatcher();
        await using var viewModel = new MainViewModel(client, dispatcher);
        await viewModel.StartAsync();
        ServiceStatusSnapshot baseline = CreateStatus(
            verified: true,
            blocked: false,
            capabilities: DeviceCapabilities.SafeTargetDefaults() with
            {
                LightingEffects = new HashSet<LightingEffect>(
                    Enum.GetValues<LightingEffect>()),
            });
        LightingSettings off = LightingSettingsFactory.Off();
        LightingSettings spectrum = LightingSettingsFactory.SpectrumCycling(40);
        ProfileSet profiles = baseline.Profiles with
        {
            PluggedIn = baseline.Profiles.PluggedIn with { Lighting = off },
            Battery = baseline.Profiles.Battery with { Lighting = off },
            UsbC = baseline.Profiles.UsbC with { Lighting = off },
        };
        profiles = activeSource switch
        {
            PowerSource.PluggedIn => profiles with
            {
                PluggedIn = profiles.PluggedIn with { Lighting = spectrum },
            },
            PowerSource.Battery => profiles with
            {
                Battery = profiles.Battery with { Lighting = spectrum },
            },
            PowerSource.UsbC => profiles with
            {
                UsbC = profiles.UsbC with { Lighting = spectrum },
            },
            _ => throw new ArgumentOutOfRangeException(nameof(activeSource)),
        };
        ServiceStatusSnapshot known = baseline with
        {
            Revision = baseline.Revision + 1,
            ActivePowerSource = activeSource,
            Profiles = profiles,
            ConfirmedState = baseline.ConfirmedState! with { Lighting = off },
            DesiredState = baseline.DesiredState with
            {
                Lighting = new ResolvedSetting<LightingSettings>(
                    spectrum,
                    SettingValueSource.PowerProfile),
            },
        };
        var notifications = new ConcurrentQueue<(
            bool Difference,
            PowerSource Source,
            LightingEffect? Effect)>();
        viewModel.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(MainViewModel.HasLightingProfileDifference))
            {
                notifications.Enqueue((viewModel.HasLightingProfileDifference,
                    viewModel.ActivePowerSource, viewModel.SelectedLightingEffectOption?.Effect));
            }
        };
        var unknownApplied = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var knownApplied = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        dispatcher.AfterInvoke = () =>
        {
            if (viewModel.HasAppliedServiceState)
            {
                if (viewModel.ActivePowerSource == PowerSource.ExternalUnknown)
                {
                    unknownApplied.TrySetResult();
                }
                else if (viewModel.ActivePowerSource == activeSource)
                {
                    knownApplied.TrySetResult();
                }
            }
        };

        if (startsWithUnknownPower)
        {
            client.Publish(known with
            {
                Revision = baseline.Revision,
                ActivePowerSource = PowerSource.ExternalUnknown,
            });
            await unknownApplied.Task.WaitAsync(TimeSpan.FromSeconds(5));

            Assert.Equal(LightingEffect.Off, viewModel.SelectedLightingEffectOption?.Effect);
            Assert.False(viewModel.HasLightingProfileDifference);
            Assert.False(viewModel.DiscardLightingProfileDraftCommand.CanExecute(null));
            Assert.Equal("Waiting for power source", viewModel.LightingProfileRelationshipStatus);
            notifications.Clear();
        }

        client.Publish(known);
        await knownApplied.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(LightingEffect.Off, viewModel.SelectedLightingEffectOption?.Effect);
        Assert.False(viewModel.IsKeyboardEffectUnconfirmed);
        Assert.False(viewModel.HasLightingProfileDraft);
        Assert.True(viewModel.HasLightingProfileDifference);
        Assert.True(viewModel.DiscardLightingProfileDraftCommand.CanExecute(null));
        Assert.Equal((true, activeSource, (LightingEffect?)LightingEffect.Off), notifications.Last());
        Assert.Equal(profiles, viewModel.Profiles);
        Assert.Empty(client.MutationOperations);
    }
}
