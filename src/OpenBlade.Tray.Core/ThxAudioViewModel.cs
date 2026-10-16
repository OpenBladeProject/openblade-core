using System.ComponentModel;
using System.Globalization;
using OpenBlade.Session;

namespace OpenBlade.Tray;

public sealed record ThxAudioEndpointOption(ThxAudioEndpoint Endpoint, string Label);

public sealed record ThxEnhancementCommitRequest(
    ThxAudioEndpoint Endpoint,
    ThxAudioEnhancementKind Kind,
    int LevelPercent,
    long ContextRevision);

public interface IThxAudioAdapter
{
    Task<SessionAudioResponse> DiscoverAsync(CancellationToken cancellationToken);
    Task<SessionAudioResponse> ConnectAsync(ThxAudioEndpoint endpoint, CancellationToken cancellationToken);
    Task<SessionAudioResponse> ReadAsync(ThxAudioEndpoint endpoint, CancellationToken cancellationToken);
    Task<SessionAudioResponse> SetSpatialAsync(ThxAudioEndpoint endpoint, bool enabled, CancellationToken cancellationToken);
    Task<SessionAudioResponse> SetEqualizerAsync(ThxAudioEndpoint endpoint, bool enabled, CancellationToken cancellationToken);
    Task<SessionAudioResponse> SelectPresetAsync(ThxAudioEndpoint endpoint, string presetId, IReadOnlyList<double> gainsDb, CancellationToken cancellationToken);
    Task<SessionAudioResponse> SetEnhancementAsync(ThxAudioEndpoint endpoint, ThxAudioEnhancementKind kind, bool enabled, int? levelPercent, CancellationToken cancellationToken);
}

/// <summary>
/// User-requested audio operations only. Restoring the preference, discovering
/// components, switching endpoints, and disposing never change audio settings.
/// </summary>
public sealed partial class ThxAudioViewModel(IThxAudioAdapter adapter) : INotifyPropertyChanged, IAsyncDisposable
{
    private readonly IThxAudioAdapter adapter = adapter ?? throw new ArgumentNullException(nameof(adapter));
    private readonly CancellationTokenSource lifetime = new();
    private ThxAudioState? state;
    private ThxAudioEndpoint endpoint;
    private ThxAudioAvailability availability = ThxAudioAvailability.Disabled;
    private string statusKey = "THX audio integration is off.";
    private bool enabled;
    private bool busy;
    private bool disposed;
    private bool automaticConnectionAttempted;
    private Task pendingOperation = Task.CompletedTask;
    private ThxAudioEnhancementKind? pendingEnhancementKind;
    private long enhancementContextRevision;
    private IReadOnlyList<ThxEqualizerBandViewModel> equalizerBands = [];
    private double normalizationLevel;
    private double voiceClarityLevel;
    private int? confirmedNormalizationLevel;
    private int? confirmedVoiceClarityLevel;
    private readonly Dictionary<ThxAudioEndpoint, Dictionary<string, double[]>> presetCurves = [];
    private readonly Dictionary<ThxAudioEndpoint, Dictionary<string, double[]>> factoryPresetCurves = [];
    private string? equalizerDraftPresetId;
    private string? equalizerDraftNativeId;

    public event PropertyChangedEventHandler? PropertyChanged;

