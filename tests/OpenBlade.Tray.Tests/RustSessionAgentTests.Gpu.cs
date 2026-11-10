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
