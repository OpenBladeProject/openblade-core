using OpenBlade.Platform;
using OpenBlade.Session;

namespace OpenBlade.SessionAgent.Tests;

public sealed class SessionGpuRequestDispatcherTests
{
    [Fact]
    public async Task Query_MapsEligibleAndProtectedApps()
    {
        var manager = new FakeGpuAppManager
        {
            ScanResult = Scan(
                App(42, 123, "game", canClose: true),
                App(8, 456, "dwm", canClose: false)),
        };
        var dispatcher = new SessionGpuRequestDispatcher(manager);
        Guid requestId = Guid.NewGuid();

        SessionGpuResponse response = await dispatcher.DispatchAsync(
            new QueryGpuAppsRequest(SessionGpuIpc.ProtocolVersion, requestId),
            CancellationToken.None);

        Assert.Equal(SessionGpuOperationStatus.Succeeded, response.Status);
        Assert.Equal(requestId, response.RequestId);
        Assert.Collection(
            response.Apps,
            app => Assert.True(app.CanClose),
            app => Assert.False(app.CanClose));
        Assert.Equal(0, manager.CloseCount);
        Assert.Equal(0, manager.ForceCount);
    }

    [Fact]
    public async Task Close_RejectsChangedIdentityBeforeInvokingManager()
    {
        var manager = new FakeGpuAppManager
        {
            ScanResult = Scan(App(42, 999, "replacement", canClose: true)),
        };
        var dispatcher = new SessionGpuRequestDispatcher(manager);

        SessionGpuResponse response = await dispatcher.DispatchAsync(
            new CloseGpuAppsRequest(
                SessionGpuIpc.ProtocolVersion,
                Guid.NewGuid(),
                Force: true,
                [new SessionGpuAppIdentity(42, 123, "game")]),
            CancellationToken.None);

        Assert.Equal(SessionGpuOperationStatus.Failed, response.Status);
        Assert.Single(response.Results);
        Assert.Contains("identity changed", response.Results[0].Error, StringComparison.Ordinal);
        Assert.Equal(0, manager.CloseCount);
        Assert.Equal(0, manager.ForceCount);
    }

    [Fact]
    public async Task ForceClose_InvokesForcePathForExactIdentity()
    {
        WindowsGpuApp target = App(42, 123, "game", canClose: true);
        var manager = new FakeGpuAppManager
        {
            ScanResult = Scan(target),
        };
        var dispatcher = new SessionGpuRequestDispatcher(manager);

        SessionGpuResponse response = await dispatcher.DispatchAsync(
            new CloseGpuAppsRequest(
                SessionGpuIpc.ProtocolVersion,
                Guid.NewGuid(),
                Force: true,
                [new SessionGpuAppIdentity(42, 123, "game")]),
            CancellationToken.None);

        Assert.Equal(SessionGpuOperationStatus.Succeeded, response.Status);
        Assert.Equal(0, manager.CloseCount);
        Assert.Equal(1, manager.ForceCount);
        Assert.Equal(target, Assert.Single(manager.LastTargets));
    }

    [Fact]
    public async Task Query_RejectsMoreAppsThanTheProtocolCanRepresent()
    {
        WindowsGpuApp[] apps = Enumerable.Range(1, SessionGpuIpc.MaximumApps + 1)
            .Select(processId => App(processId, processId, $"app{processId}", canClose: true))
            .ToArray();
        var manager = new FakeGpuAppManager
        {
            ScanResult = new WindowsGpuAppScan(true, null, apps),
        };
        var dispatcher = new SessionGpuRequestDispatcher(manager);

        SessionGpuResponse response = await dispatcher.DispatchAsync(
            new QueryGpuAppsRequest(SessionGpuIpc.ProtocolVersion, Guid.NewGuid()),
            CancellationToken.None);

        Assert.Equal(SessionGpuOperationStatus.Failed, response.Status);
        Assert.Contains("Too many", response.Error, StringComparison.Ordinal);
        Assert.Empty(response.Apps);
        Assert.Empty(response.Results);
        await SessionGpuIpcCodec.WriteResponseAsync(new MemoryStream(), response);
    }

    [Fact]
    public async Task Query_ObservesDeadlineThatExpiresDuringScan()
    {
        using var cancellation = new CancellationTokenSource();
        var manager = new FakeGpuAppManager
        {
            ScanResult = Scan(),
            ScanAction = cancellation.Cancel,
        };
        var dispatcher = new SessionGpuRequestDispatcher(manager);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            dispatcher.DispatchAsync(
                new QueryGpuAppsRequest(SessionGpuIpc.ProtocolVersion, Guid.NewGuid()),
                cancellation.Token));
    }

    private static WindowsGpuAppScan Scan(params WindowsGpuApp[] apps) =>
        new(true, null, apps);

    private static WindowsGpuApp App(
        int processId,
        long startTime,
        string name,
        bool canClose) => new(
            processId,
            startTime,
            name,
            $@"C:\Apps\{name}.exe",
            1,
            canClose,
            canClose ? null : "Windows process");

    private sealed class FakeGpuAppManager : IWindowsGpuAppManager
    {
        public required WindowsGpuAppScan ScanResult { get; init; }

        public Action? ScanAction { get; init; }

        public int CloseCount { get; private set; }

        public int ForceCount { get; private set; }

        public IReadOnlyList<WindowsGpuApp> LastTargets { get; private set; } = [];

        public WindowsGpuAppScan Scan(CancellationToken cancellationToken = default)
        {
            ScanAction?.Invoke();
            return ScanResult;
        }

        public IReadOnlyList<WindowsGpuAppActionResult> CloseGracefully(
            IReadOnlyList<WindowsGpuApp> apps,
            CancellationToken cancellationToken = default)
        {
            CloseCount++;
            LastTargets = apps;
            return Success(apps);
        }

        public IReadOnlyList<WindowsGpuAppActionResult> ForceClose(
            IReadOnlyList<WindowsGpuApp> apps,
            CancellationToken cancellationToken = default)
        {
            ForceCount++;
            LastTargets = apps;
            return Success(apps);
        }

        private static IReadOnlyList<WindowsGpuAppActionResult> Success(
            IReadOnlyList<WindowsGpuApp> apps) => apps
                .Select(app => new WindowsGpuAppActionResult(
                    app.ProcessId,
                    app.StartTimeUtcTicks,
                    true,
                    null))
                .ToArray();
    }
}
