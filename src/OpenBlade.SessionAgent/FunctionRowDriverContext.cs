using System.Diagnostics;
using System.Security.Principal;
using OpenBlade.Contracts;
using OpenBlade.Session;
using OpenBlade.Tray;

namespace OpenBlade.SessionAgent;

/// <summary>Exact Fn metadata and the existing typed action pump inside the sole driver worker.</summary>
internal sealed class FunctionRowDriverContext : IDisposable, IReactiveKeyboardSourceFactory
{
    private readonly CancellationTokenSource lifetime = new();
    private readonly ServiceClient statusClient = new();
    private readonly ServiceClient actionClient = new();
    private readonly IDisplayBrightnessOsd osd;
    private readonly ServiceReactiveLightingLoop observer;
    private readonly FunctionRowInputPump pump;
    private readonly FunctionRowDriverActionGuard actions;
    private readonly Task observation;
    private readonly RazerBladeInputDeviceKind expectedDevice;
    private ReactiveKeyboardSourceOptions? options;
    private WindowsReactiveKeyboardSource? source;
    private int failed;
    private int disposed;

    internal FunctionRowDriverContext(RazerSnapTapDriverProfile profile)
    {
        expectedDevice = profile.ProductId switch
        {
            "02E0" => RazerBladeInputDeviceKind.Rz09_0581,
            "02C6" => RazerBladeInputDeviceKind.Rz09_0528,
            _ => throw new ArgumentOutOfRangeException(nameof(profile)),
        };
        using WindowsIdentity identity = WindowsIdentity.GetCurrent();
        using Process process = Process.GetCurrentProcess();
        osd = WindowsTrayBrightnessOsdNotifier.CreateBestEffort(
            SessionAgentEventEndpoint.Create(identity.User!, process.SessionId), Log);
        var executor = new FunctionRowActionExecutor(actionClient, new WindowsSpecialKeyInputSender(),
            new WmiDisplayBrightnessController(), osd);
        actions = new(executor.ExecuteAsync, executor.ResetAsync);
        pump = new(actions.ExecuteAsync, actions.ResetAsync, errorHandler: Log);
        observer = new(statusClient, this, new ServiceReactiveLightingLoopOptions(ErrorHandler: Log,
            ReadFunctionRowBackend: () => KeyboardInputBackend.Unknown));
        observation = Task.Run(() => observer.RunAsync(lifetime.Token));
    }

    internal bool Failed => Volatile.Read(ref failed) != 0 || actions.Failed
        || observation.IsFaulted || Volatile.Read(ref source)?.Completion.IsCompleted == true;
    internal bool FunctionKeyPressed => Volatile.Read(ref source)?.FunctionKeyPressed == true;
    internal bool FunctionKeyKnown => Volatile.Read(ref source)?.FunctionKeyKnown == true;
    internal bool? FunctionKeyState => Volatile.Read(ref source)?.FunctionKeyState;
    internal FunctionKeyPrimaryMode? PrimaryMode => Volatile.Read(ref options)?.ReadFunctionKeyPrimaryMode();
    internal KeyboardDriverModeObservation DriverMode =>
        Volatile.Read(ref options)?.ReadDriverModeObservation() ?? KeyboardDriverModeObservation.Unknown;
    internal RazerBladeInputDeviceKind DeviceKind =>
        Volatile.Read(ref options)?.ReadVerifiedInputDeviceKind() ?? default;
    internal bool Ready => Volatile.Read(ref source) is not null && !Failed && DriverMode.AllowsFunctionRowInterception
        && PrimaryMode is not null && DeviceKind == expectedDevice;

    public IReactiveKeyboardSource Create(ReactiveKeyboardSourceOptions value)
    {
        Volatile.Write(ref options, value);
        // This process observes exact Fn reports only. Main-agent lighting,
        // shortcuts, gaming mode and the Windows F-row hook keep their own owner.
        var metadata = new WindowsReactiveKeyboardSource(value with
        {
            ObserveOnly = true,
            PublishPulse = _ => { },
            PublishPerformanceShortcut = _ => { },
            PublishSpecialKey = _ => { },
            ShouldOverrideSpecialKey = _ => false,
            PublishDefaultLaptopKey = _ => { },
            PublishFunctionRowInput = _ => { },
            ReadFunctionRowBackend = () => KeyboardInputBackend.Unknown,
            ReadGamingModeKeyPolicy = () => new(false, false),
            IsSpecialKeyInputAdmitted = _ => false,
            PublishBlade18FnShortcut = _ => { },
            IsBlade18FnInputAdmitted = () => false,
            ResetFunctionRowInput = pump.Reset,
        });
        Volatile.Write(ref source, metadata);
        return metadata;
    }

    internal void Publish(FunctionRowInputEvent input)
    {
        if (!pump.TryEnqueue(input))
        {
            Volatile.Write(ref failed, 1);
            throw new InvalidOperationException("The function-row input queue cannot accept this edge.");
        }
    }

    internal void Reset() => pump.Reset();

    internal bool Stop()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) { return Volatile.Read(ref failed) == 0 && !actions.Failed; }
        lifetime.Cancel();
        try
        {
            try { observation.WaitAsync(TimeSpan.FromSeconds(1)).GetAwaiter().GetResult(); }
            catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
            observer.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(1)).GetAwaiter().GetResult();
            // Dispose waits for the pump's final typed native-brightness reset.
            // Errors latch recovery instead of treating queue closure as restoration.
            pump.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(1)).GetAwaiter().GetResult();
            actionClient.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(1)).GetAwaiter().GetResult();
            statusClient.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(1)).GetAwaiter().GetResult();
        }
        catch (Exception exception)
        {
            Volatile.Write(ref failed, 1);
            Log(exception);
        }
        osd.Dispose();
        lifetime.Dispose();
        return Volatile.Read(ref failed) == 0 && !actions.Failed;
    }

    public void Dispose() => Stop();

    private static void Log(Exception exception) => DiagnosticTrace.TraceException(
        "OpenBlade.SessionAgent.FunctionRowDriverContext", "Observe", exception);
}

/// <summary>Latches action failure before the shared queue can execute another new press.</summary>
internal sealed class FunctionRowDriverActionGuard(
    Func<FunctionRowInputEvent, CancellationToken, Task> execute,
    Func<CancellationToken, Task> reset)
{
    private int failed;
    internal bool Failed => Volatile.Read(ref failed) != 0;

    internal async Task ExecuteAsync(FunctionRowInputEvent input, CancellationToken cancellationToken)
    {
        if (Failed && input.Phase != FunctionRowInputPhase.Up) { return; }
        try { await execute(input, cancellationToken).ConfigureAwait(false); }
        catch
        {
            Volatile.Write(ref failed, 1);
            throw;
        }
    }

    internal async Task ResetAsync(CancellationToken cancellationToken)
    {
        try { await reset(cancellationToken).ConfigureAwait(false); }
        catch
        {
            Volatile.Write(ref failed, 1);
            throw;
        }
    }
}
