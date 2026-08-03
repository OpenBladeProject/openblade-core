using System.IO.Pipes;
using System.Diagnostics;
using System.Security.Principal;
using OpenBlade.Contracts;
using OpenBlade.Session;
using Backend = OpenBlade.Display;

namespace OpenBlade.Tray;

internal sealed class SessionDisplaySettingsAdapter : IDisplaySettingsAdapter
{
    private static readonly TimeSpan ConnectionTimeout = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan QueryTimeout = TimeSpan.FromSeconds(8);
    private static readonly TimeSpan MutationTimeout = TimeSpan.FromSeconds(35);
    private readonly string pipeName;

    internal SessionDisplaySettingsAdapter()
    {
        SecurityIdentifier userSid = WindowsIdentity.GetCurrent().User
            ?? throw new InvalidOperationException("The current user SID is unavailable.");
        using Process process = Process.GetCurrentProcess();
        pipeName = SessionAgentPipeEndpoint.Create(userSid, process.SessionId).PipeName;
    }

    public Task<DisplaySettingsSnapshot> GetStateAsync(CancellationToken token) =>
        SendAsync(
            requestId => new QueryDisplayRequest(
                SessionDisplayIpc.ProtocolVersion,
                requestId),
            token);

    public Task<DisplaySettingsSnapshot> SetRefreshRateAsync(
        PowerSource expectedPowerSource,
        string refreshRateKey,
        CancellationToken token) => SendAsync(
            requestId => new SetRefreshRateRequest(
                SessionDisplayIpc.ProtocolVersion,
                requestId,
                expectedPowerSource,
                refreshRateKey),
            token);

    public Task<DisplaySettingsSnapshot> SetHdrEnabledAsync(
        PowerSource expectedPowerSource,
        bool enabled,
        CancellationToken token) => SendAsync(
            requestId => new SetHdrRequest(
                SessionDisplayIpc.ProtocolVersion,
                requestId,
                expectedPowerSource,
                enabled),
            token);

    public Task<DisplaySettingsSnapshot> SetColorProfileAsync(
        PowerSource expectedPowerSource,
        string colorProfileKey,
        CancellationToken token) => SendAsync(
            requestId => new SetColorProfileRequest(
                SessionDisplayIpc.ProtocolVersion,
                requestId,
                expectedPowerSource,
                colorProfileKey),
            token);

    public Task<DisplaySettingsSnapshot> ApplyProfileAsync(
        PowerSource expectedPowerSource,
        DisplayProfileSettings settings,
        CancellationToken token) => SendAsync(
            requestId => new ApplyDisplayProfileRequest(
                SessionDisplayIpc.ProtocolVersion,
                requestId,
                expectedPowerSource,
                settings),
            token);

    private async Task<DisplaySettingsSnapshot> SendAsync(
        Func<Guid, SessionDisplayRequest> createRequest,
        CancellationToken cancellationToken)
    {
        Guid requestId = Guid.NewGuid();
        SessionDisplayRequest request = createRequest(requestId);
        using var pipe = new NamedPipeClientStream(
            ".",
            pipeName,
            PipeDirection.InOut,
            PipeOptions.Asynchronous);
        try
        {
            using (var connectionDeadline = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken))
            {
                connectionDeadline.CancelAfter(ConnectionTimeout);
                await pipe.ConnectAsync(connectionDeadline.Token).ConfigureAwait(false);
            }

            using var exchangeDeadline = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken);
            exchangeDeadline.CancelAfter(request is QueryDisplayRequest
                ? QueryTimeout
                : MutationTimeout);
            await SessionDisplayIpcCodec.WriteRequestAsync(
                pipe,
                request,
                exchangeDeadline.Token).ConfigureAwait(false);
            SessionDisplayResponse response = await SessionDisplayIpcCodec.ReadResponseAsync(
                pipe,
                exchangeDeadline.Token).ConfigureAwait(false);
            if (response.RequestId != requestId)
            {
                throw new InvalidDataException(
                    "Session-agent response ID does not match the request.");
            }

            return Map(response);
        }
        catch (OperationCanceledException) when (
            !cancellationToken.IsCancellationRequested)
        {
            return Unavailable("The display session agent is not responding.");
        }
        catch (Exception exception) when (
            !cancellationToken.IsCancellationRequested
            && exception is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            return Unavailable($"Display controls are unavailable: {exception.Message}");
        }
    }

    private static DisplaySettingsSnapshot Map(SessionDisplayResponse response) =>
        Map(response.State) with
        {
            StatusMessage = response.Error ?? response.State.Message,
            OperationSucceeded = response.Status == SessionDisplayOperationStatus.Succeeded,
        };

    private static DisplaySettingsSnapshot Map(Backend.DisplaySettingsSnapshot state) => new(
        state.IsAvailable,
        state.RefreshOptions
            .Select(option => new DisplayRefreshRateOption(
                option.Key,
                option.Name,
                option.IsDynamic))
            .ToArray(),
        state.CurrentRefreshKey,
        state.HdrSupported,
        state.HdrEnabled,
        state.ColorProfiles
            .Select(profile => new DisplayColorProfileOption(profile.Id, profile.Name))
            .ToArray(),
        state.ActiveColorProfileId,
        state.Message,
        true,
        state.DisplayName,
        state.Width,
        state.Height,
        state.BitsPerColorChannel);

    private static DisplaySettingsSnapshot Unavailable(string message) => new(
        IsAvailable: false,
        RefreshRateOptions: [],
        SelectedRefreshRateKey: null,
        IsHdrSupported: false,
        IsHdrEnabled: false,
        ColorProfileOptions: [],
        SelectedColorProfileKey: null,
        StatusMessage: message,
        OperationSucceeded: false);
}
