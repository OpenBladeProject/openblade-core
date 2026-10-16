using System.Collections.ObjectModel;
using OpenBlade.Contracts;
using OpenBlade.Session;

namespace OpenBlade.Tray;

public sealed record ThxApplicationOutputOption(ThxApplicationAudioOutput Output, string Label);
public sealed record ThxApplicationRuleOption(string ExecutableName, ThxApplicationAudioOutput Output, string Label);

public sealed partial class ThxAudioViewModel
{
    private ThxAudioProfileSettings? profileDraft;
    private ThxAudioProfileSettings? loadedProfile;
    private PowerSource profileSource;
    private long profileRevision;
    private bool applicationRoutingEnabled;
    private string applicationExecutable = string.Empty;
    private ThxApplicationOutputOption? applicationOutput;
    private string profileMessageKey = "Choose an EQ preset for both outputs, then capture the current audio settings.";
    private bool profilePersistenceAvailable;

    public ObservableCollection<ThxApplicationRuleOption> ApplicationRules { get; } = [];
    public bool CanCaptureAudioProfile => CanRead && availability != ThxAudioAvailability.AvailableReadOnly;
    public bool HasAudioProfile => profileDraft is not null;
    public bool CanSaveAudioProfile => HasAudioProfile && profilePersistenceAvailable && !busy && !disposed
        && availability != ThxAudioAvailability.AvailableReadOnly;
    public bool ProfilePersistenceAvailable
    {
        get => profilePersistenceAvailable;
        set
        {
            if (profilePersistenceAvailable != value)
            {
                profilePersistenceAvailable = value;
                NotifyProfileEditor();
            }
        }
    }
    public bool CanConfigureApplicationRouting => state?.ApplicationProfiles != ThxAudioFeatureAccess.ObservedOnly;
    public bool ApplicationRoutingUnavailable => !CanConfigureApplicationRouting;
    public string ApplicationRoutingUnavailableHint => TrayText.Get("Application routing is not yet enabled for this model.");
    public string AudioProfileMessage => TrayText.Get(state?.ApplicationAutomationPaused == true
        ? "Audio automation is paused. Read the current THX settings to resume."
        : profileMessageKey);
    public bool ApplicationRoutingEnabled
    {
        get => applicationRoutingEnabled;
        set
        {
            if (applicationRoutingEnabled != value && (!value || CanConfigureApplicationRouting))
            {
                applicationRoutingEnabled = value;
                NotifyProfileEditor();
            }
        }
    }
    public string ApplicationExecutable
    {
        get => applicationExecutable;
        set
        {
            if (applicationExecutable == value)
            {
                return;
            }
            applicationExecutable = value;
            applicationOutput = ApplicationOutputs.FirstOrDefault();
            NotifyProfileEditor();
        }
    }
    public ThxApplicationOutputOption? ApplicationOutput
    {
        get => applicationOutput;
        set
        {
            if (applicationOutput != value)
            {
                applicationOutput = value;
                NotifyProfileEditor();
            }
        }
    }
    public IReadOnlyList<ThxApplicationOutputOption> ApplicationOutputs
    {
        get
        {
            string executable = applicationExecutable.Trim();
            ThxAudioApplicationProfile? supported = state?.ApplicationProfileCatalog.FirstOrDefault(item =>
                string.Equals(item.ExecutableName, executable, StringComparison.OrdinalIgnoreCase));
            return Enum.GetValues<ThxApplicationAudioOutput>()
                .Where(output => output switch
                {
                    ThxApplicationAudioOutput.Immersive => supported?.SupportsImmersive == true,
                    ThxApplicationAudioOutput.Competitive => supported?.SupportsCompetitive == true,
                    _ => true,
                })
                .Select(output => new ThxApplicationOutputOption(output, ApplicationOutputLabel(output))).ToArray();
        }
    }
    public bool CanAddApplicationRule => !disposed && CanConfigureApplicationRouting && ApplicationOutput is { } selected
        && ApplicationOutputs.Contains(selected)
        && ApplicationRules.Count < ThxAudioProfileSettings.MaximumApplicationRules
        && IsValidExecutable(applicationExecutable)
        && !ApplicationRules.Any(rule => string.Equals(rule.ExecutableName,
            applicationExecutable.Trim(), StringComparison.OrdinalIgnoreCase));

