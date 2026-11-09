using OpenBlade.Contracts;
using OpenBlade.SessionAgent;
using OpenBlade.Tray;

namespace OpenBlade.SessionAgent.Tests;

public sealed class Blade18FunctionRowDriverTests
{
    private const RazerBladeInputDeviceKind Device = RazerBladeInputDeviceKind.Rz09_0484;

    [Theory]
    [InlineData(FunctionKeyPrimaryMode.FunctionKeys, false, false)]
    [InlineData(FunctionKeyPrimaryMode.FunctionKeys, true, true)]
    [InlineData(FunctionKeyPrimaryMode.MultimediaKeys, false, true)]
    [InlineData(FunctionKeyPrimaryMode.MultimediaKeys, true, false)]
    public void ExactDriverMapsBothLayersWithoutWideningWindowsHook(
        FunctionKeyPrimaryMode mode, bool fn, bool media)
    {
        List<SnapTapKeyStroke> sent = [];
        List<FunctionRowInputEvent> actions = [];
        var processor = new FunctionRowDriverInputProcessor(sent.Add, actions.Add, () => { });
        Assert.False(RazerBladeFunctionRowMediaInput.SupportsDevice(Device));
        Assert.True(processor.Handle(new(0x3B, true), Device, fn, mode, true));
        Assert.True(processor.Handle(new(0x3B, false), Device, !fn, mode, false));
        Assert.Equal(media ? 0 : 2, sent.Count);
        Assert.Equal(media ? 2 : 0, actions.Count);
        Assert.False(processor.HasTrackedKeys);
    }

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public void LostAdmissionOrCollectionRequiresAnotherExactFnReport(bool admitted, bool collectionCurrent)
    {
        var fn = new Rz090484FnInputState();
        Observe(fn, 1, 0);
        Assert.False(fn.Read());
        Assert.Null(fn.ReadAdmitted(_ => admitted && collectionCurrent));
        Assert.Null(fn.ReadAdmitted(_ => true));
        Observe(fn, 1, 11);
        Assert.True(fn.ReadAdmitted(_ => true));
        fn.Reset();
        Assert.Null(fn.Read());
    }

    [Fact]
    public void UnknownFnPassesEntirePressThroughEvenAfterKnowledgeReturns()
    {
        var fn = new Rz090484FnInputState();
        List<SnapTapKeyStroke> sent = [];
        List<FunctionRowInputEvent> actions = [];
        var processor = new FunctionRowDriverInputProcessor(sent.Add, actions.Add, () => { });
        Handle(true);
        Observe(fn, 1, 0);
        Handle(true);
        Handle(false);
        Assert.Equal(3, sent.Count);
        Assert.Empty(actions);
        Handle(true);
        Assert.Single(actions);
        fn.InvalidateKnowledge();
        Handle(true);
        Assert.Single(actions);
        Handle(false);
        Assert.Equal(FunctionRowInputPhase.Up, actions[1].Phase);
        Assert.Equal(3, sent.Count);

        void Handle(bool down)
        {
            bool? state = fn.Read();
            Assert.True(processor.Handle(new(0x3C, down), Device, state == true,
                FunctionKeyPrimaryMode.MultimediaKeys, state.HasValue));
        }
    }

