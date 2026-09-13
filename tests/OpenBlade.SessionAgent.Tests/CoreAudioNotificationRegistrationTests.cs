using OpenBlade.SessionAgent;

namespace OpenBlade.SessionAgent.Tests;

public sealed class CoreAudioNotificationRegistrationTests
{
    [Theory]
    [InlineData(FailurePoint.Creation)]
    [InlineData(FailurePoint.Cast)]
    [InlineData(FailurePoint.Registration)]
    [InlineData(FailurePoint.InitialBinding)]
    public void Acquire_FailureReleasesOnlySuccessfullyAcquiredState(
        FailurePoint failurePoint)
    {
        object nativeObject = new();
        var cleanup = new List<string>();
        var failure = new InvalidOperationException("Injected acquisition failure.");

        Exception exception = Assert.Throws<InvalidOperationException>(() =>
            CoreAudioNotificationRegistration.Acquire(
                create: () => failurePoint == FailurePoint.Creation
                    ? throw failure
                    : nativeObject,
                validate: value => failurePoint == FailurePoint.Cast
                    ? throw failure
                    : value,
                register: _ =>
                {
                    if (failurePoint == FailurePoint.Registration)
                    {
                        throw failure;
                    }
                },
                unregister: _ => cleanup.Add("unregister"),
                initialize: _ =>
                {
                    if (failurePoint == FailurePoint.InitialBinding)
                    {
                        throw failure;
                    }
                },
                releaseBindings: () => cleanup.Add("bindings"),
                releaseManagedState: () => cleanup.Add("managed"),
                release: value =>
                {
                    Assert.Same(nativeObject, value);
                    cleanup.Add("release");
                }));

        Assert.Same(failure, exception);
        Assert.Equal(ExpectedCleanup(failurePoint), cleanup);
    }

    [Fact]
    public void Dispose_UnregistersSuccessfulRegistrationBeforeReleaseExactlyOnce()
    {
        object nativeObject = new();
        var cleanup = new List<string>();
        CoreAudioNotificationRegistration registration =
            CoreAudioNotificationRegistration.Acquire(
                create: () => nativeObject,
                validate: value => value,
                register: _ => { },
                unregister: _ => cleanup.Add("unregister"),
                initialize: _ => { },
                releaseBindings: () => cleanup.Add("bindings"),
                releaseManagedState: () => cleanup.Add("managed"),
                release: _ => cleanup.Add("release"));

        Assert.Empty(cleanup);

        registration.Dispose();
        registration.Dispose();

        Assert.Equal(["unregister", "release"], cleanup);
    }

    private static string[] ExpectedCleanup(FailurePoint failurePoint) =>
        failurePoint switch
        {
            FailurePoint.Creation => ["managed"],
            FailurePoint.Cast => ["release", "managed"],
            FailurePoint.Registration => ["release", "managed"],
            FailurePoint.InitialBinding =>
                ["bindings", "unregister", "release", "managed"],
            _ => throw new ArgumentOutOfRangeException(nameof(failurePoint)),
        };

    public enum FailurePoint
    {
        Creation,
        Cast,
        Registration,
        InitialBinding,
    }
}