    public void LoadProfile(PowerSource source, ThxAudioProfileSettings? settings)
    {
        if (profileRevision > 0 && source == profileSource && ProfilesEqual(settings, loadedProfile))
        {
            return;
        }
        enhancementContextRevision++;
        normalizationLevel = state?.SoundNormalization?.LevelPercent ?? 0;
        voiceClarityLevel = state?.VoiceClarity?.LevelPercent ?? 0;
        profileSource = source;
        profileRevision++;
        profileDraft = settings;
        loadedProfile = settings;
        profileMessageKey = settings is null
            ? "Choose an EQ preset for both outputs, then capture the current audio settings."
            : "Saved audio settings are available for this power profile.";
        applicationRoutingEnabled = settings?.ApplicationRoutingEnabled ?? false;
        ApplicationRules.Clear();
        foreach (ThxApplicationAudioRule rule in settings?.ApplicationRules ?? [])
        {
            ApplicationRules.Add(new(rule.ExecutableName, rule.Output, ApplicationOutputLabel(rule.Output)));
        }
        presetCurves.Clear();
        if (settings is not null)
        {
            LoadCurves(ThxAudioEndpoint.Speakers, settings.Speakers.EqualizerCurves);
            LoadCurves(ThxAudioEndpoint.Headphones, settings.Headphones.EqualizerCurves);
        }
        NotifyAll();
    }

    public Task CaptureAudioProfileAsync()
    {
        if (!CanCaptureAudioProfile)
        {
            return Task.CompletedTask;
        }
        long revision = profileRevision;
        return StartAsync(async token =>
        {
            var responses = new Dictionary<ThxAudioEndpoint, SessionAudioResponse>();
            foreach (ThxAudioEndpoint output in Enum.GetValues<ThxAudioEndpoint>())
            {
                SessionAudioResponse response = await adapter.ReadAsync(output, token);
                if (response.Status != ThxAudioOperationStatus.Succeeded
                    || response.Availability != ThxAudioAvailability.Available
                    || response.State?.Endpoint != output)
                {
                    return response with { State = null };
                }
                responses.Add(output, response);
            }
            if (revision == profileRevision)
            {
                ThxAudioEndpointSettings? speakers = CaptureEndpoint(responses[ThxAudioEndpoint.Speakers].State!);
                ThxAudioEndpointSettings? headphones = CaptureEndpoint(responses[ThxAudioEndpoint.Headphones].State!);
                if (speakers is not null && headphones is not null)
                {
                    profileDraft = new(applicationRoutingEnabled, speakers, headphones, []);
                    profileMessageKey = "Current audio settings captured. Save the profile to keep them.";
                }
                else
                {
                    profileMessageKey = "Choose an EQ preset for both outputs before capturing audio settings.";
                }
            }
            return responses[endpoint];
        }, isMutation: false);
    }

    private void UpdateConfirmedProfileDraft(ThxAudioState confirmed)
    {
        if (profileDraft is null)
        {
            return;
        }
        ThxAudioEndpointSettings? updated = CaptureEndpoint(confirmed);
        if (updated is null)
        {
            profileDraft = null;
            profileMessageKey = "Choose an EQ preset for both outputs before capturing audio settings.";
            return;
        }
        profileDraft = confirmed.Endpoint == ThxAudioEndpoint.Speakers
            ? profileDraft with { Speakers = updated }
            : profileDraft with { Headphones = updated };
    }

    public ThxAudioProfileSettings? CreateProfileDraft() => profileDraft is null ? null : profileDraft with
    {
        ApplicationRoutingEnabled = applicationRoutingEnabled,
        ApplicationRules = ApplicationRules.Select(rule => new ThxApplicationAudioRule(rule.ExecutableName, rule.Output)).ToArray(),
    };

    public void AddApplicationRule()
    {
        if (!CanAddApplicationRule)
        {
            return;
        }
        ThxApplicationOutputOption selected = ApplicationOutput!;
        ApplicationRules.Add(new(ThxApplicationAudioRule.NormalizeExecutableName(applicationExecutable),
            selected.Output, selected.Label));
        applicationExecutable = string.Empty;
        applicationOutput = ApplicationOutputs.FirstOrDefault();
        NotifyProfileEditor();
    }

    public void RemoveApplicationRule(ThxApplicationRuleOption rule)
    {
        if (!disposed)
        {
            ApplicationRules.Remove(rule);
            NotifyProfileEditor();
        }
    }

