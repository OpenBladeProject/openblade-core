using System.Threading.Channels;
using OpenBlade.Contracts;
using OpenBlade.Tray;

namespace OpenBlade.SessionAgent;

/// <summary>Never performs file I/O on the keyboard-hook thread.</summary>
internal sealed class SnapTapStatusPublisher : IDisposable
{
    private readonly Channel<SnapTapRuntimeStatus> channel = Channel.CreateBounded<SnapTapRuntimeStatus>(
        new BoundedChannelOptions(1)
        {
            SingleReader = true,
            SingleWriter = true,
            FullMode = BoundedChannelFullMode.DropOldest,
        });
    private readonly Task writer;
    private RazerSnapTapDriverProfile? profile;
    private readonly KeyboardInputBackend backend;
    private int failed;
    private SnapTapRuntimeState? lastState;
    private bool? lastFunctionKeyKnown;

    internal SnapTapStatusPublisher(RazerSnapTapDriverProfile? profile = null,
        KeyboardInputBackend backend = KeyboardInputBackend.RazerDriver)
    {
        this.profile = profile;
        this.backend = backend.Normalize();
        writer = Task.Run(async () =>
        {
            try
            {
                await foreach (SnapTapRuntimeStatus status in channel.Reader.ReadAllAsync()
                    .ConfigureAwait(false))
                {
                    if (backend == KeyboardInputBackend.RazerDriver) { KeyboardDriverStatusStore.Write(status); }
                    else { SnapTapStatusStore.Write(status); }
                }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or System.Security.SecurityException)
            {
                DiagnosticTrace.TraceException(
                    "OpenBlade.SessionAgent.SnapTapStatusPublisher",
                    "WriteStatus",
                    exception);
                Volatile.Write(ref failed, 1);
            }
        });
    }

    internal bool Failed => Volatile.Read(ref failed) != 0;

    internal void SetProfile(RazerSnapTapDriverProfile value) =>
        profile = value ?? throw new ArgumentNullException(nameof(value));

    internal void Publish(SnapTapRuntimeState state, DiagnosticFailure? failure = null)
    {
        bool? functionKeyKnown = backend == KeyboardInputBackend.RazerDriver ? KeyboardDriverStatusStore.FunctionKeyKnown : null;
        if (lastState == state && lastFunctionKeyKnown == functionKeyKnown && failure is null)
        {
            return;
        }
        lastState = state;
        lastFunctionKeyKnown = functionKeyKnown;
        channel.Writer.TryWrite(CreateStatus(state, failure, profile, backend));
    }

    internal static SnapTapRuntimeStatus CreateStatus(SnapTapRuntimeState state, DiagnosticFailure? failure,
        RazerSnapTapDriverProfile? profile, KeyboardInputBackend backend) => new(
            state,
            DateTimeOffset.UtcNow,
            Environment.ProcessId,
            backend == KeyboardInputBackend.RazerDriver ? profile?.ProductName : null,
            backend == KeyboardInputBackend.RazerDriver && profile is { PhysicallyValidated: false },
            failure)
        { Backend = backend.Normalize() };

    public void Dispose()
    {
        channel.Writer.TryComplete();
        // The hook is already removed before waiting for the final status flush.
        try
        {
            if (!writer.Wait(TimeSpan.FromSeconds(2)))
            {
                Volatile.Write(ref failed, 1);
            }
        }
        catch (AggregateException exception)
        {
            DiagnosticTrace.TraceException(
                "OpenBlade.SessionAgent.SnapTapStatusPublisher",
                "StopWriter",
                exception);
            Volatile.Write(ref failed, 1);
        }
    }
}