    public bool IsEnabled => enabled;
    public bool IsBusy => busy;
    public bool CanToggleIntegration => !busy && !disposed;
    public bool CanRead => enabled && !busy && !disposed;
    public bool CanEdit => CanRead && state is not null && availability == ThxAudioAvailability.Available;
    public bool SpatialEnabled => state?.SpatialEnabled ?? false;
    public bool EqualizerEnabled => state?.EqualizerEnabled ?? false;
    public ThxAudioEndpoint Endpoint => endpoint;
    public IReadOnlyList<ThxAudioEndpointOption> Endpoints { get; } =
    [
        new(ThxAudioEndpoint.Speakers, TrayText.Get("Speakers")),
        new(ThxAudioEndpoint.Headphones, TrayText.Get("Headphones")),
    ];
    public ThxAudioEndpointOption SelectedEndpoint => Endpoints.Single(option => option.Endpoint == endpoint);
    public IReadOnlyList<ThxAudioPreset> Presets => state?.Presets.Select(preset =>
        preset with { Name = TrayText.Get(preset.Name) }).ToArray() ?? [];
    public ThxAudioPreset? SelectedPreset => Presets.FirstOrDefault(preset => preset.Id == state?.SelectedPresetId);
    public string StatusMessage => TrayText.Get(statusKey);
    public IReadOnlyList<ThxEqualizerBandViewModel> EqualizerBands => equalizerBands;
    public bool HasEqualizerBands => equalizerBands.Count > 0;
    public bool CanEditEqualizerBands => CanEdit && HasEqualizerBands && HasEditableEqualizerPreset;
    private bool HasEditableEqualizerPreset => state?.SelectedPresetId is "game" or "movie" or "music" or "voice" or "custom";
    public string EqualizerDraftHint => TrayText.Get(HasEditableEqualizerPreset
        ? "Adjust the selected preset, then apply your EQ changes."
        : "Choose Game, Movie, Music, Voice, or Custom to edit EQ.");
    public bool CanResetEqualizer => CanEditEqualizerBands
        && factoryPresetCurves.TryGetValue(endpoint, out Dictionary<string, double[]>? curves)
        && curves.TryGetValue(state!.SelectedPresetId!, out double[]? gains)
        && gains.Length == equalizerBands.Count;
    public bool HasEqualizerChanges => equalizerBands.Any(band => band.HasChanges);
    public bool CanApplyEqualizer => CanEditEqualizerBands && HasEqualizerChanges;
    public bool CanDiscardEqualizerChanges => CanEditEqualizerBands && HasEqualizerChanges;
    public bool HasSoundNormalization => state?.SoundNormalization is not null;
    public bool HasVoiceClarity => state?.VoiceClarity is not null;
    public bool CanEditSoundNormalization => CanEdit && HasSoundNormalization;
    public bool CanEditVoiceClarity => CanEdit && HasVoiceClarity;
    public bool CanEditSoundNormalizationLevel => CanEditSoundNormalization && SoundNormalizationEnabled;
    public bool CanEditVoiceClarityLevel => CanEditVoiceClarity && VoiceClarityEnabled;
    public string SoundNormalizationLevelLabel => TrayText.Format("{0}%", normalizationLevel);
    public string VoiceClarityLevelLabel => TrayText.Format("{0}%", voiceClarityLevel);
    public bool SoundNormalizationEnabled => state?.SoundNormalization?.Enabled ?? false;
    public bool VoiceClarityEnabled => state?.VoiceClarity?.Enabled ?? false;
    public bool CanApplySoundNormalization => CanEditSoundNormalizationLevel
        && normalizationLevel != state!.SoundNormalization!.LevelPercent;
    public bool CanApplyVoiceClarity => CanEditVoiceClarityLevel
        && voiceClarityLevel != state!.VoiceClarity!.LevelPercent;
    public string ActiveProfileMessage => state is { SelectedPresetId: null, SelectedNativeProfileId: { } id }
        ? string.Format(CultureInfo.CurrentCulture, TrayText.Get("Active application profile: {0}"), id)
        : string.Empty;
    public bool HasActiveApplicationProfile => !string.IsNullOrEmpty(ActiveProfileMessage);

    public double SoundNormalizationLevel
    {
        get => normalizationLevel;
        set
        {
            if (CanEditSoundNormalization && double.IsFinite(value) && value is >= 0 and <= 100)
            {
                normalizationLevel = Math.Round(value);
                NotifyAll();
            }
        }
    }