    private ThxAudioEndpointSettings? CaptureEndpoint(ThxAudioState confirmed)
    {
        if (!Enum.TryParse(confirmed.SelectedPresetId, ignoreCase: true, out ThxEqualizerPreset selected)
            || !Enum.IsDefined(selected) || confirmed.VoiceClarity is null)
        {
            return null;
        }
        if (!presetCurves.TryGetValue(confirmed.Endpoint, out Dictionary<string, double[]>? curves))
        {
            curves = confirmed.FactoryPresetCurves.ToDictionary(curve => curve.PresetId,
                curve => curve.GainsDb.ToArray(), StringComparer.Ordinal);
            presetCurves[confirmed.Endpoint] = curves;
        }
        curves[confirmed.SelectedPresetId!] = confirmed.EqualizerBands.Select(band => band.GainDb).ToArray();
        string[] ids = ["game", "movie", "music", "voice", "custom"];
        if (ids.Any(id => !curves.ContainsKey(id)))
        {
            return null;
        }
        return new(confirmed.SpatialEnabled, confirmed.EqualizerEnabled, selected,
            new(curves["game"].ToArray(), curves["movie"].ToArray(), curves["music"].ToArray(),
                curves["voice"].ToArray(), curves["custom"].ToArray()),
            new(confirmed.VoiceClarity.Enabled, confirmed.VoiceClarity.LevelPercent),
            confirmed.SoundNormalization is { } normalization ? new(normalization.Enabled, normalization.LevelPercent) : null);
    }

    private void LoadCurves(ThxAudioEndpoint output, ThxEqualizerCurves curves) => presetCurves[output] = new(StringComparer.Ordinal)
    {
        ["game"] = curves.Game.ToArray(),
        ["movie"] = curves.Movie.ToArray(),
        ["music"] = curves.Music.ToArray(),
        ["voice"] = curves.Voice.ToArray(),
        ["custom"] = curves.Custom.ToArray(),
    };

    private static bool IsValidExecutable(string value)
    {
        try
        {
            _ = ThxApplicationAudioRule.NormalizeExecutableName(value);
            return true;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    private static string ApplicationOutputLabel(ThxApplicationAudioOutput output) => TrayText.Get(output switch
    {
        ThxApplicationAudioOutput.Immersive => "THX Immersive Mode",
        ThxApplicationAudioOutput.Competitive => "THX Competitive Mode",
        _ => output.ToString(),
    });

    private static bool ProfilesEqual(ThxAudioProfileSettings? left, ThxAudioProfileSettings? right) =>
        ReferenceEquals(left, right) || left is not null && right is not null
        && left.ApplicationRoutingEnabled == right.ApplicationRoutingEnabled
        && left.ApplicationRules.SequenceEqual(right.ApplicationRules)
        && EndpointsEqual(left.Speakers, right.Speakers) && EndpointsEqual(left.Headphones, right.Headphones);

    private static bool EndpointsEqual(ThxAudioEndpointSettings left, ThxAudioEndpointSettings right) =>
        left.SpatialEnabled == right.SpatialEnabled && left.EqualizerEnabled == right.EqualizerEnabled
        && left.SelectedPreset == right.SelectedPreset && left.VoiceClarity == right.VoiceClarity
        && left.SoundNormalization == right.SoundNormalization
        && left.EqualizerCurves.Game.SequenceEqual(right.EqualizerCurves.Game)
        && left.EqualizerCurves.Movie.SequenceEqual(right.EqualizerCurves.Movie)
        && left.EqualizerCurves.Music.SequenceEqual(right.EqualizerCurves.Music)
        && left.EqualizerCurves.Voice.SequenceEqual(right.EqualizerCurves.Voice)
        && left.EqualizerCurves.Custom.SequenceEqual(right.EqualizerCurves.Custom);

    private void NotifyProfileEditor()
    {
        foreach (string property in new[] { nameof(CanCaptureAudioProfile), nameof(HasAudioProfile), nameof(CanSaveAudioProfile),
            nameof(ApplicationRoutingEnabled), nameof(ApplicationExecutable), nameof(ApplicationOutput),
            nameof(ApplicationOutputs), nameof(CanAddApplicationRule), nameof(AudioProfileMessage), nameof(CanConfigureApplicationRouting), nameof(ApplicationRoutingUnavailable), nameof(ApplicationRoutingUnavailableHint) })
        {
            PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(property));
        }
    }
}
