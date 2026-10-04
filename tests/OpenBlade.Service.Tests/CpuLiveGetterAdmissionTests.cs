using OpenBlade.Contracts;
using OpenBlade.Service;

namespace OpenBlade.Service.Tests;

public sealed class CpuLiveGetterAdmissionTests
{
    [Fact]
    public void FullAdmissionRunsAtEntryAndEndWhileEveryBoundaryChecksContinuity()
    {
        int full = 0, continuity = 0;
        bool valid = true;
        using var proof = new CpuLiveGetterAdmission(default, () => true,
            _ => { full++; return valid; }, continuity: _ => { continuity++; return true; });
        for (int i = 0; i < 164; i++) { Assert.True(proof.Allows()); }
        Assert.Equal(1, full); Assert.Equal(163, continuity);
        valid = false;
        Assert.False(proof.Revalidate()); Assert.Equal(2, full);
        valid = true; Assert.False(proof.Allows()); Assert.False(proof.Revalidate());
    }

    [Fact]
    public void EachGetterClassifiesOnceButChecksWindowsAndLifetimeEveryTime()
    {
        int classifications = 0, captures = 0, checks = 0;
        CpuLiveGetterAdmission Create() => new(default, () => { checks++; return true; },
            proof => proof.FullPower(() => { classifications++; return true; },
                () => { captures++; return PowerSource.ExternalUnknown; }));
        using var first = Create();
        for (int i = 0; i < 820; i++) { Assert.True(first.Allows()); }
        Assert.Equal(1, classifications); Assert.Equal(820, captures); Assert.True(checks >= 820);
        first.Dispose(); Assert.False(first.Allows());
        using var second = Create(); Assert.True(second.Allows()); Assert.Equal(2, classifications);
        Assert.False(first.Allows());
    }

    [Theory]
    [InlineData("battery")]
    [InlineData("usb")]
    [InlineData("unknown")]
    [InlineData("generation")]
    [InlineData("cancel")]
    [InlineData("deadline")]
    [InlineData("end")]
    public void DisagreementPermanentlyRevokesTheGetter(string failure)
    {
        using var cancellation = new CancellationTokenSource();
        bool current = true; TimeSpan elapsed = TimeSpan.Zero; var power = PowerSource.ExternalUnknown;
        int classifications = 0;
        using var proof = new CpuLiveGetterAdmission(cancellation.Token, () => current,
            value => value.FullPower(() => { classifications++; return true; }, () => power), () => elapsed);
        Assert.True(proof.Allows());
        switch (failure)
        {
            case "battery": power = PowerSource.Battery; break;
            case "usb": power = PowerSource.UsbC; break;
            case "unknown": power = (PowerSource)99; break;
            case "generation": current = false; break;
            case "cancel": cancellation.Cancel(); break;
            case "deadline": elapsed = TimeSpan.FromSeconds(10); break;
            case "end": proof.Dispose(); break;
        }
        Assert.False(proof.Allows());
        current = true; power = PowerSource.ExternalUnknown; elapsed = TimeSpan.Zero;
        Assert.False(proof.Allows()); Assert.Equal(1, classifications);
    }

    [Fact]
    public void LateSuccessfulClassificationCannotRenewCancelledGetter()
    {
        using var cancellation = new CancellationTokenSource();
        int captures = 0;
        using var proof = new CpuLiveGetterAdmission(cancellation.Token, () => true,
            value => value.FullPower(() => { cancellation.Cancel(); return true; },
                () => { captures++; return PowerSource.ExternalUnknown; }));
        Assert.False(proof.Allows()); Assert.False(proof.Allows()); Assert.Equal(0, captures);
    }
}
