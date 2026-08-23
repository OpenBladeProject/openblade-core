using OpenBlade.Contracts;
using OpenBlade.Tray;

namespace OpenBlade.SessionAgent;

internal readonly record struct FunctionRowHookRecoveryResult(
    bool ReinstallHook,
    FunctionRowInputEvent? ReleaseEvent);

internal static class FunctionRowHookRecovery
{
    public static FunctionRowHookRecoveryResult EvaluateTargetPacket(
        RazerBladeFunctionRowInputState inputState,
        RazerBladeInputDeviceKind deviceKind,
        bool observedByHook,
        uint scanCode,
        bool keyDown)
    {
        ArgumentNullException.ThrowIfNull(inputState);
        if (observedByHook
            || !RazerBladeFunctionRowMediaInput.SupportsDevice(deviceKind)
            || !inputState.ReconcilePassedThroughEdge(
                scanCode,
                keyDown,
                out FunctionRowInputEvent? releaseEvent))
        {
            return default;
        }

        return new(ReinstallHook: true, ReleaseEvent: releaseEvent);
    }
}