    public double VoiceClarityLevel
    {
        get => voiceClarityLevel;
        set
        {
            if (CanEditVoiceClarity && double.IsFinite(value) && value is >= 0 and <= 100)
            {
                voiceClarityLevel = Math.Round(value);
                NotifyAll();
            }
        }
    }

    public void ApplyPreference(bool value)
    {
        if (disposed || busy)
        {
            return;
        }
        enabled = value;
        state = null;
        automaticConnectionAttempted = false;
        ResetAdvancedDrafts();
        availability = value ? ThxAudioAvailability.ComponentsUnavailable : ThxAudioAvailability.Disabled;
        statusKey = value ? "Read the current THX settings to connect." : "THX audio integration is off.";
        NotifyAll();
    }

    public Task SetEnabledAsync(bool value)
    {
        if (!CanToggleIntegration || enabled == value)
        {
            return Task.CompletedTask;
        }
        ApplyPreference(value);
        return value ? ConnectIfNeededAsync() : Task.CompletedTask;
    }

    public Task ConnectIfNeededAsync()
    {
        if (!CanRead || state is not null || automaticConnectionAttempted)
        {
            return Task.CompletedTask;
        }
        automaticConnectionAttempted = true;
        return StartAsync(token => adapter.ConnectAsync(endpoint, token), isMutation: false);
    }

    public void SelectEndpoint(ThxAudioEndpoint value)
    {
        if (!CanRead || !Enum.IsDefined(value) || endpoint == value)
        {
            return;
        }
        endpoint = value;
        state = null;
        automaticConnectionAttempted = false;
        ResetAdvancedDrafts();
        statusKey = "Read the current THX settings to connect.";
        NotifyAll();
    }

    public Task RefreshAsync()
    {
        if (!CanRead)
        {
            return Task.CompletedTask;
        }
        automaticConnectionAttempted = true;
        return StartAsync(token => adapter.ReadAsync(endpoint, token), isMutation: false);
    }

    public Task SetSpatialAsync(bool value) => !CanEdit || SpatialEnabled == value
        ? Task.CompletedTask
        : StartAsync(token => adapter.SetSpatialAsync(endpoint, value, token), isMutation: true);

    public Task SetEqualizerAsync(bool value) => !CanEdit || EqualizerEnabled == value
        ? Task.CompletedTask
        : StartAsync(token => adapter.SetEqualizerAsync(endpoint, value, token), isMutation: true);

    public Task SelectPresetAsync(ThxAudioPreset preset) => !CanEdit
        || !Presets.Contains(preset) || SelectedPreset?.Id == preset.Id
        || !presetCurves.TryGetValue(endpoint, out Dictionary<string, double[]>? curves)
        || !curves.TryGetValue(preset.Id, out double[]? gains)
        ? Task.CompletedTask
        : StartAsync(token => adapter.SelectPresetAsync(endpoint, preset.Id, gains.ToArray(), token), isMutation: true,
            discardEqualizerDraft: true);

    public Task ApplyEqualizerAsync() => !CanApplyEqualizer
        ? Task.CompletedTask
        : StartAsync(token => adapter.SelectPresetAsync(endpoint, state!.SelectedPresetId!,
            equalizerBands.Select(band => band.DraftGainDb).ToArray(), token), isMutation: true,
            discardEqualizerDraft: true);

    public void ResetEqualizerToFactory()
    {
        if (!CanResetEqualizer)
        {
            return;
        }
        double[] gains = factoryPresetCurves[endpoint][state!.SelectedPresetId!];
        for (int index = 0; index < equalizerBands.Count; index++)
        {
            equalizerBands[index].DraftGainDb = gains[index];
        }
    }

    public void FlattenEqualizer()
    {
        if (!CanEditEqualizerBands)
        {
            return;
        }
        foreach (ThxEqualizerBandViewModel band in equalizerBands)
        {
            band.DraftGainDb = 0;
        }
    }

