namespace OpenBlade.Display.Tests;

public sealed class WindowsDisplaySettingsControllerSchedulingTests
{
    [Fact]
    public void NativeWork_IsScheduledAwayFromCallerSynchronizationContextThread()
    {
        int callerThread = 0;
        int workerThread = 0;
        Exception? failure = null;
        var caller = new Thread(() =>
        {
            try
            {
                SynchronizationContext.SetSynchronizationContext(new SynchronizationContext());
                callerThread = Environment.CurrentManagedThreadId;
                workerThread = WindowsDisplaySettingsController.RunNativeAsync(
                    () => Environment.CurrentManagedThreadId,
                    CancellationToken.None).GetAwaiter().GetResult();
            }
            catch (Exception exception)
            {
                failure = exception;
            }
        });

        caller.Start();
        Assert.True(caller.Join(TimeSpan.FromSeconds(5)));

        Assert.Null(failure);
        Assert.NotEqual(callerThread, workerThread);
    }
}
