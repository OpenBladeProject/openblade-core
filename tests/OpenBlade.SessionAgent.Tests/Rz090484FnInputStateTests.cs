using OpenBlade.Tray;

namespace OpenBlade.SessionAgent.Tests;

public sealed class Rz090484FnInputStateTests
{
    [Theory]
    [InlineData(10)]
    [InlineData(11)]
    public void Snapshot_AcceptsEitherSourceBackedFnIdInEverySlot(byte key)
    {
        for (int slot = 1; slot < 16; slot++)
        {
            var state = new Rz090484FnInputState();
            byte[] report = new byte[16];
            report[0] = 4;
            report[slot] = key;
            state.ObserveHid(RazerBladeInputDeviceKind.Rz09_0484, 1, report, true);
            Assert.True(state.FunctionHeld);
            report[slot] = 0;
            state.ObserveHid(RazerBladeInputDeviceKind.Rz09_0484, 1, report, true);
            Assert.False(state.FunctionHeld);
        }
    }

    [Fact]
    public void Snapshot_DuplicatesAndOrderDoNotChangeHeldState()
    {
        var state = Held();
        Observe(state, 1, 4, 11, 10, 10);
        Assert.True(state.FunctionHeld);
        Observe(state, 1, 4, 0, 11);
        Assert.True(state.FunctionHeld);
        Observe(state, 1, 4);
        Assert.False(state.FunctionHeld);
    }

    [Fact]
    public void UnrelatedHardwareReport_DoesNotReleaseFn()
    {
        var state = Held();
        Observe(state, 1, 5);
        Assert.True(state.FunctionHeld);
    }

    [Fact]
    public void CompetingCollection_RemainsAmbiguousUntilLifecycleReset()
    {
        var state = Held();
        Observe(state, 2, 4, 10);
        Assert.False(state.FunctionHeld);
        state.ObserveHid(RazerBladeInputDeviceKind.Rz09_0484, 1, [], true);
        Observe(state, 1, 4, 10);
        Assert.False(state.FunctionHeld);
        state.Reset();
        Observe(state, 2, 4, 10);
        Assert.True(state.FunctionHeld);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(3)]
    [InlineData(15)]
    [InlineData(17)]
    public void MalformedGeometry_ClearsHeldState(int length)
    {
        var state = Held();
        byte[] report = new byte[length];
        if (length > 0)
        {
            report[0] = 4;
        }
        state.ObserveHid(RazerBladeInputDeviceKind.Rz09_0484, 1, report, true);
        Assert.False(state.FunctionHeld);
    }

    [Fact]
    public void IdentityAndAdmission_FailClosed()
    {
        var state = new Rz090484FnInputState();
        byte[] report = new byte[16];
        report[0] = 4;
        report[1] = 10;
        state.ObserveHid(RazerBladeInputDeviceKind.Rz09_0528, 1, report, true);
        Assert.False(state.FunctionHeld);
        state.ObserveHid(RazerBladeInputDeviceKind.Rz09_0484, 0, report, true);
        Assert.False(state.FunctionHeld);
        state.ObserveHid(RazerBladeInputDeviceKind.Rz09_0484, 1, report, true);
        Assert.True(state.FunctionHeld);
        state.ObserveHid(RazerBladeInputDeviceKind.Rz09_0484, 1, report, false);
        Assert.False(state.FunctionHeld);
        state.Reset();
        Assert.False(state.FunctionHeld);
    }

    private static Rz090484FnInputState Held()
    {
        var state = new Rz090484FnInputState();
        Observe(state, 1, 4, 10);
        return state;
    }

    private static void Observe(Rz090484FnInputState state, nint collection, params byte[] bytes)
    {
        byte[] report = new byte[16];
        bytes.CopyTo(report, 0);
        state.ObserveHid(RazerBladeInputDeviceKind.Rz09_0484, collection, report, true);
    }
}
