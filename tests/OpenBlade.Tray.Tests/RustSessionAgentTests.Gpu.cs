using System.Buffers.Binary;
using System.IO.Pipes;
using System.Security.Principal;
using OpenBlade.Session;

namespace OpenBlade.Tray.Tests;

public sealed partial class RustSessionAgentTests
{
    [RustSessionAgentFact]
    public async Task TrayGpuClient_PreservesStableIdentitiesAndBlocksEveryClose()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        CancellationToken token = timeout.Token;
        string id = Guid.NewGuid().ToString("N");
        await using (var rejected = Host.Start(id, "simulated-close", gpu: true))
        {
            await rejected.AssertRejectedAsync(token);
        }
        await using var host = Host.Start(id, "available", gpu: true);
        await host.WaitForReadyAsync(token);
        var client = new SessionGpuAppAdapter(GpuPipeName(id));

        SessionGpuResponse snapshot = await client.QueryAsync(token);
        Assert.Equal(SessionGpuOperationStatus.Succeeded, snapshot.Status);
        Assert.Null(snapshot.Error);
        Assert.Empty(snapshot.Results);
        Assert.Collection(snapshot.Apps,
            row => Assert.Equal(new SessionGpuApp(41, 504911232000000041, "active-game", 15.0,
                false, GpuCloseUnsupported), row),
            row => Assert.Equal(new SessionGpuApp(42, 504911232000000042, "idle-holder", 0,
                false, GpuCloseUnsupported), row),
            row => Assert.Equal(new SessionGpuApp(43, 504911232000000043, "other-session", 7.5,
                false, "The process belongs to another Windows session."), row));

