using OpenBlade.Contracts;
using OpenBlade.Session;
using OpenBlade.Tray;

namespace OpenBlade.SessionAgent;

internal sealed class SessionThxAudioAutomationController : IThxAudioAutomationController
{
    private readonly Func<SessionAudioRequest, CancellationToken, Task<SessionAudioResponse>> dispatch;
    private readonly Func<CancellationToken, Task<bool>> integrationEnabled;
    private readonly Func<bool> activeSession;
    private readonly Func<ThxAudioEndpoint?> activeOutput;
    private readonly SessionAudioRequestDispatcher? dispatcher;

    internal SessionThxAudioAutomationController(SessionAudioRequestDispatcher dispatcher, string preferencesPath)
        : this(dispatcher.DispatchAutomationAsync, async token =>
        {
            try
            {
                return (await new UiPreferenceStore(preferencesPath).LoadAsync(token)
                    .ConfigureAwait(false)).ThxAudioIntegrationEnabled;
            }
            catch (OperationCanceledException) { throw; }
            catch { return false; }
        }, WindowsThxForegroundSource.IsActiveInputSession, WindowsThxActiveOutput.Read)
    {
        this.dispatcher = dispatcher;
    }

    internal SessionThxAudioAutomationController(
        Func<SessionAudioRequest, CancellationToken, Task<SessionAudioResponse>> dispatch,
        Func<CancellationToken, Task<bool>> integrationEnabled,
        Func<bool> activeSession,
        Func<ThxAudioEndpoint?> activeOutput)
    {
        this.dispatch = dispatch;
        this.integrationEnabled = integrationEnabled;
        this.activeSession = activeSession;
        this.activeOutput = activeOutput;
    }

    public Task<ThxAudioOperationStatus> ApplyAsync(
        ThxAudioAutomationTarget target, CancellationToken cancellationToken) =>
        dispatcher is null
            ? ApplyCoreAsync(target, cancellationToken)
            : dispatcher.RunAutomationAsync(token => ApplyCoreAsync(target, token), cancellationToken);

