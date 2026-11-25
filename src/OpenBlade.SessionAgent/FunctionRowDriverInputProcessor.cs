using OpenBlade.Contracts;
using OpenBlade.Tray;

namespace OpenBlade.SessionAgent;

/// <summary>Forwards consumed plain F-key edges once and latches media ownership per press.</summary>
internal sealed class FunctionRowDriverInputProcessor(
    Action<SnapTapKeyStroke> send,
    Action<FunctionRowInputEvent> publish,
    Action reset)
{
    private readonly RazerBladeFunctionRowInputState state = new();
    private readonly HashSet<ushort> held = [];
    private readonly Dictionary<ushort, FunctionRowAction> media = [];

    internal bool Faulted { get; private set; }
    internal bool OutputUncertain { get; private set; }
    internal bool HasTrackedKeys => held.Count != 0;
    internal DiagnosticFailure? Failure { get; private set; }

    internal bool Handle(SnapTapKeyStroke stroke, RazerBladeInputDeviceKind deviceKind,
        bool functionKeyPressed, FunctionKeyPrimaryMode? primaryMode, bool driverModeActive,
        bool stopping = false)
    {
        if (Faulted)
        {
            return false;
        }
        if (!RazerSnapTapDriverProtocol.ScanCodes(RazerKeyboardHookPurpose.FunctionRow).Contains(stroke.ScanCode))
        {
            Faulted = true;
            Failure = SnapTapDiagnosticFailure.Create("FunctionRowDriver", "ProcessInput", "InputLoop", "UnexpectedScanCode");
            return false;
        }
        bool wasDown = held.Contains(stroke.ScanCode);
        if (stroke.IsDown) { held.Add(stroke.ScanCode); }
        else { held.Remove(stroke.ScanCode); }
        try
        {
            // The exact 02A0 driver owns these per-device F-key packets. Its
            // Product 672 host map shares the actions, not the Blade 16 firmware
            // Driver-mode requirement or global Windows-hook admission.
            bool suppress;
            if (deviceKind == RazerBladeInputDeviceKind.Rz09_0484)
            {
                if (stroke.IsDown && state.PrepareKeyDown(stroke.ScanCode, wasDown) is { } stale)
                {
                    Publish(stale);
                }
                suppress = state.Process(stroke.ScanCode, stroke.IsDown, functionKeyPressed,
                    primaryMode, driverModeActive && !stopping, out FunctionRowInputEvent? input);
                if (input is { } observed) { Publish(observed); }
            }
            else
            {
                suppress = FunctionRowKeyboardHookProcessor.Process(state, deviceKind,
                    stroke.ScanCode, stroke.IsDown, wasDown, functionKeyPressed, primaryMode,
                    driverModeActive && !stopping, Publish);
            }
            if (!suppress)
            {
                // An intercepted original never reaches Windows automatically.
                // Set the latch before submission so any exception prohibits replay.
                OutputUncertain = true;
                send(stroke);
                OutputUncertain = false;
            }
            return true;
        }
        catch (Exception exception)
        {
            Faulted = true;
            Failure = SnapTapDiagnosticFailure.FromException("FunctionRowDriver", "ProcessInput", "InputLoop",
                "InputFailed", exception, OutputUncertain);
            return false;
        }

        void Publish(FunctionRowInputEvent input)
        {
            if (input.Phase == FunctionRowInputPhase.Up) { media.Remove((ushort)input.ScanCode); }
            else if (input.Phase == FunctionRowInputPhase.Down) { media[(ushort)input.ScanCode] = input.Action; }
            // Retain the original suppression latch during loss of knowledge or
            // stop, but never initiate another media action in those conditions.
            if (input.Phase == FunctionRowInputPhase.Up || (!stopping && driverModeActive))
            {
                publish(input);
            }
        }
    }

    internal void ReleaseMedia()
    {
        try
        {
            foreach (var pair in media)
            {
                publish(new(pair.Key, pair.Value, FunctionRowInputPhase.Up));
            }
        }
        finally
        {
            media.Clear();
            reset();
        }
    }
}
