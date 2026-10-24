using System.ComponentModel;
using OpenBlade.Session;
using OpenBlade.Tray;

namespace OpenBlade.Tray.Tests;

public sealed class ThxAudioDiagnosticProviderTests
{
    [Fact]
    public async Task CaptureAsync_RedactsAndBoundsBaseAudioDetailWithoutExportingTarget()
    {
        string userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var response = new SessionAudioResponse(SessionAudioIpc.ProtocolVersion, Guid.NewGuid(),
            ThxAudioOperationStatus.Unavailable, ThxAudioAvailability.ComponentsUnavailable, "THX is absent.", null)
        {
            BaseAudio = new(true, false, false,
                $"Base driver for {Environment.UserName} at {userProfile}.\r\n" + new string('x', 1200)),
        };
        var adapter = new StubAdapter(response);
        ThxAudioDiagnosticReport report = await new ThxAudioDiagnosticProvider(adapter).CaptureAsync(CancellationToken.None);
        Assert.Equal(1, adapter.DiscoverCount);
        Assert.NotNull(report.BaseAudio);
        Assert.True(report.BaseAudio.ExactRoot);
        Assert.False(report.BaseAudio.Ready);
        Assert.False(report.BaseAudio.CanInstall);
        Assert.Equal(1024, report.BaseAudio.Detail.Length);
        Assert.Contains("[REDACTED]", report.BaseAudio.Detail, StringComparison.Ordinal);
        Assert.DoesNotContain(Environment.UserName, report.BaseAudio.Detail, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain('\r', report.BaseAudio.Detail);
        Assert.DoesNotContain('\n', report.BaseAudio.Detail);
        string json = System.Text.Json.JsonSerializer.Serialize(report);
        Assert.DoesNotContain("Target", json, StringComparison.Ordinal);
        Assert.Null(typeof(ThxBaseAudioDiagnosticProof).GetProperty("Target"));
    }

    [Fact]
    public async Task CaptureAsync_OmitsAbsentBaseProofFromExistingReportJson()
    {
        var adapter = new StubAdapter(new SessionAudioResponse(SessionAudioIpc.ProtocolVersion, Guid.NewGuid(),
            ThxAudioOperationStatus.Succeeded, ThxAudioAvailability.Available, null, null));
        ThxAudioDiagnosticReport report = await new ThxAudioDiagnosticProvider(adapter).CaptureAsync(CancellationToken.None);
        Assert.Null(report.BaseAudio);
        Assert.DoesNotContain("BaseAudio", System.Text.Json.JsonSerializer.Serialize(report), StringComparison.Ordinal);
    }

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
        Assert.Equal("TimedOut", report.Failure?.Reason);
        Assert.Equal(nameof(OperationCanceledException), report.Failure?.ExceptionType);
    }

    [Fact]
    public async Task CaptureAsync_PreservesWrappedNativeFailureWithoutPrivateMessage()
    {
        var exception = new IOException("private path", new Win32Exception(5, "private service"));
        var provider = new ThxAudioDiagnosticProvider(new StubAdapter(exception));
        ThxAudioDiagnosticReport report = await provider.CaptureAsync(CancellationToken.None);
        Assert.Equal("Discovery", report.Failure?.Stage);
        Assert.Equal(exception.HResult, report.Failure?.HResult);
        Assert.Equal(5, report.Failure?.Cause?.NativeErrorCode);
        Assert.DoesNotContain("private", System.Text.Json.JsonSerializer.Serialize(report), StringComparison.OrdinalIgnoreCase);
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
