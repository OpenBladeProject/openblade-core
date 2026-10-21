using OpenBlade.Session;
using OpenBlade.Tray;

namespace OpenBlade.Tray.Tests;

public sealed class WindowsBatteryHistoryViewModelTests
{
    [Fact]
    public async Task Refresh_ExposesTypedReportStatusAndTimeline()
    {
        WindowsBatteryReport report = CreateReport();
        await using var viewModel = new WindowsBatteryHistoryViewModel(
            new StubAdapter(report));

        await viewModel.RefreshAsync();

        Assert.Same(report, viewModel.Report);
        Assert.Equal(report.GeneratedAtUtc, viewModel.GeneratedAtUtc);
        Assert.Same(report.Status, viewModel.Status);
        Assert.Equal(87.5, viewModel.HealthPercent);
        Assert.Single(viewModel.Intervals);
        Assert.False(viewModel.IsLoading);
        Assert.Null(viewModel.Error);
    }

    [Fact]
    public async Task Refresh_FailurePreservesLastConfirmedReportAndShowsError()
    {
        WindowsBatteryReport report = CreateReport();
        var adapter = new StubAdapter(report);
        await using var viewModel = new WindowsBatteryHistoryViewModel(adapter);
        await viewModel.RefreshAsync();
        adapter.Failure = new IOException("Battery history is unavailable.");

        await viewModel.RefreshAsync();

        Assert.Same(report, viewModel.Report);
        Assert.Equal("Battery history is unavailable.", viewModel.Error);
        Assert.False(viewModel.IsLoading);
    }

    [Fact]
    public async Task Dispose_CancelsAndDrainsDirectRefreshBeforeReleasingResources()
    {
        var adapter = new CancellableAdapter();
        var viewModel = new WindowsBatteryHistoryViewModel(adapter);
        Task refresh = viewModel.RefreshAsync();
        await adapter.Started.Task;
        await viewModel.DisposeAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => refresh);
        Assert.False(viewModel.IsLoading);
    }

    private sealed class CancellableAdapter : IWindowsBatteryReportAdapter
    {
        internal TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async Task<WindowsBatteryReport> ReadAsync(CancellationToken cancellationToken)
        {
            Started.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            throw new InvalidOperationException("unreachable");
        }
    }

    private static WindowsBatteryReport CreateReport()
    {
        DateTimeOffset end = new(2026, 10, 22, 12, 0, 0, TimeSpan.Zero);
        DateTimeOffset start = end.AddHours(-1);
        return new WindowsBatteryReport(
            WindowsBatteryReportSource.WindowsPowerCfg,
            end,
            start,
            end,
            new WindowsBatteryStatus(80_000, 70_000, 87.5, 42),
            [
                new WindowsBatteryReportInterval(
                    start,
                    end,
                    WindowsBatteryPowerSource.Battery,
                    WindowsBatteryActivity.Active,
                    60_000,
                    70_000,
                    10_000,
                    85.7),
            ]);
    }

    private sealed class StubAdapter(WindowsBatteryReport report)
        : IWindowsBatteryReportAdapter
    {
        internal Exception? Failure { get; set; }

        public Task<WindowsBatteryReport> ReadAsync(CancellationToken cancellationToken) =>
            Failure is null
                ? Task.FromResult(report)
                : Task.FromException<WindowsBatteryReport>(Failure);
    }
}
