namespace OpenBlade.SessionAgent;

/// <summary>Closed, source-reviewed keyboard selector sets; never arbitrary scan codes.</summary>
internal enum RazerKeyboardHookPurpose
{
    SnapTap,
    FunctionRow,
    Combined,
}

internal static class RazerKeyboardHookScope
{
    internal static RazerKeyboardHookPurpose Purpose(OpenBlade.Tray.KeyboardDriverFeatures features) => features switch
    {
        OpenBlade.Tray.KeyboardDriverFeatures.SnapTap => RazerKeyboardHookPurpose.SnapTap,
        OpenBlade.Tray.KeyboardDriverFeatures.FunctionRow => RazerKeyboardHookPurpose.FunctionRow,
        OpenBlade.Tray.KeyboardDriverFeatures.SnapTap | OpenBlade.Tray.KeyboardDriverFeatures.FunctionRow => RazerKeyboardHookPurpose.Combined,
        _ => throw new ArgumentOutOfRangeException(nameof(features)),
    };
}

/// <summary>Serializes global hook-table mutations across feature workers.</summary>
internal sealed class RazerKeyboardHookMutationGate : IDisposable
{
    private readonly Mutex mutex;

    private RazerKeyboardHookMutationGate(Mutex mutex) => this.mutex = mutex;

    internal static RazerKeyboardHookMutationGate Enter()
    {
        var mutex = new Mutex(false, @"Local\OpenBlade.KeyboardDriver.HookMutation");
        try
        {
            if (!mutex.WaitOne(TimeSpan.FromSeconds(5)))
            {
                throw new TimeoutException("The keyboard hook table is busy.");
            }
            return new(mutex);
        }
        catch (AbandonedMutexException)
        {
            // The interrupted owner may have submitted a mutation. This attempt
            // must stop; ownership journals and a later independent read decide recovery.
            mutex.ReleaseMutex();
            mutex.Dispose();
            throw;
        }
        catch
        {
            mutex.Dispose();
            throw;
        }
    }

    public void Dispose()
    {
        mutex.ReleaseMutex();
        mutex.Dispose();
    }
}
