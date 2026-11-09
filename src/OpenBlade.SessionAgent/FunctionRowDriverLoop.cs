using OpenBlade.Contracts;
using OpenBlade.Tray;

namespace OpenBlade.SessionAgent;

/// <summary>Bounded function-row drain rules used by the sole combined driver worker.</summary>
internal static class FunctionRowDriverLoop
{
    internal static readonly TimeSpan StopDrainLimit = TimeSpan.FromSeconds(2);

    internal static bool Pump(FunctionRowDriverInputProcessor processor, Func<SnapTapKeyStroke?> read,
        Func<bool> shouldStop, Func<bool> functionKeyPressed, Func<FunctionKeyPrimaryMode?> readPrimaryMode,
        Func<KeyboardDriverModeObservation> readDriverMode, Func<RazerBladeInputDeviceKind> readDeviceKind,
        RazerBladeInputDeviceKind admittedDevice, TimeProvider? timeProvider = null)
    {
        TimeProvider time = timeProvider ?? TimeProvider.System;
        long? stopStarted = null;
        while (!processor.Faulted)
        {
            if (shouldStop() && stopStarted is null) { stopStarted = time.GetTimestamp(); }
            if (stopStarted is { } start)
            {
                if (!processor.HasTrackedKeys) { return true; }
                if (time.GetElapsedTime(start) >= StopDrainLimit) { return false; }
            }
            if (readDeviceKind() != admittedDevice) { return !processor.HasTrackedKeys; }
            if (read() is { } stroke)
            {
                processor.Handle(stroke, admittedDevice, functionKeyPressed(), readPrimaryMode(),
                    readDriverMode().AllowsFunctionRowInterception, stopStarted is not null);
            }
        }
        return !processor.HasTrackedKeys && !processor.OutputUncertain;
    }

}