    [Fact]
    public void RetiredSourceCannotPublishNewSnapshotUnderThePreviousCollectionIdentity()
    {
        var fn = new Rz090484FnInputState();
        Observe(fn, 1, 0);
        Assert.Null(fn.ReadAdmitted(collection =>
        {
            Assert.Equal((nint)1, collection);
            fn.Reset();
            Observe(fn, 2, 11);
            return true;
        }));
        Assert.True(fn.ReadAdmitted(collection => collection == 2));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void FailedProbeCannotClearNewerFnReport(bool changeCollection)
    {
        var fn = new Rz090484FnInputState();
        Observe(fn, 1, 0);
        nint currentCollection = changeCollection ? 2 : 1;

        Assert.Null(fn.ReadAdmitted(collection =>
        {
            Assert.Equal((nint)1, collection);
            if (changeCollection)
            {
                fn.Reset();
            }
            Observe(fn, currentCollection, 11);
            return false;
        }));

        Assert.True(fn.Read());
        Assert.True(fn.ReadAdmitted(collection => collection == currentCollection));
    }

    [Fact]
    public void FnMetadataRejectsOtherModelsMalformedReportsAndAmbiguousCollections()
    {
        var fn = new Rz090484FnInputState();
        byte[] report = new byte[16];
        report[0] = 4;
        report[15] = 11;
        fn.ObserveHid(RazerBladeInputDeviceKind.Rz09_0581, 1, report, true);
        Assert.Null(fn.Read());
        fn.ObserveHid(Device, 1, report, true);
        Assert.True(fn.Read());
        fn.ObserveHid(Device, 1, [4, 11, 0], true);
        Assert.Null(fn.Read());
        fn.ObserveHid(Device, 1, report, true);
        fn.ObserveHid(Device, 2, report, true);
        Assert.Null(fn.Read());
        fn.ObserveHid(Device, 1, report, true);
        Assert.Null(fn.Read());
        fn.Reset();
        fn.ObserveHid(Device, 2, report, true);
        Assert.True(fn.Read());
    }

    [Theory]
    [InlineData(KeyboardInputBackend.WindowsHook, true)]
    [InlineData(KeyboardInputBackend.RazerDriver, false)]
    [InlineData(KeyboardInputBackend.Unknown, false)]
    public void DriverSelectionKeepsDefaultShortcutsWithOneFunctionRowOwner(KeyboardInputBackend backend, bool expected)
    {
        Assert.Equal(expected, WindowsReactiveKeyboardSource.AllowsBlade18DefaultFunctionRow(0x3B, backend));
        Assert.Equal(expected, WindowsReactiveKeyboardSource.AllowsBlade18DefaultFunctionRow(0x58, backend));
        foreach (uint scan in new uint[] { 0x19, 0x13, 0x14, 0x48, 0x4B, 0x50, 0x4D, 0x53 })
        {
            Assert.True(WindowsReactiveKeyboardSource.AllowsBlade18DefaultFunctionRow(scan, backend));
        }
    }

    [Theory]
    [InlineData(KeyboardInputBackend.RazerDriver)]
    [InlineData(KeyboardInputBackend.Unknown)]
    public void UnmatchedFunctionRowEdgesCannotStartRecoveryWithoutWindowsOwnership(KeyboardInputBackend backend)
    {
        var state = new Blade18DefaultFnInputState();
        Assert.False(WindowsReactiveKeyboardSource.ReconcileBlade18PassedThroughEdge(
            state, 0x3C, false, true, backend, out var down, out bool reinstallOnDown));
        Assert.False(WindowsReactiveKeyboardSource.ReconcileBlade18PassedThroughEdge(
            state, 0x3C, false, false, backend, out var up, out bool reinstallOnUp));
        Assert.False(reinstallOnDown);
        Assert.False(reinstallOnUp);
        Assert.Null(down);
        Assert.Null(up);
        Assert.False(state.HasActiveFunctionRowKeys);
    }

    [Theory]
    [InlineData(KeyboardInputBackend.RazerDriver, true)]
    [InlineData(KeyboardInputBackend.Unknown, false)]
    public void UnmatchedReleaseDrainsAnExistingWindowsPressAfterHandoff(
        KeyboardInputBackend backend, bool consumed)
    {
        var state = new Blade18DefaultFnInputState();
        Assert.Equal(consumed, state.Process(0x3C, false, true, false, consumed, true, out _));
        Assert.True(state.HasActiveFunctionRowKeys);

        Assert.True(WindowsReactiveKeyboardSource.ReconcileBlade18PassedThroughEdge(
            state, 0x3C, false, false, backend, out var release, out bool reinstallHook));

        Assert.False(state.HasActiveFunctionRowKeys);
        Assert.False(reinstallHook);
        if (consumed)
        {
            Assert.Equal(Blade18DefaultFnAction.VolumeDown, release?.Action);
            Assert.Equal(FunctionRowInputPhase.Up, release?.Phase);
        }
        else
        {
            Assert.Null(release);
        }
    }

    [Theory]
    [InlineData(0x3C, false, KeyboardInputBackend.WindowsHook)]
    [InlineData(0x19, false, KeyboardInputBackend.RazerDriver)]
    [InlineData(0x48, true, KeyboardInputBackend.Unknown)]
    public void UnmatchedEdgesStillRecoverKeysOwnedByWindows(uint scanCode, bool extended, KeyboardInputBackend backend)
    {
        var state = new Blade18DefaultFnInputState();
        Assert.True(WindowsReactiveKeyboardSource.ReconcileBlade18PassedThroughEdge(
            state, scanCode, extended, true, backend, out var release, out bool reinstallHook));
        Assert.True(reinstallHook);
        Assert.Null(release);
    }

    [Fact]
    public void WindowsHoldCannotTransferOwnershipBeforeItsMatchingRelease()
    {
        var state = new Blade18DefaultFnInputState();
        Assert.True(state.Process(0x3C, false, true, false, true, true, out _));
        Assert.True(state.HasActiveFunctionRowKeys);
        state.Cancel();
        Assert.True(state.HasActiveFunctionRowKeys);
        Assert.True(state.Process(0x3C, false, false, false, false, false, out _));
        Assert.False(state.HasActiveFunctionRowKeys);
    }

    [Fact]
    public void AmbiguousDriverOutputStopsWithoutReplay()
    {
        int submissions = 0;
        var processor = new FunctionRowDriverInputProcessor(_ =>
        {
            submissions++;
            throw new IOException("uncertain");
        }, _ => Assert.Fail(), () => { });
        Assert.False(processor.Handle(new(0x3B, true), Device, false,
            FunctionKeyPrimaryMode.FunctionKeys, true));
        Assert.True(processor.OutputUncertain);
        Assert.False(processor.Handle(new(0x3B, false), Device, false,
            FunctionKeyPrimaryMode.FunctionKeys, true));
        Assert.Equal(1, submissions);
    }

    [Fact]
    public void SourceReviewedBlade18KeepsTheRequestedDriverFeatureUnion()
    {
        var profile = new RazerSnapTapDriverProfile("RZ09-0484", "02A0", true, false);
        Assert.False(profile.PhysicallyValidated);
        var requested = KeyboardDriverFeatures.SnapTap | KeyboardDriverFeatures.FunctionRow;
        Assert.Equal(requested, SnapTapDriverSupervisor.AdmitFeatures(requested, profile));
    }

    private static void Observe(Rz090484FnInputState fn, nint collection, byte key)
    {
        byte[] report = new byte[16];
        report[0] = 4;
        report[15] = key;
        fn.ObserveHid(Device, collection, report, true);
    }
}
