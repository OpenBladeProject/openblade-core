using OpenBlade.SessionAgent;

namespace OpenBlade.SessionAgent.Tests;

public sealed class WindowsKeyboardHookLifetimeTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void FailedRetirementRetainsBothHandlesUntilFinalCleanup(
        bool previousFinallyReleased, bool hasActiveKeys)
    {
        int created = 0;
        bool finalCleanup = false;
        var released = new List<nint>();
        var hooks = new WindowsKeyboardHookLifetime(
            () => ++created,
            handle =>
            {
                released.Add(handle);
                return finalCleanup && (handle == 2 || previousFinallyReleased);
            });
        var tracker = new WindowsFunctionRowOwnershipTracker();
        var ownership = tracker.Begin();
        Assert.True(hooks.InstallOrReplace());
        ownership.ConfirmActive();

        Assert.False(hooks.InstallOrReplace());
        Assert.Equal([(nint)1], released);
        Assert.False(hooks.InstallOrReplace());
        Assert.Equal(2, created);

        finalCleanup = true;
        bool cleanupConfirmed = hooks.ReleaseAll();
        ownership.Complete(cleanupConfirmed, hasActiveKeys);

        Assert.Equal([(nint)1, (nint)2, (nint)1], released);
        Assert.Equal(previousFinallyReleased, cleanupConfirmed);
        Assert.Equal(previousFinallyReleased && !hasActiveKeys,
            ownership.CanTransferOwnership(sourceIsActiveAndIdle: false));
        Assert.Equal(previousFinallyReleased && !hasActiveKeys,
            tracker.HasNoOutstandingSources);
        // Even successful final cleanup cannot restart a faulted lifetime.
        Assert.False(hooks.InstallOrReplace());
        Assert.Equal(2, created);
    }

    [Fact]
    public void SuccessfulRetirementReleasesEachHandleOnce()
    {
        int created = 0;
        var released = new List<nint>();
        var hooks = new WindowsKeyboardHookLifetime(() => ++created, handle =>
        {
            released.Add(handle);
            return true;
        });

        Assert.True(hooks.InstallOrReplace());
        Assert.True(hooks.InstallOrReplace());
        Assert.True(hooks.InstallOrReplace());
        Assert.True(hooks.ReleaseAll());
        Assert.True(hooks.ReleaseAll());

        Assert.Equal([(nint)1, (nint)2, (nint)3], released);
    }

    [Fact]
    public void FailedReplacementCreationKeepsCurrentForCleanup()
    {
        int created = 0;
        nint released = 0;
        var hooks = new WindowsKeyboardHookLifetime(
            () => ++created == 1 ? 1 : throw new InvalidOperationException("create failed"),
            handle =>
            {
                released = handle;
                return true;
            });
        Assert.True(hooks.InstallOrReplace());

        Assert.Throws<InvalidOperationException>(() => hooks.InstallOrReplace());

        Assert.True(hooks.ReleaseAll());
        Assert.Equal((nint)1, released);
    }
}