        foreach (bool force in new[] { false, true })
        {
            SessionGpuResponse rejected = await client.CloseAsync([snapshot.Apps[0]], force, token);
            AssertRejectedGpuClose(rejected);
        }
        SessionGpuResponse next = await client.QueryAsync(token);
        Assert.NotEqual(snapshot.RequestId, next.RequestId);
        Assert.Equal(snapshot.Apps.ToArray(), next.Apps.ToArray());
        await host.StopAsync(token);
    }

    [RustSessionAgentFact(requireGpuClose: true)]
    public async Task TrayGpuClient_RoundTripsOnlyFixedSimulatedCloseActions()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        CancellationToken token = timeout.Token;
        string id = Guid.NewGuid().ToString("N");
        var client = new SessionGpuAppAdapter(GpuPipeName(id));
        await using (var queryOnly = Host.Start(id, "available", gpu: true, requireGpuClose: true))
        {
            await queryOnly.WaitForReadyAsync(token);
            SessionGpuApp target = Assert.Single((await client.QueryAsync(token)).Apps, app => app.ProcessId == 41);
            AssertRejectedGpuClose(await client.CloseAsync([target], force: false, token));
            AssertRejectedGpuClose(await client.CloseAsync([target], force: true, token));
            await queryOnly.StopAsync(token);
        }
        await using var host = Host.Start(id, "simulated-close", gpu: true, requireGpuClose: true);
        await host.WaitForReadyAsync(token);
        SessionGpuResponse snapshot = await client.QueryAsync(token);
        Assert.Equal(SessionGpuOperationStatus.Succeeded, snapshot.Status);
        SessionGpuApp owner = Assert.Single(snapshot.Apps, app => app.ProcessId == 41);
        SessionGpuApp idle = Assert.Single(snapshot.Apps, app => app.ProcessId == 42);
        SessionGpuApp foreign = Assert.Single(snapshot.Apps, app => app.ProcessId == 43);
        Assert.Equal(15.0, owner.UtilizationPercent);
        Assert.All(snapshot.Apps, app => Assert.False(app.CanClose));
        SessionGpuApp helper = owner with
        {
            ProcessId = 46,
            StartTimeUtcTicks = 504911232000000046,
        };

        SessionGpuResponse mixed = await client.CloseAsync(
            [owner with { Name = "ACTIVE-GAME" }, idle, helper, foreign], force: false, token);
        Assert.Equal(SessionGpuOperationStatus.PartiallySucceeded, mixed.Status);
        Assert.Equal("Some applications could not be closed.", mixed.Error);
        Assert.Collection(mixed.Results,
            result => Assert.Equal(new SessionGpuActionResult(46, helper.StartTimeUtcTicks, false,
                "The process no longer holds GPU resources or its identity changed after confirmation."), result),
            result => Assert.Equal(new SessionGpuActionResult(41, owner.StartTimeUtcTicks, true, null), result),
            result => Assert.Equal(new SessionGpuActionResult(42, idle.StartTimeUtcTicks, false,
                "The application has no window that can be closed gracefully."), result),
            result => Assert.Equal(new SessionGpuActionResult(43, foreign.StartTimeUtcTicks, false,
                "The process belongs to another Windows session."), result));

        SessionGpuResponse stale = await client.CloseAsync(
            [owner with { StartTimeUtcTicks = owner.StartTimeUtcTicks - 1 }], force: true, token);
        Assert.Equal(SessionGpuOperationStatus.Failed, stale.Status);
        Assert.False(Assert.Single(stale.Results).Succeeded);

        SessionGpuResponse forced = await client.CloseAsync([idle], force: true, token);
        Assert.Equal(SessionGpuOperationStatus.Succeeded, forced.Status);
        Assert.Null(forced.Error);
        Assert.Equal(new SessionGpuActionResult(42, idle.StartTimeUtcTicks, true, null),
            Assert.Single(forced.Results));
        Assert.Equal(snapshot.Apps.ToArray(), forced.Apps.ToArray());
        await host.StopAsync(token);
    }

    [RustSessionAgentFact]
    public async Task TrayGpuClient_DistinguishesEmptyMissingAndPartialInventories()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        CancellationToken token = timeout.Token;
        foreach ((string scenario, SessionGpuOperationStatus expected) in new[]
        {
            ("empty", SessionGpuOperationStatus.Succeeded),
            ("no-data", SessionGpuOperationStatus.Failed),
            ("large", SessionGpuOperationStatus.PartiallySucceeded),
        })
        {
            string id = Guid.NewGuid().ToString("N");
            await using var host = Host.Start(id, scenario, gpu: true);
            await host.WaitForReadyAsync(token);
            SessionGpuResponse snapshot = await new SessionGpuAppAdapter(GpuPipeName(id)).QueryAsync(token);
            Assert.Equal(expected, snapshot.Status);
            Assert.Empty(snapshot.Results);
            if (expected == SessionGpuOperationStatus.PartiallySucceeded)
            {
                Assert.False(string.IsNullOrWhiteSpace(snapshot.Error));
                Assert.InRange(snapshot.Apps.Count, 1, SessionGpuIpc.MaximumApps - 1);
                Assert.Equal(Enumerable.Range(1, snapshot.Apps.Count), snapshot.Apps.Select(app => app.ProcessId));
                Assert.All(snapshot.Apps, app => Assert.Equal(260, app.Name.Length));
            }
            else
            {
                Assert.Empty(snapshot.Apps);
                Assert.Equal(expected == SessionGpuOperationStatus.Failed
                    ? "GPU activity data is unavailable." : null, snapshot.Error);
            }
            await host.StopAsync(token);
        }
    }

    [RustSessionAgentFact]
    public async Task GpuFixture_RejectsBadPeersAndFramesThenReconnects()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        CancellationToken token = timeout.Token;
        string id = Guid.NewGuid().ToString("N");
        await using var host = Host.Start(id, "available", gpu: true);
        await host.WaitForReadyAsync(token);
        await using (var duplicate = Host.Start(id, "available", gpu: true))
        {
            await duplicate.AssertRejectedAsync(token);
        }
        using (var anonymous = GpuPipe(id, TokenImpersonationLevel.Anonymous))
        {
            await anonymous.ConnectAsync(token);
            await SessionGpuIpcCodec.WriteRequestAsync(anonymous, GpuQuery(), token);
            await AssertClosedAsync(anonymous, token);
        }
        using (var oversized = GpuPipe(id))
        {
            await oversized.ConnectAsync(token);
            byte[] prefix = new byte[sizeof(int)];
            BinaryPrimitives.WriteInt32LittleEndian(prefix, SessionGpuIpc.MaximumMessageBytes + 1);
            await oversized.WriteAsync(prefix, token);
            await AssertClosedAsync(oversized, token);
        }
        using (var partial = GpuPipe(id))
        {
            await partial.ConnectAsync(token);
            await partial.WriteAsync(new byte[] { 1 }, token);
            await AssertClosedAsync(partial, token);
        }
        using (var peer = GpuPipe(id))
        {
            await peer.ConnectAsync(token);
            QueryGpuAppsRequest query = GpuQuery();
            await SessionGpuIpcCodec.WriteRequestAsync(peer, query, token);
            SessionGpuResponse result = await SessionGpuIpcCodec.ReadResponseAsync(peer, token);
            Assert.Equal(query.RequestId, result.RequestId);
            Assert.Equal(query.Version, result.Version);
            Assert.Equal(SessionGpuOperationStatus.Succeeded, result.Status);
            await AssertClosedAsync(peer, token);
        }
        await host.StopAsync(token);
    }

    [RustSessionAgentFact]
    public async Task GpuFixture_RetiresCancelledScanAndBoundsStalledScanWithoutScanningOnClose()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        CancellationToken token = timeout.Token;
        string id = Guid.NewGuid().ToString("N");
        await using var host = Host.Start(id, "stalled", gpu: true);
        await host.WaitForReadyAsync(token);
        var client = new SessionGpuAppAdapter(GpuPipeName(id));
        using (var fast = CancellationTokenSource.CreateLinkedTokenSource(token))
        {
            fast.CancelAfter(TimeSpan.FromSeconds(3));
            using var cancelled = GpuPipe(id);
            await cancelled.ConnectAsync(fast.Token);
            await SessionGpuIpcCodec.WriteRequestAsync(cancelled, GpuQuery(), fast.Token);
            // The endpoint allows one request. Extra input cancels the pending scan.
            await cancelled.WriteAsync(new byte[] { 1 }, fast.Token);
            await AssertClosedAsync(cancelled, fast.Token);

            AssertRejectedGpuClose(await client.CloseAsync(
                [new SessionGpuApp(41, 504911232000000041, "active-game", 7.5, false, GpuCloseUnsupported)],
                force: true, fast.Token));
        }

        SessionGpuResponse result = await client.QueryAsync(token);
        Assert.Equal(SessionGpuOperationStatus.Failed, result.Status);
        Assert.Equal("The GPU inventory scan timed out.", result.Error);
        Assert.Empty(result.Apps);
        Assert.Empty(result.Results);
        await host.StopAsync(token);
    }

    private const string GpuCloseUnsupported = "Close GPU apps is unsupported on the private query endpoint.";

    private static string GpuPipeName(string id) => $"OpenBlade.Rust.Session.Gpu.Dev.{id}";

    private static QueryGpuAppsRequest GpuQuery() =>
        new(SessionGpuIpc.ProtocolVersion, Guid.NewGuid());

    private static NamedPipeClientStream GpuPipe(
        string id, TokenImpersonationLevel impersonation = TokenImpersonationLevel.Identification) =>
        new(".", GpuPipeName(id), PipeDirection.InOut, PipeOptions.Asynchronous, impersonation);

    private static void AssertRejectedGpuClose(SessionGpuResponse result)
    {
        Assert.Equal(SessionGpuOperationStatus.Failed, result.Status);
        Assert.Equal(GpuCloseUnsupported, result.Error);
        Assert.Empty(result.Apps);
        Assert.Empty(result.Results);
    }
}
