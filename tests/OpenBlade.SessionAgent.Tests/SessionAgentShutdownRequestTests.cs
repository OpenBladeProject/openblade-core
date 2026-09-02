namespace OpenBlade.SessionAgent.Tests;

public sealed class SessionAgentShutdownRequestTests
{
    [Fact]
    public void SignalUnregistersApplicationRestartBeforeCancelingLifetime()
    {
        var events = new List<string>();
        using var lifetime = new CancellationTokenSource();
        using CancellationTokenRegistration cancellation = lifetime.Token.Register(
            () => events.Add("cancel"));
        var request = new SessionAgentShutdownRequest(
            lifetime,
            () => events.Add("unregister"));

        request.Signal();

        Assert.Equal(["unregister", "cancel"], events);
    }

    [Fact]
    public void UnregistrationFailureDoesNotBlockLifetimeCancellation()
    {
        using var lifetime = new CancellationTokenSource();
        var request = new SessionAgentShutdownRequest(
            lifetime,
            () => throw new InvalidOperationException("unregister"));

        request.Signal();

        Assert.True(lifetime.IsCancellationRequested);
    }

    [Fact]
    public void SignalIgnoresAConcurrentLifetimeDisposal()
    {
        var lifetime = new CancellationTokenSource();
        var request = new SessionAgentShutdownRequest(lifetime, () => { });
        lifetime.Dispose();

        request.Signal();
    }
}
