using OpenBlade.Session;
using OpenBlade.Contracts;

namespace OpenBlade.Tray.Tests;

public sealed class ThxAudioViewModelTests
{
    [Fact]
    public async Task ReadOnlyCompatibilityDisplaysNativeStateWithoutEnablingChanges()
    {
        var adapter = new FakeAdapter
        {
            ReadResponse = Response(ThxAudioOperationStatus.Succeeded, AdvancedState()) with
            { Availability = ThxAudioAvailability.AvailableReadOnly },
        };
        await using var viewModel = new ThxAudioViewModel(adapter);
        viewModel.ApplyPreference(true);
        await viewModel.RefreshAsync();
        Assert.True(viewModel.CanRead);
        Assert.False(viewModel.CanEdit);
        Assert.False(viewModel.CanEditEqualizerBands);
        Assert.False(viewModel.CanCaptureAudioProfile);
        Assert.False(viewModel.CanConfigureApplicationRouting);
        viewModel.ApplicationRoutingEnabled = true;
        Assert.False(viewModel.ApplicationRoutingEnabled);
        Assert.NotEmpty(viewModel.EqualizerBands);
        await viewModel.SetSpatialAsync(true);
        Assert.Equal(["discover", "read:Speakers"], adapter.Calls);
    }

    [Fact]
    public async Task PreferenceRestoreAndShutdownDoNotContactAudioBackend()
    {
        var adapter = new FakeAdapter();
        await using (var viewModel = new ThxAudioViewModel(adapter))
        {
            Assert.False(viewModel.IsEnabled);
            viewModel.ApplyPreference(true);
            Assert.True(viewModel.CanRead);
            Assert.False(viewModel.CanEdit);
        }
        Assert.Empty(adapter.Calls);
    }

    [Fact]
    public async Task EnablingDiscoversComponentsWithoutRegisteringNativeDevice()
    {
        var adapter = new FakeAdapter();
        await using var viewModel = new ThxAudioViewModel(adapter);
        await viewModel.SetEnabledAsync(true);
        Assert.Equal(["discover"], adapter.Calls);
        Assert.False(viewModel.CanEdit);
        Assert.Empty(viewModel.Presets);
    }

    [Fact]
    public async Task ExplicitReadEnablesOnlyConfirmedEndpointControls()
    {
        var adapter = new FakeAdapter();
        await using var viewModel = new ThxAudioViewModel(adapter);
        viewModel.ApplyPreference(true);
        await viewModel.SetSpatialAsync(true);
        Assert.Empty(adapter.Calls);
        await viewModel.RefreshAsync();
        Assert.Equal(["discover", "read:Speakers"], adapter.Calls);
        Assert.True(viewModel.CanEdit);
        Assert.False(viewModel.SpatialEnabled);
        Assert.Equal("music", viewModel.SelectedPreset?.Id);
        viewModel.SelectEndpoint(ThxAudioEndpoint.Headphones);
        Assert.False(viewModel.CanEdit);
        Assert.Empty(viewModel.Presets);
        Assert.Equal(2, adapter.Calls.Count);
    }

    [Fact]
    public async Task IndeterminateMutationRequiresExplicitReadAndIsNeverReplayed()
    {
        var adapter = new FakeAdapter();
        await using var viewModel = new ThxAudioViewModel(adapter);
        viewModel.ApplyPreference(true);
        await viewModel.RefreshAsync();
        adapter.MutationResponse = Response(ThxAudioOperationStatus.Indeterminate);
        await viewModel.SetSpatialAsync(true);
        Assert.False(viewModel.CanEdit);
        Assert.False(viewModel.SpatialEnabled);
        await viewModel.SetSpatialAsync(true);
        Assert.Equal(1, adapter.Calls.Count(call => call == "spatial:True"));
        await viewModel.RefreshAsync();
        Assert.True(viewModel.CanEdit);
    }

    [Fact]
    public async Task MismatchedReadbackDoesNotEnableControls()
    {
        var adapter = new FakeAdapter
        {
            ReadResponse = Response(ThxAudioOperationStatus.Succeeded, State(ThxAudioEndpoint.Headphones)),
        };
        await using var viewModel = new ThxAudioViewModel(adapter);
        viewModel.ApplyPreference(true);
        await viewModel.RefreshAsync();
        Assert.False(viewModel.CanEdit);
    }

    [Fact]
    public async Task MissingComponentsNeverReachNativeReadOrMutation()
    {
        var adapter = new FakeAdapter
        {
            DiscoveryResponse = new(SessionAudioIpc.ProtocolVersion, Guid.NewGuid(),
                ThxAudioOperationStatus.Unavailable, ThxAudioAvailability.ComponentsUnavailable, null, null),
        };
        await using var viewModel = new ThxAudioViewModel(adapter);
        viewModel.ApplyPreference(true);
        await viewModel.RefreshAsync();
        await viewModel.SetEqualizerAsync(true);
        Assert.Equal(["discover"], adapter.Calls);
        Assert.False(viewModel.CanEdit);
    }

    [Fact]
    public async Task OnlyAdvertisedPresetsCanBeSubmitted()
    {
        var adapter = new FakeAdapter
        {
            ReadResponse = Response(ThxAudioOperationStatus.Succeeded, AdvancedState() with { Presets = State().Presets }),
        };
        await using var viewModel = new ThxAudioViewModel(adapter);
        viewModel.ApplyPreference(true);
        await viewModel.RefreshAsync();
        await viewModel.SelectPresetAsync(new ThxAudioPreset("unknown", "Unknown"));
        await viewModel.SelectPresetAsync(viewModel.Presets[1]);
        Assert.Equal(["discover", "read:Speakers", "preset:game"], adapter.Calls);
    }

    [Fact]
    public async Task DisconnectDuringMutationClearsConfirmationWithoutRetry()
    {
        var adapter = new FakeAdapter { ThrowOnMutation = true };
        await using var viewModel = new ThxAudioViewModel(adapter);
        viewModel.ApplyPreference(true);
        await viewModel.RefreshAsync();
        await viewModel.SetSpatialAsync(true);
        Assert.False(viewModel.CanEdit);
        Assert.False(viewModel.IsBusy);
        Assert.Equal(1, adapter.Calls.Count(call => call == "spatial:True"));
    }

