namespace OpenBlade.Display.Tests;

public sealed class WindowsDisplayMutationCancellationTests
{
    [Theory]
    [InlineData("refresh")]
    [InlineData("hdr")]
    [InlineData("color")]
    public async Task Setter_CanceledDuringNativePreparationDoesNotSubmit(string setting)
    {
        using var cancellation = new CancellationTokenSource();
        using var release = new ManualResetEventSlim();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int submissions = 0;
        using var controller = CreateController(_ =>
        {
            entered.TrySetResult();
            Assert.True(release.Wait(TimeSpan.FromSeconds(10)));
            return new PreparedDisplayMutation(() =>
            {
                Interlocked.Increment(ref submissions);
                return DisplayOperationResult.Success(Snapshot());
            });
        });
        Task<DisplayOperationResult> operation = SetAsync(controller, setting, cancellation.Token);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await cancellation.CancelAsync();
            Assert.False(operation.IsCompleted);
        }
        finally
        {
            release.Set();
        }
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => operation);
        Assert.Equal(0, submissions);
    }

    [Theory]
    [InlineData("refresh", true)]
    [InlineData("hdr", true)]
    [InlineData("color", true)]
    [InlineData("refresh", false)]
    [InlineData("hdr", false)]
    [InlineData("color", false)]
    public async Task Setter_CancellationAfterSubmissionRetainsOwnershipUntilReconciliation(
        string setting,
        bool confirmed)
    {
        using var cancellation = new CancellationTokenSource();
        using var release = new ManualResetEventSlim();
        var submitted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int preparations = 0;
        bool reconciled = false;
        using var controller = CreateController(_ =>
        {
            int preparation = Interlocked.Increment(ref preparations);
            if (preparation > 1)
            {
                Assert.True(reconciled);
            }
            return new PreparedDisplayMutation(() =>
            {
                if (preparation == 1)
                {
                    submitted.TrySetResult();
                    Assert.True(release.Wait(TimeSpan.FromSeconds(10)));
                    // Synthetic native readback/restoration runs after cancellation.
                    reconciled = true;
                }
                return confirmed
                    ? DisplayOperationResult.Success(Snapshot())
                    : DisplayOperationResult.Failure("readback mismatch; restoration attempted", Snapshot());
            });
        });
        Task<DisplayOperationResult> first = SetAsync(controller, setting, cancellation.Token);
        Task<DisplayOperationResult>? second = null;
        try
        {
            await submitted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await cancellation.CancelAsync();
            second = SetAsync(controller, setting, CancellationToken.None);
            Assert.False(first.IsCompleted);
            Assert.False(second.IsCompleted);
            Assert.Equal(1, Volatile.Read(ref preparations));
        }
        finally
        {
            release.Set();
        }
        DisplayOperationResult result = await first.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(confirmed, result.Succeeded);
        Assert.True(reconciled);
        Assert.NotNull(result.State);
        await second!.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(2, preparations);
    }

    [Fact]
    public async Task Refresh_RejectedNativePreparationReturnsTypedFailureWithPriorSnapshot()
    {
        DisplaySettingsSnapshot before = Snapshot();
        using var controller = CreateController(_ =>
            WindowsDisplaySettingsController.PrepareRefreshAfterSnapshot(
                before,
                () => throw new System.ComponentModel.Win32Exception(87, "validation rejected")));

        DisplayOperationResult result = await controller.SetRefreshAsync("fixed-120");

        Assert.False(result.Succeeded);
        Assert.Same(before, result.State);
        Assert.False(result.SubmissionMayHaveOccurred);
        Assert.Contains("validation rejected", result.Error);
        Assert.DoesNotContain("restor", result.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Refresh_CanceledNativePreparationPropagatesCancellation()
    {
        using var cancellation = new CancellationTokenSource();
        using var controller = CreateController(token =>
            WindowsDisplaySettingsController.PrepareRefreshAfterSnapshot(
                Snapshot(),
                () =>
                {
                    cancellation.Cancel();
                    token.ThrowIfCancellationRequested();
                    throw new InvalidOperationException("unreachable");
                }));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            controller.SetRefreshAsync("fixed-120", cancellation.Token));
    }
    private static WindowsDisplaySettingsController CreateController(
        Func<CancellationToken, PreparedDisplayMutation> prepare) => new(
            (_, token) => prepare(token),
            (_, token) => prepare(token),
            (_, token) => prepare(token));

    private static Task<DisplayOperationResult> SetAsync(
        WindowsDisplaySettingsController controller,
        string setting,
        CancellationToken token) => setting switch
        {
            "refresh" => controller.SetRefreshAsync("fixed-60", token),
            "hdr" => controller.SetHdrAsync(true, token),
            "color" => controller.SetColorProfileAsync("native.icm", token),
            _ => throw new ArgumentOutOfRangeException(nameof(setting)),
        };

    private static DisplaySettingsSnapshot Snapshot() => new(
        true, "Internal", 2560, 1600, "fixed-60", [], true, true, false, null, [], null);
}
