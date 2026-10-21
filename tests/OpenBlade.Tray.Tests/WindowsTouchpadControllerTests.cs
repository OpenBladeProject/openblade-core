using System.ComponentModel;
using System.Runtime.InteropServices;
using OpenBlade.Tray;

namespace OpenBlade.Tray.Tests;

public sealed class WindowsTouchpadControllerTests
{
    [Fact]
    public void TouchpadParametersV1MatchesWindowsLayout()
    {
        Assert.Equal(44, Marshal.SizeOf<WindowsTouchpadController.TouchpadParametersV1>());
        Assert.Equal(12, Marshal.OffsetOf<WindowsTouchpadController.TouchpadParametersV1>(
            nameof(WindowsTouchpadController.TouchpadParametersV1.SystemInformation)).ToInt32());
        Assert.Equal(16, Marshal.OffsetOf<WindowsTouchpadController.TouchpadParametersV1>(
            nameof(WindowsTouchpadController.TouchpadParametersV1.UserSettings)).ToInt32());
    }

    [Fact]
    public void NativeInputMatchesWindowsLayout()
    {
        Assert.Equal(IntPtr.Size == 8 ? 32 : 24,
            Marshal.SizeOf<WindowsTouchpadController.NativeInputUnion>());
        Assert.Equal(IntPtr.Size == 8 ? 8 : 4,
            Marshal.OffsetOf<WindowsTouchpadController.NativeInput>(
                nameof(WindowsTouchpadController.NativeInput.Data)).ToInt32());
        Assert.Equal(IntPtr.Size == 8 ? 40 : 28,
            Marshal.SizeOf<WindowsTouchpadController.NativeInput>());
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ReadEnabledReturnsReportedPrecisionTouchpadState(bool enabled)
    {
        uint requestedVersion = 0;
        var controller = Create(
            (ref WindowsTouchpadController.TouchpadParametersV1 parameters) =>
            {
                requestedVersion = parameters.VersionNumber;
                parameters.SystemInformation = WindowsTouchpadController.TouchpadPresent
                    | (enabled ? WindowsTouchpadController.TouchpadEnabled : 0);
                return true;
            });

        Assert.Equal(enabled, controller.ReadEnabled());
        Assert.Equal(WindowsTouchpadController.TouchpadParametersVersion1, requestedVersion);
    }

    [Fact]
    public void ReadEnabledReturnsUnavailableWhenPrecisionTouchpadIsAbsent()
    {
        var controller = Create(
            (ref WindowsTouchpadController.TouchpadParametersV1 parameters) =>
            {
                parameters.SystemInformation = WindowsTouchpadController.TouchpadEnabled;
                return true;
            });

        Assert.Null(controller.ReadEnabled());
    }

    [Fact]
    public void ReadEnabledReturnsUnavailableWhenWindowsQueryFails()
    {
        var controller = Create(
            (ref WindowsTouchpadController.TouchpadParametersV1 _) => false);

        Assert.Null(controller.ReadEnabled());
    }

    [Fact]
    public void ToggleSendsDocumentedChordOnce()
    {
        WindowsTouchpadController.NativeInput[]? sent = null;
        var controller = Create(
            sendInput: inputs =>
            {
                sent = inputs;
                return checked((uint)inputs.Length);
            });

        controller.Toggle();

        Assert.NotNull(sent);
        Assert.Equal([0x11, 0x5B, 0x87, 0x87, 0x5B, 0x11],
            sent.Select(input => (int)input.Data.Keyboard.VirtualKey));
        Assert.Equal([0U, 0U, 0U, 0x0002U, 0x0002U, 0x0002U],
            sent.Select(input => input.Data.Keyboard.Flags));
        Assert.All(sent, input => Assert.Equal(1U, input.Type));
    }

    [Theory]
    [InlineData(0x11)]
    [InlineData(0x10)]
    [InlineData(0x12)]
    [InlineData(0x5B)]
    [InlineData(0x5C)]
    [InlineData(0x87)]
    public void ToggleRefusesAChordKeyThatIsAlreadyHeld(ushort heldKey)
    {
        int sends = 0;
        var controller = Create(
            sendInput: inputs =>
            {
                sends++;
                return checked((uint)inputs.Length);
            },
            getAsyncKeyState: key => key == heldKey ? unchecked((short)0x8000) : (short)0);

        Assert.Throws<InvalidOperationException>(() => controller.Toggle());
        Assert.Equal(0, sends);
    }

    [Fact]
    public void PartialShortcutReleasesOnlyAcceptedKeyDownsWithoutRetryingChord()
    {
        var submissions = new List<WindowsTouchpadController.NativeInput[]>();
        var controller = Create(
            sendInput: inputs =>
            {
                submissions.Add(inputs);
                return submissions.Count == 1 ? 2U : checked((uint)inputs.Length);
            });

        Assert.Throws<Win32Exception>(() => controller.Toggle());

        Assert.Equal(2, submissions.Count);
        Assert.Equal(6, submissions[0].Length);
        Assert.Equal([0x5B, 0x11],
            submissions[1].Select(input => (int)input.Data.Keyboard.VirtualKey));
        Assert.All(submissions[1], input =>
            Assert.Equal(0x0002U, input.Data.Keyboard.Flags));
    }

    [Fact]
    public void PartialShortcutDoesNotReleaseAKeyWhoseKeyUpWasAccepted()
    {
        var submissions = new List<WindowsTouchpadController.NativeInput[]>();
        var controller = Create(
            sendInput: inputs =>
            {
                submissions.Add(inputs);
                return submissions.Count == 1 ? 4U : checked((uint)inputs.Length);
            });

        Assert.Throws<Win32Exception>(() => controller.Toggle());

        Assert.Equal([0x5B, 0x11],
            submissions[1].Select(input => (int)input.Data.Keyboard.VirtualKey));
    }

    [Fact]
    public void RejectedShortcutDoesNotAttemptCleanupOrRetry()
    {
        int sends = 0;
        var controller = Create(
            sendInput: _ =>
            {
                sends++;
                return 0;
            });

        Assert.Throws<Win32Exception>(() => controller.Toggle());
        Assert.Equal(1, sends);
    }

    [Fact]
    public void FailedCleanupIsBoundedAndReportedWithoutRetryingChord()
    {
        int sends = 0;
        var controller = Create(
            sendInput: _ => ++sends == 1 ? 2U : 0U);

        Win32Exception exception = Assert.Throws<Win32Exception>(() => controller.Toggle());

        Assert.Equal(2, sends);
        Assert.Contains("bounded key-release cleanup", exception.Message);
    }

    private static WindowsTouchpadController Create(
        WindowsTouchpadController.GetTouchpadParametersDelegate? getParameters = null,
        WindowsTouchpadController.SendInputDelegate? sendInput = null,
        WindowsTouchpadController.GetAsyncKeyStateDelegate? getAsyncKeyState = null) => new(
            getParameters ?? DefaultGetParameters,
            sendInput ?? (inputs => checked((uint)inputs.Length)),
            getAsyncKeyState ?? (_ => 0));

    private static bool DefaultGetParameters(
        ref WindowsTouchpadController.TouchpadParametersV1 parameters)
    {
        parameters.SystemInformation = WindowsTouchpadController.TouchpadPresent;
        return true;
    }
}
