using OpenBlade.Session;
using OpenBlade.Tray;

namespace OpenBlade.Tray.Tests;

public sealed class ThxAudioDiagnosticProviderTests
{
    [Fact]
    public async Task CaptureAsync_UsesDiscoveryOnlyAndReportsActualAdmissionFailure()
    {
        string userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        string error = $"Failure for {Environment.UserName} at {userProfile}.\r\n"
            + new string('x', 1100);
        var adapter = new StubAdapter(new SessionAudioResponse(
            SessionAudioIpc.ProtocolVersion,
            Guid.NewGuid(),
            ThxAudioOperationStatus.Unavailable,
            ThxAudioAvailability.ComponentsUnavailable,
            error,
            null));
        var provider = new ThxAudioDiagnosticProvider(adapter);

        ThxAudioDiagnosticReport report = await provider.CaptureAsync(CancellationToken.None);

        Assert.Equal(1, adapter.DiscoverCount);
        Assert.Equal(ThxAudioDiagnosticCaptureStatus.Succeeded, report.CaptureStatus);
        Assert.Equal(ThxAudioOperationStatus.Unavailable, report.OperationStatus);
        Assert.Equal(ThxAudioAvailability.ComponentsUnavailable, report.Availability);
        Assert.NotNull(report.Error);
        Assert.Equal(1024, report.Error.Length);
        Assert.Contains("[REDACTED]", report.Error, StringComparison.Ordinal);
        Assert.DoesNotContain(Environment.UserName, report.Error, StringComparison.OrdinalIgnoreCase);
        if (!string.IsNullOrWhiteSpace(userProfile))
        {
            Assert.DoesNotContain(userProfile, report.Error, StringComparison.OrdinalIgnoreCase);
        }
        Assert.DoesNotContain('\r', report.Error);
        Assert.DoesNotContain('\n', report.Error);
    }

    [Fact]
    public async Task CaptureAsync_WhenSessionAgentTimesOut_ReportsTimeoutSeparately()
    {
        var adapter = new StubAdapter(new OperationCanceledException());
        var provider = new ThxAudioDiagnosticProvider(adapter);

        ThxAudioDiagnosticReport report = await provider.CaptureAsync(CancellationToken.None);

        Assert.Equal(ThxAudioDiagnosticCaptureStatus.Failed, report.CaptureStatus);
        Assert.Null(report.OperationStatus);
        Assert.Null(report.Availability);
        Assert.Equal("The Session Agent THX admission query timed out.", report.Error);
    }

    [Fact]
    public async Task CaptureAsync_WhenCallerCancels_PropagatesCancellation()
    {
        using var source = new CancellationTokenSource();
        source.Cancel();
        var adapter = new StubAdapter(new OperationCanceledException(source.Token));
        var provider = new ThxAudioDiagnosticProvider(adapter);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            provider.CaptureAsync(source.Token));
    }

    private sealed class StubAdapter : IThxAudioAdapter
    {
        private readonly SessionAudioResponse? response;
        private readonly Exception? exception;

        internal StubAdapter(SessionAudioResponse response)
        {
            this.response = response;
        }

        internal StubAdapter(Exception exception)
        {
            this.exception = exception;
        }

        internal int DiscoverCount { get; private set; }

        public Task<SessionAudioResponse> DiscoverAsync(CancellationToken cancellationToken)
        {
            DiscoverCount++;
            return exception is null
                ? Task.FromResult(response!)
                : Task.FromException<SessionAudioResponse>(exception);
        }

        public Task<SessionAudioResponse> ConnectAsync(
            ThxAudioEndpoint endpoint,
            CancellationToken cancellationToken) => throw UnexpectedMutation();

        public Task<SessionAudioResponse> ReadAsync(
            ThxAudioEndpoint endpoint,
            CancellationToken cancellationToken) => throw UnexpectedMutation();

        public Task<SessionAudioResponse> SetSpatialAsync(
            ThxAudioEndpoint endpoint,
            bool enabled,
            CancellationToken cancellationToken) => throw UnexpectedMutation();

        public Task<SessionAudioResponse> SetEqualizerAsync(
            ThxAudioEndpoint endpoint,
            bool enabled,
            CancellationToken cancellationToken) => throw UnexpectedMutation();

        public Task<SessionAudioResponse> SelectPresetAsync(
            ThxAudioEndpoint endpoint,
            string presetId,
            IReadOnlyList<double> gainsDb,
            CancellationToken cancellationToken) => throw UnexpectedMutation();

        public Task<SessionAudioResponse> SetEnhancementAsync(
            ThxAudioEndpoint endpoint,
            ThxAudioEnhancementKind kind,
            bool enabled,
            int? levelPercent,
            CancellationToken cancellationToken) => throw UnexpectedMutation();

        private static InvalidOperationException UnexpectedMutation() =>
            new("THX diagnostics must use discovery only.");
    }
}