    public void DiscardEqualizerChanges()
    {
        if (!CanEditEqualizerBands)
        {
            return;
        }
        foreach (ThxEqualizerBandViewModel band in equalizerBands)
        {
            band.Discard();
        }
    }

    public Task SetSoundNormalizationAsync(bool value) => !CanEditSoundNormalization
        || SoundNormalizationEnabled == value ? Task.CompletedTask
        : StartAsync(token => adapter.SetEnhancementAsync(endpoint,
            ThxAudioEnhancementKind.SoundNormalization, value,
            null, token), isMutation: true,
            discardEnhancementDraft: value ? null : ThxAudioEnhancementKind.SoundNormalization);

    public Task SetVoiceClarityAsync(bool value) => !CanEditVoiceClarity
        || VoiceClarityEnabled == value ? Task.CompletedTask
        : StartAsync(token => adapter.SetEnhancementAsync(endpoint,
            ThxAudioEnhancementKind.VoiceClarity, value,
            null, token), isMutation: true,
            discardEnhancementDraft: value ? null : ThxAudioEnhancementKind.VoiceClarity);

    public Task ApplySoundNormalizationAsync() => !CanApplySoundNormalization ? Task.CompletedTask
        : StartAsync(token => adapter.SetEnhancementAsync(endpoint,
            ThxAudioEnhancementKind.SoundNormalization, SoundNormalizationEnabled,
            (int)normalizationLevel, token), isMutation: true,
            discardEnhancementDraft: ThxAudioEnhancementKind.SoundNormalization);

    public Task ApplyVoiceClarityAsync() => !CanApplyVoiceClarity ? Task.CompletedTask
        : StartAsync(token => adapter.SetEnhancementAsync(endpoint,
            ThxAudioEnhancementKind.VoiceClarity, VoiceClarityEnabled,
            (int)voiceClarityLevel, token), isMutation: true,
            discardEnhancementDraft: ThxAudioEnhancementKind.VoiceClarity);

    // Called only by a completed pointer/keyboard gesture. Bound setters remain
    // draft-only; a delayed UI callback carries its original endpoint/context.
    public ThxEnhancementCommitRequest? CaptureEnhancementCommit(ThxAudioEnhancementKind kind, double value)
    {
        if (!enabled || disposed || state is null || availability != ThxAudioAvailability.Available
            || !double.IsFinite(value) || value is < 0 or > 100
            || busy && (pendingEnhancementKind is null || pendingEnhancementKind == kind))
        {
            return null;
        }
        ThxAudioEnhancement? confirmed = kind switch
        {
            ThxAudioEnhancementKind.SoundNormalization => state.SoundNormalization,
            ThxAudioEnhancementKind.VoiceClarity => state.VoiceClarity,
            _ => null,
        };
        if (confirmed?.Enabled != true)
        {
            return null;
        }
        int level = (int)Math.Round(value);
        if (kind == ThxAudioEnhancementKind.SoundNormalization)
        {
            normalizationLevel = level;
        }
        else
        {
            voiceClarityLevel = level;
        }
        NotifyAll();
        return level == confirmed.LevelPercent ? null
            : new(endpoint, kind, level, enhancementContextRevision);
    }

