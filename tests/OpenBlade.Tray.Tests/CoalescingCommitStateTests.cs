using OpenBlade.Tray;

namespace OpenBlade.Tray.Tests;

public sealed class CoalescingCommitStateTests
{
    [Fact]
    public void ValuesBeforeStartCoalesceToLatest()
    {
        using var state = new CoalescingCommitState<int>();

        Assert.True(state.Schedule(50));
        Assert.True(state.Schedule(65));

        Assert.Equal(
            CoalescingCommitBeginStatus.Started,
            state.TryBegin(out int value));
        Assert.Equal(65, value);
        Assert.False(state.Complete());
        Assert.Equal(
            CoalescingCommitBeginStatus.NoWork,
            state.TryBegin(out _));
    }

    [Fact]
    public void ValueScheduledDuringCommitBecomesSingleFollowUp()
    {
        using var state = new CoalescingCommitState<string>();
        state.Schedule("first");
        Assert.Equal(
            CoalescingCommitBeginStatus.Started,
            state.TryBegin(out string? first));
        Assert.Equal("first", first);

        state.Schedule("superseded");
        state.Schedule("latest");
        Assert.Equal(
            CoalescingCommitBeginStatus.InProgress,
            state.TryBegin(out _));

        Assert.True(state.Complete());
        Assert.Equal(
            CoalescingCommitBeginStatus.Started,
            state.TryBegin(out string? followUp));
        Assert.Equal("latest", followUp);
        Assert.False(state.Complete());
    }

    [Fact]
    public void ClearPendingDoesNotCancelInProgressCommit()
    {
        using var state = new CoalescingCommitState<int>();
        state.Schedule(1);
        Assert.Equal(
            CoalescingCommitBeginStatus.Started,
            state.TryBegin(out _));
        state.Schedule(2);

        state.ClearPending();

        Assert.False(state.Complete());
        Assert.Equal(
            CoalescingCommitBeginStatus.NoWork,
            state.TryBegin(out _));
    }

    [Fact]
    public void PendingValueCanBeReadWithoutConsumingIt()
    {
        using var state = new CoalescingCommitState<int>();
        state.Schedule(70);

        Assert.True(state.TryPeekPending(out int value));
        Assert.Equal(70, value);
        Assert.Equal(
            CoalescingCommitBeginStatus.Started,
            state.TryBegin(out int started));
        Assert.Equal(70, started);
        Assert.False(state.Complete());
    }

    [Fact]
    public void DisposeRejectsAndDiscardsPendingWork()
    {
        var state = new CoalescingCommitState<int>();
        state.Schedule(80);

        state.Dispose();

        Assert.False(state.Schedule(90));
        Assert.False(state.TryPeekPending(out _));
        Assert.Equal(
            CoalescingCommitBeginStatus.NoWork,
            state.TryBegin(out _));
    }

    [Fact]
    public void DisposeDuringCommitAllowsSafeCompletionWithoutFollowUp()
    {
        var state = new CoalescingCommitState<int>();
        state.Schedule(80);
        Assert.Equal(
            CoalescingCommitBeginStatus.Started,
            state.TryBegin(out _));
        state.Schedule(75);

        state.Dispose();

        Assert.False(state.Complete());
    }

    [Fact]
    public void CompleteWithoutStartedCommitIsRejected()
    {
        using var state = new CoalescingCommitState<int>();

        Assert.Throws<InvalidOperationException>(() => state.Complete());
    }
}
