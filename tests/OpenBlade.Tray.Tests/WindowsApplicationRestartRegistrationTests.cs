namespace OpenBlade.Tray.Tests;

public sealed class WindowsApplicationRestartRegistrationTests
{
    [Fact]
    public void NativeRegistrationCanRoundTripForTheCurrentProcess()
    {
        var registration = new WindowsApplicationRestartRegistration();

        Assert.True(registration.TryRegister());
        Assert.True(registration.TryUnregister());
    }

    [Fact]
    public void RegisterUsesDedicatedArgumentAndAllowsEveryRestartReason()
    {
        string? commandLine = null;
        uint? flags = null;
        int calls = 0;
        var registration = new WindowsApplicationRestartRegistration(
            (arguments, requestedFlags) =>
            {
                calls++;
                commandLine = arguments;
                flags = requestedFlags;
                return 0;
            },
            () => 0);

        Assert.True(registration.TryRegister());
        Assert.True(registration.TryRegister());

        Assert.Equal(1, calls);
        Assert.Equal(
            WindowsApplicationRestartRegistration.RestartArgument,
            commandLine);
        Assert.Equal(0U, flags);
    }

    [Fact]
    public void FailedRegistrationIsBestEffortAndCanBeRetried()
    {
        int calls = 0;
        var registration = new WindowsApplicationRestartRegistration(
            (_, _) =>
            {
                calls++;
                return calls == 1 ? unchecked((int)0x80004005) : 0;
            },
            () => 0);

        Assert.False(registration.TryRegister());
        Assert.True(registration.TryRegister());

        Assert.Equal(2, calls);
    }

    [Fact]
    public void NativeExceptionsDoNotEscapeStartupOrShutdown()
    {
        var registration = new WindowsApplicationRestartRegistration(
            (_, _) => throw new EntryPointNotFoundException(),
            () => throw new EntryPointNotFoundException());

        Assert.False(registration.TryRegister());

        var registered = new WindowsApplicationRestartRegistration(
            (_, _) => 0,
            () => throw new EntryPointNotFoundException());
        Assert.True(registered.TryRegister());
        Assert.False(registered.TryUnregister());
    }

    [Fact]
    public void UnregisterIsIdempotentAndRetriesAfterNativeFailure()
    {
        int unregisterCalls = 0;
        var registration = new WindowsApplicationRestartRegistration(
            (_, _) => 0,
            () =>
            {
                unregisterCalls++;
                return unregisterCalls == 1 ? unchecked((int)0x80004005) : 0;
            });

        Assert.True(registration.TryUnregister());
        Assert.True(registration.TryRegister());
        Assert.False(registration.TryUnregister());
        Assert.True(registration.TryUnregister());
        Assert.True(registration.TryUnregister());

        Assert.Equal(2, unregisterCalls);
    }
}