    [Fact]
    public async Task EqualizerEditsAreStagedAndApplyUsesTheVisibleBandVector()
    {
        var initial = AdvancedState();
        var adapter = new FakeAdapter { ReadResponse = Response(ThxAudioOperationStatus.Succeeded, initial) };
        await using var viewModel = new ThxAudioViewModel(adapter);
        viewModel.ApplyPreference(true);
        await viewModel.RefreshAsync();
        viewModel.EqualizerBands[0].DraftGainDb = 2;
        Assert.Equal(2, adapter.Calls.Count);
        Assert.True(viewModel.CanApplyEqualizer);
        adapter.MutationResponse = Response(ThxAudioOperationStatus.Succeeded, initial with
        {
            SelectedPresetId = "music",
            EqualizerBands = initial.EqualizerBands.Select((band, index) => index == 0 ? band with { GainDb = 2 } : band).ToArray(),
        });
        await viewModel.ApplyEqualizerAsync();
        Assert.Equal("preset:music", adapter.Calls[^1]);
        Assert.Equal(new double[] { 2, 0, 0, 0, 0, 0, 0 }, Assert.Single(adapter.PresetRequests).Gains);
        Assert.False(viewModel.HasEqualizerChanges);
        Assert.Equal("music", viewModel.SelectedPreset?.Id);
    }

    [Fact]
    public async Task UnrelatedReadbackPreservesEqualizerDraftAndDiscardNeverWrites()
    {
        var initial = AdvancedState();
        var adapter = new FakeAdapter
        {
            ReadResponse = Response(ThxAudioOperationStatus.Succeeded, initial),
            MutationResponse = Response(ThxAudioOperationStatus.Succeeded, initial with { SpatialEnabled = true }),
        };
        await using var viewModel = new ThxAudioViewModel(adapter);
        viewModel.ApplyPreference(true);
        await viewModel.RefreshAsync();
        viewModel.EqualizerBands[0].DraftGainDb = -3;
        await viewModel.SetSpatialAsync(true);
        Assert.Equal(-3, viewModel.EqualizerBands[0].DraftGainDb);
        viewModel.DiscardEqualizerChanges();
        Assert.Equal(0, viewModel.EqualizerBands[0].DraftGainDb);
        Assert.Equal(3, adapter.Calls.Count);
        viewModel.EqualizerBands[0].DraftGainDb = double.NaN;
        viewModel.EqualizerBands[0].DraftGainDb = 13;
        Assert.False(viewModel.HasEqualizerChanges);
        viewModel.SelectEndpoint(ThxAudioEndpoint.Headphones);
        Assert.Empty(viewModel.EqualizerBands);
    }

    [Fact]
    public async Task EnhancementToggleDoesNotApplyStagedLevelAndExplicitApplyDoes()
    {
        var initial = AdvancedState();
        var adapter = new FakeAdapter { ReadResponse = Response(ThxAudioOperationStatus.Succeeded, initial) };
        await using var viewModel = new ThxAudioViewModel(adapter);
        viewModel.ApplyPreference(true);
        await viewModel.RefreshAsync();
        viewModel.SoundNormalizationLevel = 75;
        Assert.False(viewModel.CanApplySoundNormalization);
        await viewModel.ApplySoundNormalizationAsync();
        Assert.Equal(2, adapter.Calls.Count);
        adapter.MutationResponse = Response(ThxAudioOperationStatus.Succeeded, initial with { SoundNormalization = new(true, 50) });
        await viewModel.SetSoundNormalizationAsync(true);
        Assert.Equal("enhancement:SoundNormalization:True:", adapter.Calls[^1]);
        Assert.Equal(75, viewModel.SoundNormalizationLevel);
        adapter.MutationResponse = Response(ThxAudioOperationStatus.Succeeded, initial with { SoundNormalization = new(true, 75) });
        await viewModel.ApplySoundNormalizationAsync();
        Assert.Equal("enhancement:SoundNormalization:True:75", adapter.Calls[^1]);
        Assert.False(viewModel.CanApplySoundNormalization);
    }

    [Fact]
    public async Task ApplicationRulesAreSemanticUniqueAndDoNotWriteAudio()
    {
        var adapter = new FakeAdapter
        {
            ReadResponse = Response(ThxAudioOperationStatus.Succeeded, AdvancedState() with
            {
                ApplicationProfiles = ThxAudioFeatureAccess.Available,
                ApplicationProfileCatalog = [new("game.exe", true, false)],
            })
        };
        await using var viewModel = new ThxAudioViewModel(adapter);
        viewModel.LoadProfile(PowerSource.PluggedIn, ProfileSettings());
        viewModel.ApplyPreference(true);
        await viewModel.RefreshAsync();
        viewModel.ApplicationExecutable = " GAME.EXE ";
        Assert.DoesNotContain(viewModel.ApplicationOutputs, output => output.Output == ThxApplicationAudioOutput.Competitive);
        viewModel.ApplicationOutput = viewModel.ApplicationOutputs.Single(output => output.Output == ThxApplicationAudioOutput.Immersive);
        viewModel.AddApplicationRule();
        Assert.Equal("game.exe", Assert.Single(viewModel.ApplicationRules).ExecutableName);
        viewModel.ApplicationExecutable = "Game.exe";
        Assert.False(viewModel.CanAddApplicationRule);
        Assert.Equal(2, adapter.Calls.Count);
        Assert.Equal(ThxApplicationAudioOutput.Immersive, Assert.Single(viewModel.CreateProfileDraft()!.ApplicationRules).Output);
        viewModel.LoadProfile(PowerSource.PluggedIn, ProfileSettings());
        Assert.Single(viewModel.ApplicationRules); // Equivalent status refresh preserves the draft.
        viewModel.LoadProfile(PowerSource.Battery, null);
        Assert.Empty(viewModel.ApplicationRules);
        Assert.Null(viewModel.CreateProfileDraft());
    }