    private async Task<ThxAudioOperationStatus> ApplyCoreAsync(
        ThxAudioAutomationTarget target, CancellationToken cancellationToken)
    {
        if (!ThxAudioProfileSettings.IsWellFormed(target.Settings)
            || !target.Settings.ApplicationRoutingEnabled || !Enum.IsDefined(target.Endpoint))
        {
            return ThxAudioOperationStatus.Unavailable;
        }
        bool submitted = false;
        try
        {
            // Read only the observed active output. This never clears uncertainty.
            ThxAudioEndpoint endpoint = target.Endpoint;
            {
                SessionAudioResponse read = await Send(new ReadThxAudioRequest(
                    SessionAudioIpc.ProtocolVersion, Guid.NewGuid(), endpoint)).ConfigureAwait(false);
                if (read.Status != ThxAudioOperationStatus.Succeeded || read.State?.Endpoint != endpoint)
                {
                    return read.Status == ThxAudioOperationStatus.Succeeded
                        ? ThxAudioOperationStatus.Failed : read.Status;
                }
                ThxAudioState state = read.State;
                ThxAudioEndpointSettings settings = endpoint == ThxAudioEndpoint.Speakers
                    ? target.Settings.Speakers : target.Settings.Headphones;
                if (read.State.VoiceClarity is null
                    || settings.SoundNormalization is not null && read.State.SoundNormalization is null)
                {
                    return ThxAudioOperationStatus.Unavailable;
                }
                if (target.ApplicationRule is { Output: ThxApplicationAudioOutput.Immersive or ThxApplicationAudioOutput.Competitive } advertisedRule)
                {
                    ThxAudioApplicationProfile? profile = read.State.ApplicationProfileCatalog.FirstOrDefault(
                        item => string.Equals(item.ExecutableName, advertisedRule.ExecutableName, StringComparison.OrdinalIgnoreCase));
                    if (profile is null || (advertisedRule.Output == ThxApplicationAudioOutput.Immersive
                            ? !profile.SupportsImmersive : !profile.SupportsCompetitive))
                    {
                        return ThxAudioOperationStatus.Unavailable;
                    }
                }
                else if (!read.State.Presets.Any(preset => preset.Id == PresetId(Preset(target, settings))))
                {
                    return ThxAudioOperationStatus.Unavailable;
                }
                if (target.ApplicationRule is { Output: ThxApplicationAudioOutput.Immersive or ThxApplicationAudioOutput.Competitive } rule)
                {
                    state = await Change(new ApplyThxApplicationProfileRequest(
                        SessionAudioIpc.ProtocolVersion, Guid.NewGuid(), endpoint, rule.ExecutableName,
                        rule.Output == ThxApplicationAudioOutput.Immersive
                            ? ThxAudioApplicationMode.Immersive : ThxAudioApplicationMode.Competitive)).ConfigureAwait(false);
                }
                else
                {
                    ThxEqualizerPreset preset = Preset(target, settings);
                    IReadOnlyList<double> gains = Curve(settings.EqualizerCurves, preset);
                    if (state.SelectedPresetId != PresetId(preset)
                        || !state.EqualizerBands.Select(band => band.GainDb).SequenceEqual(gains))
                    {
                        state = await Change(new SelectThxPresetRequest(SessionAudioIpc.ProtocolVersion,
                            Guid.NewGuid(), endpoint, PresetId(preset), gains)).ConfigureAwait(false);
                    }
                }
                bool spatial = target.ApplicationRule is not null || settings.SpatialEnabled;
                if (state.SpatialEnabled != spatial)
                {
                    state = await Change(new SetThxSpatialRequest(SessionAudioIpc.ProtocolVersion,
                        Guid.NewGuid(), endpoint, spatial)).ConfigureAwait(false);
                }
                if (state.EqualizerEnabled != settings.EqualizerEnabled)
                {
                    state = await Change(new SetThxEqualizerRequest(SessionAudioIpc.ProtocolVersion,
                        Guid.NewGuid(), endpoint, settings.EqualizerEnabled)).ConfigureAwait(false);
                }
                if (!Matches(state.VoiceClarity, settings.VoiceClarity))
                {
                    state = await Change(new SetThxEnhancementRequest(SessionAudioIpc.ProtocolVersion,
                        Guid.NewGuid(), endpoint, ThxAudioEnhancementKind.VoiceClarity,
                        settings.VoiceClarity.Enabled,
                        settings.VoiceClarity.Enabled ? settings.VoiceClarity.LevelPercent : null)).ConfigureAwait(false);
                }
                if (settings.SoundNormalization is { } normalization
                    && !Matches(state.SoundNormalization, normalization))
                {
                    _ = await Change(new SetThxEnhancementRequest(SessionAudioIpc.ProtocolVersion,
                        Guid.NewGuid(), endpoint, ThxAudioEnhancementKind.SoundNormalization,
                        normalization.Enabled,
                        normalization.Enabled ? normalization.LevelPercent : null)).ConfigureAwait(false);
                }
            }
            return ThxAudioOperationStatus.Succeeded;
        }
        catch (OperationFailure failure)
        {
            return submitted ? ThxAudioOperationStatus.Indeterminate : failure.Status;
        }
        catch
        {
            return submitted ? ThxAudioOperationStatus.Indeterminate : ThxAudioOperationStatus.Failed;
        }

        async Task<SessionAudioResponse> Send(SessionAudioRequest request)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!activeSession() || !await integrationEnabled(cancellationToken).ConfigureAwait(false)
                || !activeSession() || activeOutput() != target.Endpoint)
            {
                throw new OperationFailure(ThxAudioOperationStatus.Unavailable);
            }
            cancellationToken.ThrowIfCancellationRequested();
            if (request is not ReadThxAudioRequest)
            {
                submitted = true;
            }
            return await dispatch(request, cancellationToken).ConfigureAwait(false);
        }

        async Task<ThxAudioState> Change(SessionAudioRequest request)
        {
            SessionAudioResponse result = await Send(request).ConfigureAwait(false);
            if (result.Status != ThxAudioOperationStatus.Succeeded || result.State?.Endpoint != target.Endpoint)
            {
                throw new OperationFailure(result.Status == ThxAudioOperationStatus.Succeeded
                    ? ThxAudioOperationStatus.Indeterminate : result.Status);
            }
            return result.State;
        }
    }

    private static bool Matches(ThxAudioEnhancement? current, ThxAudioEnhancementSettings requested) =>
        current is not null
        && current.Enabled == requested.Enabled
        && (!requested.Enabled || current.LevelPercent == requested.LevelPercent);

    private static ThxEqualizerPreset Preset(ThxAudioAutomationTarget target, ThxAudioEndpointSettings settings) =>
        target.ApplicationRule?.Output switch
        {
            ThxApplicationAudioOutput.Game => ThxEqualizerPreset.Game,
            ThxApplicationAudioOutput.Movie => ThxEqualizerPreset.Movie,
            ThxApplicationAudioOutput.Music => ThxEqualizerPreset.Music,
            ThxApplicationAudioOutput.Voice => ThxEqualizerPreset.Voice,
            ThxApplicationAudioOutput.Custom => ThxEqualizerPreset.Custom,
            _ => settings.SelectedPreset,
        };

    private static string PresetId(ThxEqualizerPreset preset) => preset.ToString().ToLowerInvariant();

    private static IReadOnlyList<double> Curve(ThxEqualizerCurves curves, ThxEqualizerPreset preset) => preset switch
    {
        ThxEqualizerPreset.Game => curves.Game,
        ThxEqualizerPreset.Movie => curves.Movie,
        ThxEqualizerPreset.Music => curves.Music,
        ThxEqualizerPreset.Voice => curves.Voice,
        ThxEqualizerPreset.Custom => curves.Custom,
        _ => throw new InvalidDataException("Unsupported THX preset."),
    };

    private sealed class OperationFailure(ThxAudioOperationStatus status) : Exception
    {
        internal ThxAudioOperationStatus Status { get; } = status;
    }
}