    public async Task CommitEnhancementAsync(ThxEnhancementCommitRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.ContextRevision != enhancementContextRevision || request.Endpoint != endpoint || disposed)
        {
            return;
        }
        if (busy)
        {
            if (pendingEnhancementKind is null || pendingEnhancementKind == request.Kind)
            {
                DiscardRejectedEnhancementCommit(request);
                return;
            }
            // One wait, never a retry. The UI coalesces subsequent gestures.
            await pendingOperation;
        }
        if (request.ContextRevision != enhancementContextRevision || request.Endpoint != endpoint || disposed)
        {
            return;
        }
        if (request.Kind == ThxAudioEnhancementKind.SoundNormalization
            && normalizationLevel == request.LevelPercent && CanApplySoundNormalization)
        {
            await ApplySoundNormalizationAsync();
        }
        else if (request.Kind == ThxAudioEnhancementKind.VoiceClarity
            && voiceClarityLevel == request.LevelPercent && CanApplyVoiceClarity)
        {
            await ApplyVoiceClarityAsync();
        }
        else
        {
            DiscardRejectedEnhancementCommit(request);
        }
    }

    private void DiscardRejectedEnhancementCommit(ThxEnhancementCommitRequest request)
    {
        if (disposed || request.Endpoint != endpoint || request.ContextRevision != enhancementContextRevision)
        {
            return;
        }
        if (request.Kind == ThxAudioEnhancementKind.SoundNormalization
            && normalizationLevel == request.LevelPercent && state?.SoundNormalization is { } normalization)
        {
            normalizationLevel = normalization.LevelPercent;
        }
        else if (request.Kind == ThxAudioEnhancementKind.VoiceClarity
            && voiceClarityLevel == request.LevelPercent && state?.VoiceClarity is { } clarity)
        {
            voiceClarityLevel = clarity.LevelPercent;
        }
        else
        {
            return;
        }
        NotifyAll();
    }

    private Task StartAsync(
        Func<CancellationToken, Task<SessionAudioResponse>> action,
        bool isMutation,
        bool isDiscovery = false,
        bool discardEqualizerDraft = false,
        ThxAudioEnhancementKind? discardEnhancementDraft = null)
    {
        if (disposed || busy)
        {
            return Task.CompletedTask;
        }
        long operationProfileRevision = profileRevision;
        busy = true;
        pendingEnhancementKind = discardEnhancementDraft;
        NotifyAll();
        pendingOperation = RunAsync(action, isMutation, isDiscovery,
            discardEqualizerDraft, discardEnhancementDraft, operationProfileRevision);
        return pendingOperation;
    }

    private async Task RunAsync(
        Func<CancellationToken, Task<SessionAudioResponse>> action,
        bool isMutation,
        bool isDiscovery,
        bool discardEqualizerDraft,
        ThxAudioEnhancementKind? discardEnhancementDraft,
        long operationProfileRevision)
    {
        try
        {
            // These awaits deliberately capture the UI context: confirmed
            // state and the finally block both update bound properties.
            SessionAudioResponse response = await action(lifetime.Token);
            if (disposed)
            {
                return;
            }
            availability = response.Availability;
            if (response.Status == ThxAudioOperationStatus.Succeeded
                && response.Availability is ThxAudioAvailability.Available or ThxAudioAvailability.AvailableReadOnly)
            {
                state = response.State is { } confirmed && confirmed.Endpoint == endpoint
                    ? confirmed : null;
                if (state is not null)
                {
                    bool sameProfile = operationProfileRevision == profileRevision;
                    ConfirmAdvancedDrafts(state, discardEqualizerDraft, discardEnhancementDraft, sameProfile);
                    if (isMutation && sameProfile)
                    {
                        UpdateConfirmedProfileDraft(state);
                    }
                }
                statusKey = state?.SystemEffectsDisabled == true
                    ? "Windows audio enhancements are off for this output. Enable Device Default Effects in Windows sound settings to hear THX."
                    : availability == ThxAudioAvailability.AvailableReadOnly
                    ? "THX is available for reading. Controls await validation for this model."
                    : state is not null
                    ? "THX settings are up to date."
                    : isDiscovery ? "THX components found. Read the current settings to connect."
                    : "THX settings could not be confirmed. Read them again before making changes.";
            }
            else
            {
                state = null;
                statusKey = response.Status == ThxAudioOperationStatus.Indeterminate
                    ? "The audio change could not be confirmed. Read the current settings before trying again."
                    : response.Availability == ThxAudioAvailability.ControllerActive
                    ? "Close Synapse and THX Spatial Audio before connecting."
                    : response.Availability == ThxAudioAvailability.UnsupportedModel
                    ? "THX audio is unavailable for this model or audio device."
                    : response.Availability == ThxAudioAvailability.ComponentsUnavailable
                    ? "The supported THX components are unavailable."
                    : "THX settings could not be confirmed. Read them again before making changes.";
            }
        }
        catch (Exception exception) when (exception is IOException or InvalidOperationException
            or OperationCanceledException or UnauthorizedAccessException)
        {
            state = null;
            statusKey = isMutation
                ? "The audio change could not be confirmed. Read the current settings before trying again."
                : "The Session Agent could not read THX settings.";
        }
        finally
        {
            if (state is null)
            {
                enhancementContextRevision++;
                confirmedNormalizationLevel = confirmedVoiceClarityLevel = null;
            }
            pendingEnhancementKind = null;
            busy = false;
            if (!disposed)
            {
                NotifyAll();
            }
        }
    }

    private void NotifyAll()
    {
        NotifyProfileEditor();
        foreach (ThxEqualizerBandViewModel band in equalizerBands)
        {
            band.CanEdit = CanEditEqualizerBands;
        }
        foreach (string property in new[]
        {
            nameof(IsEnabled), nameof(IsBusy), nameof(CanToggleIntegration), nameof(CanRead),
            nameof(CanEdit), nameof(SpatialEnabled), nameof(EqualizerEnabled), nameof(Endpoint),
            nameof(SelectedEndpoint), nameof(Presets), nameof(SelectedPreset), nameof(StatusMessage),
            nameof(EqualizerBands), nameof(HasEqualizerBands), nameof(CanEditEqualizerBands),
            nameof(EqualizerDraftHint), nameof(CanResetEqualizer),
            nameof(HasEqualizerChanges), nameof(CanApplyEqualizer), nameof(CanDiscardEqualizerChanges), nameof(HasSoundNormalization),
            nameof(HasVoiceClarity), nameof(CanEditSoundNormalization), nameof(CanEditVoiceClarity),
            nameof(SoundNormalizationEnabled), nameof(VoiceClarityEnabled), nameof(SoundNormalizationLevel),
            nameof(VoiceClarityLevel), nameof(CanApplySoundNormalization), nameof(CanApplyVoiceClarity),
            nameof(CanEditSoundNormalizationLevel), nameof(CanEditVoiceClarityLevel),
            nameof(SoundNormalizationLevelLabel), nameof(VoiceClarityLevelLabel),
            nameof(ActiveProfileMessage), nameof(HasActiveApplicationProfile),
        })
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(property));
        }
    }

    private void ConfirmAdvancedDrafts(ThxAudioState confirmed, bool discardEqualizer,
        ThxAudioEnhancementKind? discardEnhancement, bool updateProfileCurves)
    {
        Dictionary<string, double[]> confirmedFactoryCurves = confirmed.FactoryPresetCurves.ToDictionary(
            curve => curve.PresetId, curve => curve.GainsDb.ToArray(), StringComparer.Ordinal);
        bool factoryGeometryConfirmed = confirmed.EqualizerBands.Count > 0
            && confirmedFactoryCurves.Count == 5
            && confirmedFactoryCurves.Values.All(
                gains => gains.Length == confirmed.EqualizerBands.Count);
        factoryPresetCurves[confirmed.Endpoint] = factoryGeometryConfirmed
            ? confirmedFactoryCurves
            : new(StringComparer.Ordinal);
        // Native readback stays visible, but an older operation must not replace
        // curves loaded for a newly selected power profile.
        if (updateProfileCurves)
        {
            if (!presetCurves.TryGetValue(confirmed.Endpoint, out Dictionary<string, double[]>? curves))
            {
                curves = new(StringComparer.Ordinal);
                presetCurves[confirmed.Endpoint] = curves;
            }
            foreach ((string id, double[] gains) in factoryPresetCurves[confirmed.Endpoint])
            {
                if (!curves.TryGetValue(id, out double[]? existing))
                {
                    curves[id] = gains.ToArray();
                }
                else if (existing.Length != gains.Length)
                {
                    curves[id] = AlignHighFrequencyGains(existing, gains);
                }
            }
            if (confirmed.SelectedPresetId is { } selected && confirmed.EqualizerBands.Count > 0)
            {
                curves[selected] = confirmed.EqualizerBands.Select(band => band.GainDb).ToArray();
            }
        }
        discardEqualizer |= equalizerDraftPresetId != confirmed.SelectedPresetId
            || equalizerDraftNativeId != confirmed.SelectedNativeProfileId;
        equalizerDraftPresetId = confirmed.SelectedPresetId;
        equalizerDraftNativeId = confirmed.SelectedNativeProfileId;
        if (equalizerBands.Select(band => band.FrequencyHz)
            .SequenceEqual(confirmed.EqualizerBands.Select(band => band.FrequencyHz)))
        {
            for (int index = 0; index < equalizerBands.Count; index++)
            {
                equalizerBands[index].Confirm(confirmed.EqualizerBands[index].GainDb, discardEqualizer);
            }
        }
        else
        {
            foreach (ThxEqualizerBandViewModel band in equalizerBands)
            {
                band.PropertyChanged -= OnBandPropertyChanged;
            }
            equalizerBands = confirmed.EqualizerBands.Select(band =>
                new ThxEqualizerBandViewModel(band.FrequencyHz, band.GainDb)).ToArray();
            foreach (ThxEqualizerBandViewModel band in equalizerBands)
            {
                band.PropertyChanged += OnBandPropertyChanged;
            }
        }
        if (confirmed.SoundNormalization is { } normalization)
        {
            if (confirmedNormalizationLevel is null || normalizationLevel == confirmedNormalizationLevel
                || discardEnhancement == ThxAudioEnhancementKind.SoundNormalization)
            {
                normalizationLevel = normalization.LevelPercent;
            }
            confirmedNormalizationLevel = normalization.LevelPercent;
        }
        if (confirmed.VoiceClarity is { } clarity)
        {
            if (confirmedVoiceClarityLevel is null || voiceClarityLevel == confirmedVoiceClarityLevel
                || discardEnhancement == ThxAudioEnhancementKind.VoiceClarity)
            {
                voiceClarityLevel = clarity.LevelPercent;
            }
            confirmedVoiceClarityLevel = clarity.LevelPercent;
        }
    }

    private static double[] AlignHighFrequencyGains(
        IReadOnlyList<double> existing,
        IReadOnlyList<double> factory)
    {
        double[] aligned = factory.ToArray();
        int overlap = Math.Min(existing.Count, aligned.Length);
        for (int index = 1; index <= overlap; index++)
        {
            aligned[^index] = existing[^index];
        }
        return aligned;
    }

    private void ResetAdvancedDrafts()
    {
        enhancementContextRevision++;
        foreach (ThxEqualizerBandViewModel band in equalizerBands)
        {
            band.PropertyChanged -= OnBandPropertyChanged;
        }
        equalizerBands = [];
        equalizerDraftPresetId = equalizerDraftNativeId = null;
        normalizationLevel = voiceClarityLevel = 0;
        confirmedNormalizationLevel = confirmedVoiceClarityLevel = null;
    }

    private void OnBandPropertyChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName == nameof(ThxEqualizerBandViewModel.HasChanges))
        {
            NotifyAll();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (disposed)
        {
            return;
        }
        disposed = true;
        enhancementContextRevision++;
        await lifetime.CancelAsync();
        await pendingOperation;
        lifetime.Dispose();
        // Do not reset EQ, spatial processing, or the selected preset on exit.
    }
}
