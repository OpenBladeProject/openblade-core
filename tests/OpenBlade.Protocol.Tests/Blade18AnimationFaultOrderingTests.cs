using OpenBlade.Contracts;
using OpenBlade.Protocol;

namespace OpenBlade.Protocol.Tests;

public sealed partial class ExperimentalBladeLightingTests
{
    // A failed animation frame must record the write pause before Faulted is
    // observable. Readers outside the operation gate (the service, the tray
    // status and the stop-on-ambiguity tests) treat Faulted as "writes are
    // paused". With the pause recorded after Faulted, an observer that polls
    // without delay sees the window in most iterations, so 100 runs fail
    // reliably on that order and stay at zero violations on the fixed one.
    [Fact]
    public async Task Blade18AnimationFault_RecordsThePauseBeforeFaultedIsObservable()
    {
        int violations = 0;
        for (int iteration = 0; iteration < 100; iteration++)
        {
            var clock = new AnimationClock();
            var session = new Session { FailAnimationBatch = 2 };
            var policy = Enabled();
            await using var port = AnimatedPort(session, clock, policy);
            await port.SetLightingAsync(LightingSettingsFactory.Fire(50), CancellationToken.None);
            using var stop = new CancellationTokenSource();
            Task<bool> observer = Task.Run(() =>
            {
                while (!stop.IsCancellationRequested)
                {
                    if (port.OwnershipState == LightingRendererOwnershipState.Faulted)
                    {
                        return policy.WritesAllowed;
                    }
                }
                return false;
            });
            clock.Advance(TimeSpan.FromMilliseconds(50));
            await WaitUntilAsync(() => port.OwnershipState == LightingRendererOwnershipState.Faulted);
            stop.Cancel();
            if (await observer)
            {
                violations++;
            }
        }
        Assert.Equal(0, violations);
    }
}
