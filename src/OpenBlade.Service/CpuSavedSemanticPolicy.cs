using System.Buffers.Binary;
using OpenBlade.Contracts;

namespace OpenBlade.Service;

// Source-semantic checks only. A match establishes neither reviewed capture
// admission nor identity, provider ACK, safety, ownership or write authority.
internal static class CpuSavedSemanticPolicy
{
    internal static bool MatchesEncoding(CpuSavedTuningCommand? command, ReadOnlySpan<byte> encoded)
    {
        if (!HasSupportedShape(command) || encoded.Length != 8) { return false; }
        uint selector = command!.Kind == CpuSavedTuningCommandKind.CurveOptimizer ? 0x5000Au : 0x50001u;
        uint argument = command.Kind == CpuSavedTuningCommandKind.CurveOptimizer
            ? 0x11FF0000u | (uint)-command.Value : (uint)command.Value * 1000;
        return BinaryPrimitives.ReadUInt32LittleEndian(encoded) == selector
            && BinaryPrimitives.ReadUInt32LittleEndian(encoded[4..]) == argument;
    }

    internal static bool MatchesKnownAnswers() =>
        Known(CpuSavedTuningCommandKind.CurveOptimizer, 0, "0A0005000000FF11")
        && Known(CpuSavedTuningCommandKind.CurveOptimizer, -1, "0A0005000100FF11")
        && Known(CpuSavedTuningCommandKind.CurveOptimizer, -30, "0A0005001E00FF11")
        && Known(CpuSavedTuningCommandKind.PowerLimit, 45, "01000500C8AF0000")
        && Known(CpuSavedTuningCommandKind.PowerLimit, 95, "0100050018730100")
        && Known(CpuSavedTuningCommandKind.ResetPowerLimit, 0, "0100050000000000");

    // Both originals must be exact, complete mirrors. The caller supplies the
    // proposed plan; both it and the actual planner must match the independent
    // semantics below. Empty/no-change plans establish no ownership.
    internal static bool MatchesPlan(
        ReadOnlySpan<byte> current, ReadOnlySpan<byte> last, CpuTuningSettings? settings,
        IReadOnlyList<CpuSavedTuningStep>? steps)
    {
        if (current.Length != 800 || !current.SequenceEqual(last)
            || settings is null || !HasSupportedSettings(settings)
            || steps is null || steps.Count > 2 || !MatchesKnownAnswers()) { return false; }
        CpuSavedTuningStep[]? actual = CpuSavedTuningPlan.Create(current.ToArray(), settings);
        if (actual is null || actual.Length != steps.Count) { return false; }
        for (int index = 0; index < actual.Length; index++)
        {
            CpuSavedTuningStep? candidate = steps[index];
            if (candidate is null || candidate.Before is null || candidate.After is null
                || actual[index].Command != candidate.Command || actual[index].RestoreCommand != candidate.RestoreCommand
                || !actual[index].Before.AsSpan().SequenceEqual(candidate.Before)
                || !actual[index].After.AsSpan().SequenceEqual(candidate.After)) { return false; }
        }

        int next = 0;
        byte[] expectedBefore = current.ToArray();
        if (settings.PowerLimitWatts is { } watts
            && !CheckRequested(new(CpuSavedTuningCommandKind.PowerLimit, watts))) { return false; }
        if (settings.CurveOptimizer is { } curve
            && !CheckRequested(new(CpuSavedTuningCommandKind.CurveOptimizer, curve.Enabled ? curve.Value : 0))) { return false; }
        return next == steps.Count;

        bool CheckRequested(CpuSavedTuningCommand command)
        {
            // Independent identity transition check, rather than trusting the
            // planner's decision to omit a command as already satisfied.
            if (MatchesTransition(expectedBefore, expectedBefore, command)) { return true; }
            if (next >= steps.Count) { return false; }
            CpuSavedTuningStep step = steps[next++];
            if (step.Command != command || !step.Before.AsSpan().SequenceEqual(expectedBefore)
                || !MatchesStep(step)) { return false; }
            expectedBefore = step.After;
            return true;
        }
    }

