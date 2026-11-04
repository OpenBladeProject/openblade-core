using OpenBlade.Contracts;
using OpenBlade.Protocol;
using OpenBlade.Service;
using static OpenBlade.Service.Tests.ProductionLightingSessionFixture;

namespace OpenBlade.Service.Tests;

public sealed class ProductionLightingRecoveryAdmissionTests
{
    [Fact]
    public async Task RecoveryAdmission_OpeningFactorySeesRevocationBeforeInitialDriverWrite()
    {
        bool admitted = true;
        var port = new FakeLightingPort(supportedLightingRefreshRates: new HashSet<LightingRefreshRate> { LightingRefreshRate.ThirtyHz });
        var factory = new FakeLightingFactory(port)
        {
            Opening = admission =>
            {
                Assert.True(admission());
                admitted = false;
                Assert.False(admission());
            },
        };
        var session = new ProductionLightingSession(factory, null, null,
            static _ => Task.CompletedTask, static () => { });
        session.SetRecoveryWriteAdmission(() => admitted);
        await Assert.ThrowsAsync<BladeWriteException>(() => session.GetLightingPortCoreAsync(
            ProductionLightingSession.LightingPortOperation.KeyboardLightingStateRead, default));
        Assert.Same(port, session.RetainedPort);
        Assert.False(port.RecoveryWriteAdmission!());
        Assert.Equal(0, port.DisposeCount);
        await session.DisposeLightingPortWithinDeadlineAsync();
    }

    [Fact]
    public async Task RecoveryAdmission_PlaybackLateRefusalDoesNotDisposeRetainedPort()
    {
        var port = new FakeLightingPort
        {
            PlaybackMuteFailure = new BladeWriteException(WriteSubmissionState.KnownNotApplied,
                BladeWriteFailurePhase.ControlRecoveryAdmission, "read-only recovery"),
        };
        var session = new ProductionLightingSession(new FakeLightingFactory(port), null, null,
            static _ => Task.CompletedTask, static () => { });
        session.SetRecoveryWriteAdmission(static () => true);
        await Assert.ThrowsAsync<BladeWriteException>(() => session.SetPlaybackMuteIndicatorAsync(true, default));
        Assert.Same(port, session.RetainedPort);
        Assert.Equal(0, port.DisposeCount);
        await session.DisposeLightingPortWithinDeadlineAsync();
    }
}
