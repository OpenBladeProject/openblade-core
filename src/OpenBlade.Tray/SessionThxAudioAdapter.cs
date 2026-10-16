using System.Diagnostics;
using System.IO.Pipes;
using System.Security.Principal;
using OpenBlade.Session;

namespace OpenBlade.Tray;

internal sealed class SessionThxAudioAdapter : IThxAudioAdapter
{
    private readonly string pipeName;

    internal SessionThxAudioAdapter()
    {
        SecurityIdentifier sid = WindowsIdentity.GetCurrent().User
            ?? throw new InvalidOperationException("The current user SID is unavailable.");
        using Process process = Process.GetCurrentProcess();
        pipeName = SessionAgentPipeEndpoint.CreateAudio(sid, process.SessionId).PipeName;
    }

    public Task<SessionAudioResponse> DiscoverAsync(CancellationToken cancellationToken) =>
        SendAsync(id => new DiscoverThxAudioRequest(SessionAudioIpc.ProtocolVersion, id), cancellationToken);

    public Task<SessionAudioResponse> ReadAsync(ThxAudioEndpoint endpoint, CancellationToken cancellationToken) =>
        SendAsync(id => new ReadThxAudioRequest(SessionAudioIpc.ProtocolVersion, id, endpoint), cancellationToken);

    public Task<SessionAudioResponse> SetSpatialAsync(ThxAudioEndpoint endpoint, bool enabled, CancellationToken cancellationToken) =>
        SendAsync(id => new SetThxSpatialRequest(SessionAudioIpc.ProtocolVersion, id, endpoint, enabled), cancellationToken);

    public Task<SessionAudioResponse> SetEqualizerAsync(ThxAudioEndpoint endpoint, bool enabled, CancellationToken cancellationToken) =>
        SendAsync(id => new SetThxEqualizerRequest(SessionAudioIpc.ProtocolVersion, id, endpoint, enabled), cancellationToken);

    public Task<SessionAudioResponse> SelectPresetAsync(ThxAudioEndpoint endpoint, string presetId, IReadOnlyList<double> gainsDb, CancellationToken cancellationToken) =>
        SendAsync(id => new SelectThxPresetRequest(SessionAudioIpc.ProtocolVersion, id, endpoint, presetId, gainsDb), cancellationToken);

    public Task<SessionAudioResponse> SetEnhancementAsync(ThxAudioEndpoint endpoint, ThxAudioEnhancementKind kind, bool enabled, int? levelPercent, CancellationToken cancellationToken) =>
        SendAsync(id => new SetThxEnhancementRequest(SessionAudioIpc.ProtocolVersion, id, endpoint, kind, enabled, levelPercent), cancellationToken);

    private async Task<SessionAudioResponse> SendAsync(
        Func<Guid, SessionAudioRequest> createRequest,
        CancellationToken cancellationToken)
    {
        Guid requestId = Guid.NewGuid();
        using var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        using (var connection = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
        {
            connection.CancelAfter(TimeSpan.FromSeconds(2));
            await pipe.ConnectAsync(connection.Token).ConfigureAwait(false);
        }
        using var operation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        operation.CancelAfter(TimeSpan.FromSeconds(20));
        await SessionAudioIpcCodec.WriteRequestAsync(pipe, createRequest(requestId), operation.Token).ConfigureAwait(false);
        SessionAudioResponse response = await SessionAudioIpcCodec.ReadResponseAsync(pipe, operation.Token).ConfigureAwait(false);
        if (response.RequestId != requestId)
        {
            throw new InvalidDataException("Session-agent audio response ID does not match the request.");
        }
        return response;
    }
}