    // A pending owned edit needs its immediate inverse checked separately
    // from the plan restoring the immutable first original pair.
    internal static bool MatchesStep(CpuSavedTuningStep? step) => step is
    { Before.Length: 800, After.Length: 800, Command: not null, RestoreCommand: not null }
        && step.Command.Kind is CpuSavedTuningCommandKind.CurveOptimizer or CpuSavedTuningCommandKind.PowerLimit
        && HasSupportedShape(step.Command) && HasSupportedShape(step.RestoreCommand)
        && !step.Before.AsSpan().SequenceEqual(step.After)
        && MatchesEncoding(step.Command, step.Command.Encode())
        && MatchesTransition(step.Before, step.After, step.Command)
        && CpuSavedTuningPlan.Simulate(step.Before, step.Command).AsSpan().SequenceEqual(step.After)
        && MatchesOriginalReverse(step.Before, step.Command.Kind, step.RestoreCommand)
        && MatchesEncoding(step.RestoreCommand, step.RestoreCommand.Encode())
        && MatchesTransition(step.After, step.Before, step.RestoreCommand)
        && CpuSavedTuningPlan.Simulate(step.After, step.RestoreCommand).AsSpan().SequenceEqual(step.Before);

    private static bool Known(CpuSavedTuningCommandKind kind, int value, string hex)
    {
        var command = new CpuSavedTuningCommand(kind, value);
        byte[] actual = command.Encode();
        return actual.AsSpan().SequenceEqual(Convert.FromHexString(hex)) && MatchesEncoding(command, actual);
    }

    private static bool HasSupportedShape(CpuSavedTuningCommand? command) => command switch
    {
        { Kind: CpuSavedTuningCommandKind.CurveOptimizer, Value: >= -30 and <= 0 } => true,
        { Kind: CpuSavedTuningCommandKind.PowerLimit, Value: >= 45 and <= 95 } => true,
        { Kind: CpuSavedTuningCommandKind.ResetPowerLimit, Value: 0 } => true,
        _ => false,
    };

    private static bool HasSupportedSettings(CpuTuningSettings settings) => settings.IsConfigured
        && (settings.PowerLimitWatts is null or >= 45 and <= 95)
        && (settings.CurveOptimizer is null || settings.CurveOptimizer.Value is >= -30 and <= -1);

    private static bool MatchesOriginalReverse(ReadOnlySpan<byte> before,
        CpuSavedTuningCommandKind forwardKind, CpuSavedTuningCommand? reverse)
    {
        if (!HasSupportedShape(reverse)) { return false; }
        if (forwardKind == CpuSavedTuningCommandKind.CurveOptimizer)
        {
            ushort magnitude = BinaryPrimitives.ReadUInt16LittleEndian(before[0xC2..]);
            return before[0xC0] == 1 && before[0xC1] == 1 && magnitude <= 30
                && reverse == new CpuSavedTuningCommand(CpuSavedTuningCommandKind.CurveOptimizer, -magnitude);
        }
        uint milliwatts = BinaryPrimitives.ReadUInt32LittleEndian(before[0x96..]);
        if (before[0x94] == 2 && before[0x95] == 2
            && milliwatts is >= 45000 and <= 95000 && milliwatts % 1000 == 0)
        {
            return reverse == new CpuSavedTuningCommand(CpuSavedTuningCommandKind.PowerLimit, (int)(milliwatts / 1000));
        }
        // Reset is exclusively the original default's reverse, never an
        // editable zero-watt request or a substitute for a custom original.
        return before[0x94] == 2 && before[0x95] == 0xFF && AllZero(before.Slice(0x96, 20))
            && reverse == new CpuSavedTuningCommand(CpuSavedTuningCommandKind.ResetPowerLimit, 0);
    }

    private static bool MatchesTransition(ReadOnlySpan<byte> before, ReadOnlySpan<byte> after,
        CpuSavedTuningCommand command)
    {
        if (before.Length != 800 || after.Length != 800 || !HasSupportedShape(command)) { return false; }
        bool curve = command.Kind == CpuSavedTuningCommandKind.CurveOptimizer;
        uint argument = curve ? (uint)-command.Value : (uint)command.Value * 1000;
        bool allPowerZero = curve ? AllZero(before.Slice(0x96, 20))
            : argument == 0 && AllZero(before.Slice(0x9A, 16));
        for (int offset = 0; offset < 800; offset++)
        {
            byte expected = offset switch
            {
                0x94 => 2,
                0x95 => allPowerZero ? (byte)0xFF : curve ? before[offset] : (byte)2,
                0xC0 or 0xC1 when curve => 1,
                >= 0xC2 and <= 0xC3 when curve => (byte)(argument >> ((offset - 0xC2) * 8)),
                >= 0x96 and <= 0x99 when !curve => (byte)(argument >> ((offset - 0x96) * 8)),
                _ => before[offset],
            };
            if (after[offset] != expected) { return false; }
        }
        return true;
    }

    private static bool AllZero(ReadOnlySpan<byte> bytes)
    {
        foreach (byte value in bytes) { if (value != 0) { return false; } }
        return true;
    }
}
