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
        Assert.False(SpecialKeyBindingDefaults.OverridesDefaultBehavior(
            new SpecialKeyAction(
                SpecialKeyActionKind.KeyboardShortcut,
                "Ctrl+NoSuchKey")));
        Assert.False(SpecialKeyBindingDefaults.OverridesDefaultBehavior(
            new SpecialKeyAction(
                SpecialKeyActionKind.OpenWebsite,
                "not a website")));
        Assert.True(SpecialKeyBindingDefaults.OverridesDefaultBehavior(
            new SpecialKeyAction(SpecialKeyActionKind.Disabled)));
    }

    [Theory]
    [InlineData(SpecialKeyActionKind.KeyboardShortcut, "Ctrl+NoSuchKey")]
    [InlineData(SpecialKeyActionKind.OpenWebsite, "file:///C:/Windows")]
    public void Validation_RejectsValuesThatCannotRun(
        SpecialKeyActionKind kind,
        string value)
    {
        string message = SpecialKeyBindingDefaults.GetValidationMessage(
            new SpecialKeyAction(kind, value));

        Assert.NotEmpty(message);
    }

    [Fact]
    public void ShortcutValidation_LocalizesUiMessageWithoutLocalizingParserException()
    {
        using var culture = new CultureScope("qps-ploc");

        FormatException exception = Assert.ThrowsAny<FormatException>(
            () => SpecialKeyShortcutParser.Parse("Ctrl+NoSuchKey"));
        string validationMessage = SpecialKeyBindingDefaults.GetValidationMessage(
            new SpecialKeyAction(
                SpecialKeyActionKind.KeyboardShortcut,
                "Ctrl+NoSuchKey"));

        Assert.Equal("Unknown shortcut key: NoSuchKey", exception.Message);
        Assert.Equal(
            TrayText.Format("Unknown shortcut key: {0}", "NoSuchKey"),
            validationMessage);
        Assert.NotEqual(exception.Message, validationMessage);
    }

    [Theory]
    [InlineData("en-US", "Enter a value for this action.")]
    [InlineData("ru-RU", "Введите значение для этого действия.")]
    [InlineData("zh-CN", "请输入此操作的值。")]
    public void ValidationMessageFollowsTheSelectedUiLanguage(
        string cultureName,
        string expected)
    {
        using var culture = new CultureScope(cultureName);

        string message = SpecialKeyBindingDefaults.GetValidationMessage(
            new SpecialKeyAction(SpecialKeyActionKind.KeyboardShortcut));

        Assert.Equal(expected, message);
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

    [Theory]
    [InlineData(0x03, SpecialKeySlot.M3)]
    [InlineData(0xD3, SpecialKeySlot.M4)]
    [InlineData(0xD4, SpecialKeySlot.M5)]
    public void RawHidInput_RearmsDedicatedMKeyAfterEveryRelease(
        byte usage,
        SpecialKeySlot expectedSlot)
    {
        bool functionPressed = false;
        Span<bool> pressed = stackalloc bool[5];

        Assert.False(Trigger([0x04, 0x0A, 0x00], ref functionPressed, pressed, out _));
        for (int press = 0; press < 3; press++)
        {
            Assert.True(Trigger(
                [0x04, 0x0A, usage],
                ref functionPressed,
                pressed,
                out SpecialKeySlot slot));
            Assert.Equal(expectedSlot, slot);
            Assert.False(Trigger(
                [0x04, 0x0A, usage],
                ref functionPressed,
                pressed,
                out _));
            Assert.False(Trigger(
                [0x04, 0x0A, 0x00],
                ref functionPressed,
                pressed,
                out _));
        }

        Assert.True(functionPressed);
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

    [Fact]
    public void RawHidInput_DoesNotTreatDirectDefaultActionsAsFnLayerMKeys()
    {
        bool functionPressed = false;
        Span<bool> pressed = stackalloc bool[5];

        Assert.False(Trigger(
            [0x04, 0x03, 0x00],
            ref functionPressed,
            pressed,
            out _));
        Assert.False(functionPressed);
        Assert.False(Trigger([0x04, 0x00, 0x00], ref functionPressed, pressed, out _));

        Assert.False(Trigger(
            [0x04, 0xD3, 0x00],
            ref functionPressed,
            pressed,
            out _));
        Assert.False(Trigger([0x04, 0x00, 0x00], ref functionPressed, pressed, out _));

        Assert.False(Trigger(
            [0x04, 0xD4, 0x00],
            ref functionPressed,
            pressed,
            out _));
        Assert.False(Trigger([0x04, 0x00, 0x00], ref functionPressed, pressed, out _));
    }

    [Fact]
    public void DefaultLaptopKeyInput_RecognizesDirectM3AndM5OncePerPress()
    {
        bool gamingModePressed = false;
        bool microphoneMutePressed = false;

        Assert.True(Rz09_0581DefaultLaptopKeyRawHidInput.TryCreateTrigger(
            [0x04, 0x03, 0x00],
            ref gamingModePressed,
            ref microphoneMutePressed,
            out SpecialKeySlot gamingMode));
        Assert.Equal(SpecialKeySlot.M3, gamingMode);
        Assert.False(Rz09_0581DefaultLaptopKeyRawHidInput.TryCreateTrigger(
            [0x04, 0x03, 0x00],
            ref gamingModePressed,
            ref microphoneMutePressed,
            out _));
        Assert.False(Rz09_0581DefaultLaptopKeyRawHidInput.TryCreateTrigger(
            [0x04, 0x00, 0x00],
            ref gamingModePressed,
            ref microphoneMutePressed,
            out _));

        Assert.True(Rz09_0581DefaultLaptopKeyRawHidInput.TryCreateTrigger(
            [0x04, 0xD4, 0x00],
            ref gamingModePressed,
            ref microphoneMutePressed,
            out SpecialKeySlot microphoneMute));
        Assert.Equal(SpecialKeySlot.M5, microphoneMute);
        Assert.False(Rz09_0581DefaultLaptopKeyRawHidInput.TryCreateTrigger(
            [0x04, 0x00, 0x00],
            ref gamingModePressed,
            ref microphoneMutePressed,
            out _));
    }

    [Fact]
    public void DefaultLaptopKeyInput_DoesNotConsumeFnLayerOrPerformanceReports()
    {
        bool gamingModePressed = false;
        bool microphoneMutePressed = false;

        Assert.False(Rz09_0581DefaultLaptopKeyRawHidInput.TryCreateTrigger(
            [0x04, 0x0A, 0x03],
            ref gamingModePressed,
            ref microphoneMutePressed,
            out _));
        Assert.False(Rz09_0581DefaultLaptopKeyRawHidInput.TryCreateTrigger(
            [0x04, 0xD3, 0x00],
            ref gamingModePressed,
            ref microphoneMutePressed,
            out _));
        Assert.False(Rz09_0581DefaultLaptopKeyRawHidInput.TryCreateTrigger(
            [0x04, 0x0A, 0xD4],
            ref gamingModePressed,
            ref microphoneMutePressed,
            out _));
    }

    [Fact]
    public void Rz090528M4_IsSpecialKeyAndNeverNeedsOlderPerformanceSemantics()
    {
        bool functionPressed = false;
        Span<bool> pressed = stackalloc bool[5];

        Assert.False(RazerBladeSpecialKeyRawHidInput.TryCreateTrigger(
            RazerBladeInputDeviceKind.Rz09_0528,
            [0x04, 0x0A, 0x00],
            ref functionPressed,
            pressed,
            out _));
        Assert.True(RazerBladeSpecialKeyRawHidInput.TryCreateTrigger(
            RazerBladeInputDeviceKind.Rz09_0528,
            [0x04, 0x0A, 0xD3],
            ref functionPressed,
            pressed,
            out SpecialKeySlot slot));
        Assert.Equal(SpecialKeySlot.M4, slot);
        Assert.False(RazerBladeSpecialKeyRawHidInput.TryCreateTrigger(
            RazerBladeInputDeviceKind.Rz09_0528,
            [0x04, 0x0A, 0x00],
            ref functionPressed,
            pressed,
            out _));
    }

    [Fact]
    public void ModelAwareSpecialKeyInput_RejectsUnsupportedDeviceIdentity()
    {
        bool functionPressed = false;
        Span<bool> pressed = stackalloc bool[5];

        Assert.False(RazerBladeSpecialKeyRawHidInput.TryCreateTrigger(
            RazerBladeInputDeviceKind.Unsupported,
            [0x04, 0x0A, 0xD3],
            ref functionPressed,
            pressed,
            out _));
        Assert.False(functionPressed);
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
