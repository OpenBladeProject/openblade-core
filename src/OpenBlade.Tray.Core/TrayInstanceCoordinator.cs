namespace OpenBlade.Tray;

public sealed record TrayInstanceEndpoint(string InstanceEventName)
{
    public static TrayInstanceEndpoint Create(string userSid)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(userSid);
        return new TrayInstanceEndpoint(
            $@"Local\OpenBlade.Tray.Instance.{userSid}");
    }
}

public sealed class TrayInstanceCoordinator : IAsyncDisposable
{
    private static readonly TimeSpan UnregistrationTimeout = TimeSpan.FromSeconds(2);
    private readonly EventWaitHandle instanceEvent;
    private readonly RegisteredWaitHandle activationRegistration;
    private int disposed;

    private TrayInstanceCoordinator(EventWaitHandle instanceEvent, Action activate)
    {
        this.instanceEvent = instanceEvent;
        activationRegistration = ThreadPool.RegisterWaitForSingleObject(
            instanceEvent,
            (_, _) =>
            {
                if (Volatile.Read(ref disposed) == 0)
                {
                    activate();
                }
            },
            null,
            Timeout.Infinite,
            executeOnlyOnce: false);
    }

    public static bool TryStart(
        TrayInstanceEndpoint endpoint,
        Action activate,
        out TrayInstanceCoordinator? coordinator)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        ArgumentNullException.ThrowIfNull(activate);

        EventWaitHandle instanceEvent = new(
            false,
            EventResetMode.AutoReset,
            endpoint.InstanceEventName,
            out bool createdNew);
        if (!createdNew)
        {
            try
            {
                instanceEvent.Set();
            }
            finally
            {
                instanceEvent.Dispose();
            }

            coordinator = null;
            return false;
        }

        try
        {
            coordinator = new TrayInstanceCoordinator(instanceEvent, activate);
            return true;
        }
        catch
        {
            instanceEvent.Dispose();
            throw;
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref disposed, 1) == 0)
        {
            using var callbacksCompleted = new EventWaitHandle(
                false,
                EventResetMode.ManualReset);
            if (activationRegistration.Unregister(callbacksCompleted))
            {
                await Task.Run(
                    () => callbacksCompleted.WaitOne(UnregistrationTimeout))
                    .ConfigureAwait(false);
            }
            instanceEvent.Dispose();
        }
    }
}
