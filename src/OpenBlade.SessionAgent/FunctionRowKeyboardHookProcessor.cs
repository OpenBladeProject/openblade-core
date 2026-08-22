using OpenBlade.Contracts;
using OpenBlade.Tray;

namespace OpenBlade.SessionAgent;

internal static class FunctionRowKeyboardHookProcessor
{
    public static bool Process(
        RazerBladeFunctionRowInputState inputState,
        RazerBladeInputDeviceKind deviceKind,
        uint scanCode,
        bool keyDown,
        bool functionKeyPressed,
        FunctionKeyPrimaryMode? primaryMode,
        bool driverModeActive,
        Action<FunctionRowInputEvent> publish)
    {
        ArgumentNullException.ThrowIfNull(inputState);
        ArgumentNullException.ThrowIfNull(publish);

        bool suppress = inputState.Process(
            deviceKind,
            scanCode,
            keyDown,
            functionKeyPressed,
            primaryMode,
            driverModeActive,
            out FunctionRowInputEvent? inputEvent);
        if (inputEvent is { } triggered)
        {
            publish(triggered);
        }

        return suppress;
    }
}
