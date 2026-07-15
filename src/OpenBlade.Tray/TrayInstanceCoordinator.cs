using System.Security.Cryptography;
using System.Text;
using System.IO;

namespace OpenBlade.Tray;

public sealed record TrayInstanceEndpoint(string InstanceEventName)
{
    public static TrayInstanceEndpoint Create(string executablePath, string userSid)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executablePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(userSid);
        if (!Path.IsPathFullyQualified(executablePath))
        {
            throw new ArgumentException(
                "Tray executable path must be fully qualified.",
                nameof(executablePath));
        }

        string identity = $"{Path.GetFullPath(executablePath).ToUpperInvariant()}\n{userSid}";
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(identity));
        return new TrayInstanceEndpoint(
            $@"Local\OpenBlade.Tray.Instance.{Convert.ToHexString(hash.AsSpan(0, 16))}");
    }
}

public sealed class TrayInstanceCoordinator : IAsyncDisposable
{
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

    public ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref disposed, 1) == 0)
        {
            activationRegistration.Unregister(null);
            instanceEvent.Dispose();
        }

        return ValueTask.CompletedTask;
    }
}
