namespace OpenBlade.Tray.Tests;

public sealed class SpecialKeyBindingTests
{
    [Fact]
    public void Normalize_FillsMissingSlotsAndKeepsFirstValidBinding()
    {
        IReadOnlyList<SpecialKeyBindingPreference> normalized =
            SpecialKeyBindingDefaults.Normalize(
            [
                new(
                    SpecialKeySlot.M3,
                    new SpecialKeyAction(
                        SpecialKeyActionKind.KeyboardShortcut,
                        " Ctrl+Shift+M ")),
                new(
                    SpecialKeySlot.M3,
                    new SpecialKeyAction(SpecialKeyActionKind.Disabled)),
            ]);

        Assert.Equal(Enum.GetValues<SpecialKeySlot>(), normalized.Select(value => value.Slot));
        Assert.Equal(
            SpecialKeyActionKind.KeyboardShortcut,
            normalized[(int)SpecialKeySlot.M3].Action.Kind);
        Assert.Equal("Ctrl+Shift+M", normalized[(int)SpecialKeySlot.M3].Action.Value);
        Assert.All(
            normalized.Where(value => value.Slot != SpecialKeySlot.M3),
            value => Assert.Equal(SpecialKeyActionKind.Default, value.Action.Kind));
    }

    [Fact]
    public void Normalize_TruncatesUntrustedPreferenceValues()
    {
        var action = new SpecialKeyAction(
            SpecialKeyActionKind.TypeText,
            new string('x', SpecialKeyBindingDefaults.MaximumTextLength + 25));

        SpecialKeyAction normalized = SpecialKeyBindingDefaults.Normalize(action);

        Assert.Equal(SpecialKeyBindingDefaults.MaximumTextLength, normalized.Value!.Length);
    }

    [Fact]
    public void OverridesDefaultBehavior_RequiresACompleteAction()
    {
        Assert.False(SpecialKeyBindingDefaults.OverridesDefaultBehavior(
            new SpecialKeyAction(SpecialKeyActionKind.Default)));
        Assert.False(SpecialKeyBindingDefaults.OverridesDefaultBehavior(
            new SpecialKeyAction(SpecialKeyActionKind.KeyboardShortcut)));
        Assert.True(SpecialKeyBindingDefaults.OverridesDefaultBehavior(
            new SpecialKeyAction(
                SpecialKeyActionKind.KeyboardShortcut,
                "Ctrl+Shift+M")));
        Assert.True(SpecialKeyBindingDefaults.OverridesDefaultBehavior(
            new SpecialKeyAction(SpecialKeyActionKind.Disabled)));
    }

    [Fact]
    public void RawHidInput_RecognizesEachCapturedFnLayerKeyOncePerPress()
    {
        bool functionPressed = false;
        Span<bool> pressed = stackalloc bool[5];

        Assert.False(Trigger([0x04, 0x0A, 0x00], ref functionPressed, pressed, out _));
        Assert.True(Trigger(
            [0x01, 0x00, 0x4B],
            ref functionPressed,
            pressed,
            out SpecialKeySlot m1));
        Assert.Equal(SpecialKeySlot.M1, m1);
        Assert.False(Trigger([0x01, 0x00, 0x4B], ref functionPressed, pressed, out _));
        Assert.False(Trigger([0x01, 0x00, 0x00], ref functionPressed, pressed, out _));

        Assert.True(Trigger(
            [0x01, 0x00, 0x4E],
            ref functionPressed,
            pressed,
            out SpecialKeySlot m2));
        Assert.Equal(SpecialKeySlot.M2, m2);
        Assert.False(Trigger([0x01, 0x00, 0x00], ref functionPressed, pressed, out _));

        Assert.True(Trigger(
            [0x04, 0x0A, 0x03],
            ref functionPressed,
            pressed,
            out SpecialKeySlot m3));
        Assert.Equal(SpecialKeySlot.M3, m3);
        Assert.False(Trigger([0x04, 0x0A, 0x00], ref functionPressed, pressed, out _));

        Assert.True(Trigger(
            [0x04, 0x0A, 0xD3],
            ref functionPressed,
            pressed,
            out SpecialKeySlot m4));
        Assert.Equal(SpecialKeySlot.M4, m4);
        Assert.False(Trigger([0x04, 0x0A, 0x00], ref functionPressed, pressed, out _));

        Assert.True(Trigger(
            [0x04, 0x0A, 0xD4],
            ref functionPressed,
            pressed,
            out SpecialKeySlot m5));
        Assert.Equal(SpecialKeySlot.M5, m5);
        Assert.False(Trigger([0x04, 0x00, 0x00], ref functionPressed, pressed, out _));
        Assert.False(functionPressed);
    }

    [Fact]
    public void RawHidInput_DoesNotTreatPageKeysAsMKeysWithoutFn()
    {
        bool functionPressed = false;
        Span<bool> pressed = stackalloc bool[5];

        Assert.False(Trigger(
            [0x01, 0x00, 0x4B],
            ref functionPressed,
            pressed,
            out _));
        Assert.False(Trigger(
            [0x01, 0x00, 0x4E],
            ref functionPressed,
            pressed,
            out _));
    }

    private static bool Trigger(
        ReadOnlySpan<byte> report,
        ref bool functionPressed,
        Span<bool> pressed,
        out SpecialKeySlot slot) =>
        Rz09_0581SpecialKeyRawHidInput.TryCreateTrigger(
            report,
            ref functionPressed,
            pressed,
            out slot);
}