    [Fact]
    public async Task CaptureReadsBothOutputsAndDoesNotGuessDefaultPresetMeaning()
    {
        var adapter = new FakeAdapter
        {
            ReadHandler = endpoint => Response(ThxAudioOperationStatus.Succeeded,
            AdvancedState(endpoint) with { SelectedPresetId = endpoint == ThxAudioEndpoint.Headphones ? "default" : "music" })
        };
        await using var viewModel = new ThxAudioViewModel(adapter);
        viewModel.ApplyPreference(true);
        await viewModel.CaptureAudioProfileAsync();
        Assert.Equal(["read:Speakers", "read:Headphones"], adapter.Calls);
        Assert.Null(viewModel.CreateProfileDraft());
        Assert.Contains("Choose an EQ preset", viewModel.AudioProfileMessage);
        adapter.ReadHandler = endpoint => Response(ThxAudioOperationStatus.Succeeded, AdvancedState(endpoint));
        await viewModel.CaptureAudioProfileAsync();
        ThxAudioProfileSettings captured = Assert.IsType<ThxAudioProfileSettings>(viewModel.CreateProfileDraft());
        Assert.Equal(7, captured.Speakers.EqualizerCurves.Music.Count);
        Assert.Equal(10, captured.Headphones.EqualizerCurves.Music.Count);
        Assert.Null(captured.Headphones.SoundNormalization);
        Assert.False(captured.ApplicationRoutingEnabled);
        Assert.DoesNotContain(adapter.Calls, call => call.StartsWith("preset:", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ResetAndFlattenStageOnlySelectedPresetAndKeepOtherModifiedCurves()
    {
        ThxAudioProfileSettings profile = ProfileSettings();
        static double[] Gains(double value) => Enumerable.Repeat(value, 7).ToArray();
        profile = profile with
        {
            Speakers = profile.Speakers with
            { EqualizerCurves = new(Gains(3), Gains(4), Gains(5), Gains(6), Gains(7)) }
        };
        ThxAudioState initial = AdvancedState() with
        {
            Presets = [new("game", "Game"), new("movie", "Movie"), new("music", "Music"), new("voice", "Voice"), new("custom", "Custom")],
            EqualizerBands = AdvancedState().EqualizerBands.Select(band => band with { GainDb = 5 }).ToArray(),
            FactoryPresetCurves = State().FactoryPresetCurves.Select(curve => curve.PresetId == "music"
                ? curve with { GainsDb = Gains(2) } : curve).ToArray(),
        };
        var adapter = new FakeAdapter { ReadResponse = Response(ThxAudioOperationStatus.Succeeded, initial) };
        await using var viewModel = new ThxAudioViewModel(adapter);
        viewModel.LoadProfile(PowerSource.PluggedIn, profile);
        viewModel.ApplyPreference(true);
        await viewModel.RefreshAsync();
        viewModel.ResetEqualizerToFactory();
        Assert.All(viewModel.EqualizerBands, band => Assert.Equal(2, band.DraftGainDb));
        viewModel.DiscardEqualizerChanges();
        Assert.All(viewModel.EqualizerBands, band => Assert.Equal(5, band.DraftGainDb));
        viewModel.FlattenEqualizer();
        Assert.All(viewModel.EqualizerBands, band => Assert.Equal(0, band.DraftGainDb));
        Assert.Equal(2, adapter.Calls.Count);
        adapter.MutationResponse = Response(ThxAudioOperationStatus.Succeeded, initial with
        { EqualizerBands = initial.EqualizerBands.Select(band => band with { GainDb = 0 }).ToArray() });
        await viewModel.ApplyEqualizerAsync();
        Assert.Equal("music", Assert.Single(adapter.PresetRequests).Id);
        Assert.Equal(Gains(0), adapter.PresetRequests[0].Gains);
        foreach ((string id, double gain) in new[] { ("game", 3d), ("movie", 4d), ("voice", 6d), ("custom", 7d), ("music", 0d) })
        {
            adapter.MutationResponse = Response(ThxAudioOperationStatus.Succeeded, initial with
            {
                SelectedPresetId = id,
                EqualizerBands = initial.EqualizerBands.Select(band => band with { GainDb = gain }).ToArray(),
            });
            await viewModel.SelectPresetAsync(viewModel.Presets.Single(preset => preset.Id == id));
            Assert.Equal(id, adapter.PresetRequests[^1].Id);
            Assert.Equal(Gains(gain), adapter.PresetRequests[^1].Gains);
        }
        viewModel.ResetEqualizerToFactory();
        Assert.All(viewModel.EqualizerBands, band => Assert.Equal(2, band.DraftGainDb));
        Assert.Equal("music", viewModel.SelectedPreset?.Id);
        Assert.Equal(6, adapter.PresetRequests.Count);
    }

    [Fact]
    public async Task CustomPresetEditsApplyAsCustom()
    {
        ThxAudioState initial = AdvancedState() with { SelectedPresetId = "custom" };
        var adapter = new FakeAdapter { ReadResponse = Response(ThxAudioOperationStatus.Succeeded, initial) };
        await using var viewModel = new ThxAudioViewModel(adapter);
        viewModel.ApplyPreference(true);
        await viewModel.RefreshAsync();
        viewModel.EqualizerBands[0].DraftGainDb = -2;
        adapter.MutationResponse = Response(ThxAudioOperationStatus.Succeeded, initial with
        { EqualizerBands = initial.EqualizerBands.Select((band, index) => index == 0 ? band with { GainDb = -2 } : band).ToArray() });
        await viewModel.ApplyEqualizerAsync();
        Assert.Equal("custom", Assert.Single(adapter.PresetRequests).Id);
        Assert.Equal(-2, adapter.PresetRequests[0].Gains[0]);
        Assert.False(viewModel.HasEqualizerChanges);
    }

    [Fact]
    public async Task DefaultStaysReadOnlyAndItsObservedCurveCanBeRestored()
    {
        ThxAudioState initial = AdvancedState() with
        {
            SelectedPresetId = "default",
            Presets = [new("default", "Default"), new("music", "Music")],
            EqualizerBands = AdvancedState().EqualizerBands.Select(band => band with { GainDb = 5 }).ToArray(),
        };
        var adapter = new FakeAdapter { ReadResponse = Response(ThxAudioOperationStatus.Succeeded, initial) };
        await using var viewModel = new ThxAudioViewModel(adapter);
        viewModel.ApplyPreference(true);
        await viewModel.RefreshAsync();
        Assert.Equal("default", viewModel.SelectedPreset?.Id);
        Assert.False(viewModel.CanEditEqualizerBands);
        Assert.False(viewModel.CanResetEqualizer);
        viewModel.EqualizerBands[0].DraftGainDb = 2;
        viewModel.FlattenEqualizer();
        viewModel.ResetEqualizerToFactory();
        await viewModel.ApplyEqualizerAsync();
        Assert.Equal(5, viewModel.EqualizerBands[0].DraftGainDb);
        Assert.Equal(2, adapter.Calls.Count);
        adapter.MutationResponse = Response(ThxAudioOperationStatus.Succeeded, initial with { SelectedPresetId = "music" });
        await viewModel.SelectPresetAsync(viewModel.Presets.Single(preset => preset.Id == "music"));
        Assert.True(viewModel.CanEditEqualizerBands);
        adapter.MutationResponse = Response(ThxAudioOperationStatus.Succeeded, initial);
        await viewModel.SelectPresetAsync(viewModel.Presets.Single(preset => preset.Id == "default"));
        Assert.Equal("default", adapter.PresetRequests[^1].Id);
        Assert.Equal(Enumerable.Repeat(5d, 7), adapter.PresetRequests[^1].Gains);
        Assert.False(viewModel.CanEditEqualizerBands);
    }

    [Fact]
    public async Task NativeApplicationPresetIsVisibleWithoutInventingAnEditablePreset()
    {
        var adapter = new FakeAdapter
        {
            ReadResponse = Response(ThxAudioOperationStatus.Succeeded,
            AdvancedState() with { SelectedPresetId = null, SelectedNativeProfileId = "NativeGame_imm" })
        };
        await using var viewModel = new ThxAudioViewModel(adapter);
        viewModel.ApplyPreference(true);
        await viewModel.RefreshAsync();
        Assert.Null(viewModel.SelectedPreset);
        Assert.Contains("NativeGame_imm", viewModel.ActiveProfileMessage);
        Assert.False(viewModel.CanEditEqualizerBands);
        viewModel.FlattenEqualizer();
        await viewModel.ApplyEqualizerAsync();
        Assert.Empty(adapter.PresetRequests);
    }

    [Fact]
    public async Task ReadingDifferentPresetDiscardsStagedCurveFromPriorPreset()
    {
        ThxAudioState current = AdvancedState();
        var adapter = new FakeAdapter { ReadHandler = _ => Response(ThxAudioOperationStatus.Succeeded, current) };
        await using var viewModel = new ThxAudioViewModel(adapter);
        viewModel.ApplyPreference(true);
        await viewModel.RefreshAsync();
        viewModel.EqualizerBands[0].DraftGainDb = -3;
        current = current with
        {
            SelectedPresetId = "custom",
            EqualizerBands = current.EqualizerBands.Select(band => band with { GainDb = 1 }).ToArray()
        };
        await viewModel.RefreshAsync();
        Assert.Equal("custom", viewModel.SelectedPreset?.Id);
        Assert.All(viewModel.EqualizerBands, band => Assert.Equal(1, band.DraftGainDb));
        Assert.False(viewModel.HasEqualizerChanges);
        Assert.Empty(adapter.PresetRequests);
    }
    [Fact]
    public async Task CapturedProfileTracksConfirmedEditedPresetWithoutChangingOtherOutput()
    {
        var adapter = new FakeAdapter
        {
            ReadHandler = output => Response(ThxAudioOperationStatus.Succeeded, AdvancedState(output)),
        };
        await using var viewModel = new ThxAudioViewModel(adapter);
        viewModel.ApplyPreference(true);
        await viewModel.CaptureAudioProfileAsync();
        ThxAudioEndpointSettings headphones = viewModel.CreateProfileDraft()!.Headphones;
        viewModel.EqualizerBands[0].DraftGainDb = 4;
        ThxAudioState updated = AdvancedState() with
        {
            EqualizerBands = AdvancedState().EqualizerBands.Select((band, index) =>
                index == 0 ? band with { GainDb = 4 } : band).ToArray(),
        };
        adapter.MutationResponse = Response(ThxAudioOperationStatus.Succeeded, updated);
        await viewModel.ApplyEqualizerAsync();
        ThxAudioProfileSettings saved = viewModel.CreateProfileDraft()!;
        Assert.Equal(ThxEqualizerPreset.Music, saved.Speakers.SelectedPreset);
        Assert.Equal(4, saved.Speakers.EqualizerCurves.Music[0]);
        Assert.All(saved.Speakers.EqualizerCurves.Custom, gain => Assert.Equal(0, gain));
        Assert.Same(headphones, saved.Headphones);
    }
    [Fact]
    public async Task PowerProfileChangeDuringMutationPreservesNewSavedSettingsAndPresetCurves()
    {
        var pending = new TaskCompletionSource<SessionAudioResponse>();
        ThxAudioState initial = AdvancedState();
        var adapter = new FakeAdapter
        {
            ReadResponse = Response(ThxAudioOperationStatus.Succeeded, initial),
            AsyncMutationHandler = _ => pending.Task,
        };
        await using var viewModel = new ThxAudioViewModel(adapter);
        viewModel.LoadProfile(PowerSource.PluggedIn, ProfileSettings());
        viewModel.ApplyPreference(true);
        await viewModel.RefreshAsync();
        viewModel.EqualizerBands[0].DraftGainDb = 7;
        Task mutation = viewModel.ApplyEqualizerAsync();
        Assert.True(viewModel.IsBusy);

        ThxAudioProfileSettings battery = ProfileSettings();
        battery = battery with
        {
            Speakers = battery.Speakers with
            {
                SpatialEnabled = true,
                EqualizerCurves = battery.Speakers.EqualizerCurves with
                {
                    Music = Enumerable.Repeat(3d, 7).ToArray(),
                    Custom = Enumerable.Repeat(4d, 7).ToArray(),
                },
            },
        };
        viewModel.LoadProfile(PowerSource.Battery, battery);
        pending.SetResult(Response(ThxAudioOperationStatus.Succeeded, initial with
        {
            EqualizerBands = initial.EqualizerBands.Select((band, index) =>
                index == 0 ? band with { GainDb = 7 } : band).ToArray(),
        }));
        await mutation;

        ThxAudioProfileSettings saved = viewModel.CreateProfileDraft()!;
        Assert.Same(battery.Speakers, saved.Speakers);
        Assert.Same(battery.Headphones, saved.Headphones);
        Assert.False(viewModel.SpatialEnabled);
        Assert.Equal(7, viewModel.EqualizerBands[0].DraftGainDb);
        Assert.False(viewModel.HasEqualizerChanges);

        adapter.AsyncMutationHandler = null;
        foreach ((string id, double gain) in new[] { ("custom", 4d), ("music", 3d) })
        {
            adapter.MutationResponse = Response(ThxAudioOperationStatus.Succeeded, initial with
            {
                SelectedPresetId = id,
                EqualizerBands = initial.EqualizerBands.Select(band => band with { GainDb = gain }).ToArray(),
            });
            await viewModel.SelectPresetAsync(viewModel.Presets.Single(preset => preset.Id == id));
            Assert.Equal(id, adapter.PresetRequests[^1].Id);
            Assert.Equal(Enumerable.Repeat(gain, 7), adapter.PresetRequests[^1].Gains);
        }
    }

    [Fact]
    public async Task ConfirmedMutationWithUnrepresentablePresetRequiresRecapture()
    {
        var adapter = new FakeAdapter
        {
            ReadHandler = output => Response(ThxAudioOperationStatus.Succeeded, AdvancedState(output)),
        };
        await using var viewModel = new ThxAudioViewModel(adapter);
        viewModel.ApplyPreference(true);
        await viewModel.CaptureAudioProfileAsync();
        Assert.NotNull(viewModel.CreateProfileDraft());
        adapter.MutationResponse = Response(ThxAudioOperationStatus.Succeeded,
            AdvancedState() with { SpatialEnabled = true, SelectedPresetId = "default" });

        await viewModel.SetSpatialAsync(true);

        Assert.Null(viewModel.CreateProfileDraft());
        Assert.False(viewModel.CanSaveAudioProfile);
    }

    [Theory]
    [InlineData(7, 10)]
    [InlineData(10, 7)]
    public async Task ImportedSpeakerGeometryAlignsHighBandsBeforeSelectingAnotherPreset(
        int importedCount,
        int confirmedCount)
    {
        static double[] Gains(int count, double start) => Enumerable.Range(0, count)
            .Select(index => start + index).ToArray();
        static ThxEqualizerCurves Curves(int count) => new(
            Gains(count, 1), Gains(count, -10), Gains(count, -5),
            Gains(count, -3), Gains(count, 0));

        ThxAudioProfileSettings imported = ProfileSettings() with
        {
            Speakers = ProfileSettings().Speakers with
            {
                EqualizerCurves = Curves(importedCount),
            },
        };
        ThxAudioState confirmed = SpeakerState(confirmedCount);
        double[] factoryGame = confirmed.FactoryPresetCurves.Single(
            curve => curve.PresetId == "game").GainsDb.ToArray();
        double[] importedGame = imported.Speakers.EqualizerCurves.Game.ToArray();
        double[] expected = factoryGame.ToArray();
        int overlap = Math.Min(importedGame.Length, expected.Length);
        Array.Copy(importedGame, importedGame.Length - overlap,
            expected, expected.Length - overlap, overlap);
        var adapter = new FakeAdapter
        {
            ReadResponse = Response(ThxAudioOperationStatus.Succeeded, confirmed),
            MutationResponse = Response(ThxAudioOperationStatus.Succeeded, confirmed with
            {
                SelectedPresetId = "game",
                EqualizerBands = confirmed.EqualizerBands.Select((band, index) =>
                    band with { GainDb = expected[index] }).ToArray(),
            }),
        };
        await using var viewModel = new ThxAudioViewModel(adapter);
        viewModel.LoadProfile(PowerSource.PluggedIn, imported);
        viewModel.ApplyPreference(true);

        await viewModel.RefreshAsync();

        Assert.Equal(importedCount,
            viewModel.CreateProfileDraft()!.Speakers.EqualizerCurves.Game.Count);
        await viewModel.SelectPresetAsync(
            viewModel.Presets.Single(preset => preset.Id == "game"));
        Assert.Equal(expected, adapter.PresetRequests[^1].Gains);
        ThxEqualizerCurves captured = viewModel.CreateProfileDraft()!.Speakers.EqualizerCurves;
        Assert.All(new[] { captured.Game, captured.Movie, captured.Music,
            captured.Voice, captured.Custom }, curve => Assert.Equal(confirmedCount, curve.Count));
        Assert.Equal(expected, captured.Game);
    }

    [Fact]
    public async Task LateReadFromOldProfileDoesNotNormalizeReplacementProfileCurves()
    {
        var pending = new TaskCompletionSource<SessionAudioResponse>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var adapter = new FakeAdapter
        {
            AsyncReadHandler = _ => pending.Task,
            MutationResponse = Response(ThxAudioOperationStatus.Succeeded, SpeakerState(7) with
            {
                SelectedPresetId = "game",
            }),
        };
        await using var viewModel = new ThxAudioViewModel(adapter);
        viewModel.LoadProfile(PowerSource.PluggedIn, ProfileSettings());
        viewModel.ApplyPreference(true);
        Task refresh = viewModel.RefreshAsync();
        ThxAudioProfileSettings replacement = ProfileSettings() with
        {
            Speakers = ProfileSettings().Speakers with
            {
                EqualizerCurves = UniformCurves(10, 6),
            },
        };

        viewModel.LoadProfile(PowerSource.Battery, replacement);
        pending.SetResult(Response(ThxAudioOperationStatus.Succeeded, SpeakerState(7)));
        await refresh;

        Assert.Same(replacement.Speakers, viewModel.CreateProfileDraft()!.Speakers);
        await viewModel.SelectPresetAsync(
            viewModel.Presets.Single(preset => preset.Id == "game"));
        Assert.Equal(Enumerable.Repeat(6d, 10), adapter.PresetRequests[^1].Gains);
    }

    private static ThxAudioProfileSettings ProfileSettings()
    {
        static ThxAudioEndpointSettings Endpoint(int count, bool speakers) => new(false, true, ThxEqualizerPreset.Music,
            new(new double[count], new double[count], new double[count], new double[count], new double[count]),
            new(false, 25), speakers ? new(false, 50) : null);
        return new(false, Endpoint(7, true), Endpoint(10, false), []);
    }

    private static ThxEqualizerCurves UniformCurves(int count, double gain)
    {
        double[] values = Enumerable.Repeat(gain, count).ToArray();
        return new(values, values, values, values, values);
    }

    private static ThxAudioState SpeakerState(int count)
    {
        int[] frequencies = count == 7
            ? [250, 500, 1000, 2000, 4000, 8000, 16000]
            : [31, 63, 125, 250, 500, 1000, 2000, 4000, 8000, 16000];
        double[] factory = count == 7
            ? [0, 0, 0, 0, 0, 0, 0]
            : [-3, -2, -1, 0, 0, 0, 0, 0, 0, 0];
        return AdvancedState() with
        {
            Presets = [new("music", "Music"), new("game", "Game")],
            EqualizerBands = frequencies.Select(frequency =>
                new ThxAudioEqualizerBand(frequency, 0)).ToArray(),
            FactoryPresetCurves = new[] { "game", "movie", "music", "voice", "custom" }
                .Select(id => new ThxAudioPresetCurve(id, factory.ToArray())).ToArray(),
        };
    }

    [Fact]
    public async Task PowerSourceChangeWhileCaptureWaitsDiscardsItsProfileResult()
    {
        var pending = new TaskCompletionSource<SessionAudioResponse>(TaskCreationOptions.RunContinuationsAsynchronously);
        var adapter = new FakeAdapter
        {
            AsyncReadHandler = endpoint => endpoint == ThxAudioEndpoint.Speakers
                ? pending.Task : Task.FromResult(Response(ThxAudioOperationStatus.Succeeded, AdvancedState(endpoint))),
        };
        await using var viewModel = new ThxAudioViewModel(adapter);
        viewModel.ApplyPreference(true);
        viewModel.LoadProfile(PowerSource.PluggedIn, null);
        Task capture = viewModel.CaptureAudioProfileAsync();
        Assert.True(viewModel.IsBusy);
        viewModel.LoadProfile(PowerSource.Battery, null);
        pending.SetResult(Response(ThxAudioOperationStatus.Succeeded, AdvancedState()));
        await capture;
        Assert.Null(viewModel.CreateProfileDraft());
        Assert.False(viewModel.IsBusy);
        Assert.Equal(["read:Speakers", "read:Headphones"], adapter.Calls);
    }

    [Fact]
    public async Task EnhancementGestureCaptureStagesWithoutWritingAndRejectsInvalidOrDisabledInput()
    {
        ThxAudioState initial = EnabledEnhancements();
        var adapter = new FakeAdapter { ReadResponse = Response(ThxAudioOperationStatus.Succeeded, initial) };
        await using var viewModel = new ThxAudioViewModel(adapter);
        viewModel.ApplyPreference(true);
        await viewModel.RefreshAsync();
        viewModel.VoiceClarityLevel = 70;
        Assert.Equal(2, adapter.Calls.Count);
        ThxEnhancementCommitRequest request = Assert.IsType<ThxEnhancementCommitRequest>(
            viewModel.CaptureEnhancementCommit(ThxAudioEnhancementKind.VoiceClarity, 75));
        Assert.Equal(ThxAudioEndpoint.Speakers, request.Endpoint);
        Assert.Equal(75, request.LevelPercent);
        Assert.Equal(75, viewModel.VoiceClarityLevel);
        Assert.Equal(2, adapter.Calls.Count);
        Assert.Null(viewModel.CaptureEnhancementCommit(ThxAudioEnhancementKind.VoiceClarity, double.NaN));
        Assert.Null(viewModel.CaptureEnhancementCommit(ThxAudioEnhancementKind.VoiceClarity, 101));
        Assert.Null(viewModel.CaptureEnhancementCommit((ThxAudioEnhancementKind)99, 50));
        adapter.MutationResponse = Response(ThxAudioOperationStatus.Succeeded,
            initial with { VoiceClarity = new(false, 25) });
        await viewModel.SetVoiceClarityAsync(false);
        Assert.Null(viewModel.CaptureEnhancementCommit(ThxAudioEnhancementKind.VoiceClarity, 75));
        await viewModel.CommitEnhancementAsync(request);
        Assert.Equal(25, viewModel.VoiceClarityLevel);
        Assert.Equal(3, adapter.Calls.Count);
    }

    [Fact]
    public async Task EnhancementGestureCannotCrossEndpointOrIntegrationContext()
    {
        var adapter = new FakeAdapter
        {
            ReadHandler = output => Response(ThxAudioOperationStatus.Succeeded, EnabledEnhancements(output)),
        };
        await using var viewModel = new ThxAudioViewModel(adapter);
        viewModel.ApplyPreference(true);
        await viewModel.RefreshAsync();
        ThxEnhancementCommitRequest oldEndpoint = viewModel.CaptureEnhancementCommit(ThxAudioEnhancementKind.VoiceClarity, 75)!;
        viewModel.SelectEndpoint(ThxAudioEndpoint.Headphones);
        await viewModel.RefreshAsync();
        viewModel.VoiceClarityLevel = 75;
        await viewModel.CommitEnhancementAsync(oldEndpoint);
        ThxEnhancementCommitRequest oldPreference = viewModel.CaptureEnhancementCommit(ThxAudioEnhancementKind.VoiceClarity, 75)!;
        viewModel.ApplyPreference(false);
        viewModel.ApplyPreference(true);
        await viewModel.RefreshAsync();
        viewModel.VoiceClarityLevel = 75;
        await viewModel.CommitEnhancementAsync(oldPreference);
        Assert.DoesNotContain(adapter.Calls, call => call.StartsWith("enhancement:", StringComparison.Ordinal));
    }

    [Fact]
    public async Task EnhancementGestureProfileRevisionIgnoresEquivalentRefreshButRejectsReplacement()
    {
        var adapter = new FakeAdapter { ReadResponse = Response(ThxAudioOperationStatus.Succeeded, EnabledEnhancements()) };
        await using var viewModel = new ThxAudioViewModel(adapter);
        viewModel.LoadProfile(PowerSource.PluggedIn, ProfileSettings());
        viewModel.ApplyPreference(true);
        await viewModel.RefreshAsync();
        ThxEnhancementCommitRequest request = viewModel.CaptureEnhancementCommit(ThxAudioEnhancementKind.VoiceClarity, 75)!;
        viewModel.LoadProfile(PowerSource.PluggedIn, ProfileSettings());
        Assert.Equal(75, viewModel.VoiceClarityLevel);
        adapter.MutationResponse = Response(ThxAudioOperationStatus.Succeeded,
            EnabledEnhancements() with { VoiceClarity = new(true, 75) });
        await viewModel.CommitEnhancementAsync(request);
        Assert.Equal("enhancement:VoiceClarity:True:75", adapter.Calls[^1]);
        request = viewModel.CaptureEnhancementCommit(ThxAudioEnhancementKind.VoiceClarity, 80)!;
        viewModel.LoadProfile(PowerSource.Battery, ProfileSettings());
        Assert.Equal(75, viewModel.VoiceClarityLevel);
        await viewModel.CommitEnhancementAsync(request);
        Assert.Equal(3, adapter.Calls.Count);
    }

    [Fact]
    public async Task SupersededEnhancementGestureDoesNotOverwriteNewerDraft()
    {
        var adapter = new FakeAdapter { ReadResponse = Response(ThxAudioOperationStatus.Succeeded, EnabledEnhancements()) };
        await using var viewModel = new ThxAudioViewModel(adapter);
        viewModel.ApplyPreference(true);
        await viewModel.RefreshAsync();
        ThxEnhancementCommitRequest old = viewModel.CaptureEnhancementCommit(ThxAudioEnhancementKind.VoiceClarity, 75)!;
        ThxEnhancementCommitRequest latest = viewModel.CaptureEnhancementCommit(ThxAudioEnhancementKind.VoiceClarity, 60)!;
        await viewModel.CommitEnhancementAsync(old);
        Assert.Equal(60, viewModel.VoiceClarityLevel);
        Assert.Equal(2, adapter.Calls.Count);
        adapter.MutationResponse = Response(ThxAudioOperationStatus.Succeeded,
            EnabledEnhancements() with { VoiceClarity = new(true, 60) });
        await viewModel.CommitEnhancementAsync(latest);
        Assert.Equal("enhancement:VoiceClarity:True:60", adapter.Calls[^1]);
    }

    [Fact]
    public async Task OtherEnhancementCommitWaitsOnceAndPreservesExplicitGesture()
    {
        var pending = new TaskCompletionSource<SessionAudioResponse>();
        ThxAudioState initial = EnabledEnhancements();
        var adapter = new FakeAdapter
        {
            ReadResponse = Response(ThxAudioOperationStatus.Succeeded, initial),
            AsyncMutationHandler = call => call.Contains("SoundNormalization", StringComparison.Ordinal)
                ? pending.Task
                : Task.FromResult(Response(ThxAudioOperationStatus.Succeeded,
                    initial with { SoundNormalization = new(true, 60), VoiceClarity = new(true, 75) })),
        };
        await using var viewModel = new ThxAudioViewModel(adapter);
        viewModel.ApplyPreference(true);
        await viewModel.RefreshAsync();
        Task normalization = viewModel.CommitEnhancementAsync(
            viewModel.CaptureEnhancementCommit(ThxAudioEnhancementKind.SoundNormalization, 60)!);
        ThxEnhancementCommitRequest clarityRequest = Assert.IsType<ThxEnhancementCommitRequest>(
            viewModel.CaptureEnhancementCommit(ThxAudioEnhancementKind.VoiceClarity, 75));
        Task clarity = viewModel.CommitEnhancementAsync(clarityRequest);
        Assert.False(clarity.IsCompleted);
        Assert.Equal(3, adapter.Calls.Count);
        pending.SetResult(Response(ThxAudioOperationStatus.Succeeded, initial with { SoundNormalization = new(true, 60) }));
        await Task.WhenAll(normalization, clarity);
        Assert.Equal("enhancement:VoiceClarity:True:75", adapter.Calls[^1]);
        Assert.Equal(4, adapter.Calls.Count);
        Assert.Equal(75, viewModel.VoiceClarityLevel);
    }

    [Fact]
    public async Task AmbiguousOtherEnhancementInvalidatesQueuedGestureEvenAfterRead()
    {
        var pending = new TaskCompletionSource<SessionAudioResponse>();
        var adapter = new FakeAdapter
        {
            ReadResponse = Response(ThxAudioOperationStatus.Succeeded, EnabledEnhancements()),
            AsyncMutationHandler = _ => pending.Task,
        };
        await using var viewModel = new ThxAudioViewModel(adapter);
        viewModel.ApplyPreference(true);
        await viewModel.RefreshAsync();
        Task normalization = viewModel.CommitEnhancementAsync(
            viewModel.CaptureEnhancementCommit(ThxAudioEnhancementKind.SoundNormalization, 60)!);
        ThxEnhancementCommitRequest request = viewModel.CaptureEnhancementCommit(ThxAudioEnhancementKind.VoiceClarity, 75)!;
        Task clarity = viewModel.CommitEnhancementAsync(request);
        pending.SetResult(Response(ThxAudioOperationStatus.Indeterminate));
        await Task.WhenAll(normalization, clarity);
        Assert.False(viewModel.CanEdit);
        Assert.Equal(3, adapter.Calls.Count);
        await viewModel.RefreshAsync();
        Assert.Equal(25, viewModel.VoiceClarityLevel);
        await viewModel.CommitEnhancementAsync(request);
        Assert.Equal(5, adapter.Calls.Count);
    }

    [Fact]
    public async Task UnrelatedBusyOperationRejectsGestureAndRestoresOnlyItsUnchangedDraft()
    {
        var pending = new TaskCompletionSource<SessionAudioResponse>();
        var adapter = new FakeAdapter
        {
            ReadResponse = Response(ThxAudioOperationStatus.Succeeded, EnabledEnhancements()),
            AsyncMutationHandler = _ => pending.Task,
        };
        await using var viewModel = new ThxAudioViewModel(adapter);
        viewModel.ApplyPreference(true);
        await viewModel.RefreshAsync();
        ThxEnhancementCommitRequest request = viewModel.CaptureEnhancementCommit(ThxAudioEnhancementKind.VoiceClarity, 75)!;
        Task spatial = viewModel.SetSpatialAsync(true);
        await viewModel.CommitEnhancementAsync(request);
        Assert.Equal(25, viewModel.VoiceClarityLevel);
        Assert.Equal(3, adapter.Calls.Count);
        pending.SetResult(Response(ThxAudioOperationStatus.Succeeded, EnabledEnhancements() with { SpatialEnabled = true }));
        await spatial;
    }

    private static ThxAudioState EnabledEnhancements(ThxAudioEndpoint output = ThxAudioEndpoint.Speakers) =>
        AdvancedState(output) with
        {
            SoundNormalization = output == ThxAudioEndpoint.Speakers ? new(true, 50) : null,
            VoiceClarity = new(true, 25),
        };
    private static ThxAudioState AdvancedState(ThxAudioEndpoint endpoint = ThxAudioEndpoint.Speakers) => State(endpoint) with
    {
        Presets = [new("music", "Music"), new("custom", "Custom")],
        EqualizerBands = (endpoint == ThxAudioEndpoint.Speakers
            ? new[] { 250, 500, 1000, 2000, 4000, 8000, 16000 }
            : new[] { 31, 63, 125, 250, 500, 1000, 2000, 4000, 8000, 16000 })
            .Select(frequency => new ThxAudioEqualizerBand(frequency, 0)).ToArray(),
        SoundNormalization = endpoint == ThxAudioEndpoint.Speakers ? new(false, 50) : null,
        VoiceClarity = new(false, 25),
    };

    private static ThxAudioState State(ThxAudioEndpoint endpoint = ThxAudioEndpoint.Speakers) =>
        new(endpoint, false, true, "music", [new("music", "Music"), new("game", "Game")])
        {
            FactoryPresetCurves = new[] { "game", "movie", "music", "voice", "custom" }
                .Select(id => new ThxAudioPresetCurve(id, new double[endpoint == ThxAudioEndpoint.Speakers ? 7 : 10])).ToArray(),
        };

    private static SessionAudioResponse Response(ThxAudioOperationStatus status, ThxAudioState? state = null) =>
        new(SessionAudioIpc.ProtocolVersion, Guid.NewGuid(), status,
            ThxAudioAvailability.Available, null, state);

    private sealed class FakeAdapter : IThxAudioAdapter
    {
        public List<string> Calls { get; } = [];
        public List<(string Id, double[] Gains)> PresetRequests { get; } = [];
        public SessionAudioResponse DiscoveryResponse { get; init; } = Response(ThxAudioOperationStatus.Succeeded);
        public SessionAudioResponse ReadResponse { get; init; } = Response(ThxAudioOperationStatus.Succeeded, State());
        public Func<ThxAudioEndpoint, SessionAudioResponse>? ReadHandler { get; set; }
        public Func<ThxAudioEndpoint, Task<SessionAudioResponse>>? AsyncReadHandler { get; init; }
        public SessionAudioResponse MutationResponse { get; set; } = Response(ThxAudioOperationStatus.Succeeded, State());
        public bool ThrowOnMutation { get; init; }
        public Func<string, Task<SessionAudioResponse>>? AsyncMutationHandler { get; set; }

        public Task<SessionAudioResponse> DiscoverAsync(CancellationToken cancellationToken)
        {
            Calls.Add("discover");
            return Task.FromResult(DiscoveryResponse);
        }
        public Task<SessionAudioResponse> ReadAsync(ThxAudioEndpoint endpoint, CancellationToken cancellationToken)
        {
            Calls.Add("read:" + endpoint);
            return AsyncReadHandler?.Invoke(endpoint) ?? Task.FromResult(ReadHandler?.Invoke(endpoint) ?? ReadResponse);
        }
        public Task<SessionAudioResponse> SetSpatialAsync(ThxAudioEndpoint endpoint, bool enabled, CancellationToken cancellationToken) =>
            Mutate("spatial:" + enabled);
        public Task<SessionAudioResponse> SetEqualizerAsync(ThxAudioEndpoint endpoint, bool enabled, CancellationToken cancellationToken) =>
            Mutate("equalizer:" + enabled);
        public Task<SessionAudioResponse> SelectPresetAsync(ThxAudioEndpoint endpoint, string presetId, IReadOnlyList<double> gainsDb, CancellationToken cancellationToken)
        {
            PresetRequests.Add((presetId, gainsDb.ToArray()));
            return Mutate("preset:" + presetId);
        }
        public Task<SessionAudioResponse> SetEnhancementAsync(ThxAudioEndpoint endpoint, ThxAudioEnhancementKind kind, bool enabled, int? levelPercent, CancellationToken cancellationToken) =>
            Mutate($"enhancement:{kind}:{enabled}:{levelPercent}");
        private Task<SessionAudioResponse> Mutate(string operation)
        {
            Calls.Add(operation);
            return ThrowOnMutation
                ? Task.FromException<SessionAudioResponse>(new IOException("Disconnected"))
                : AsyncMutationHandler?.Invoke(operation) ?? Task.FromResult(MutationResponse);
        }
    }
}
