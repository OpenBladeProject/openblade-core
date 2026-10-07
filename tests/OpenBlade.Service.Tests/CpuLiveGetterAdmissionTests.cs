using OpenBlade.Contracts;
using OpenBlade.Service;

namespace OpenBlade.Service.Tests;

public sealed class CpuLiveGetterAdmissionTests
{
    [Theory]
    [InlineData("cancel")]
    [InlineData("deadline")]
    [InlineData("lifetime")]
    [InlineData("transport")]
    public void TransportGuardPreservesOriginalLifetimeAndStickyRevocationWithoutDiscovery(string fault)
    {
        using var cancellation = new CancellationTokenSource();
        bool lifetime = true, transport = true;
        int full = 0, fresh = 0; TimeSpan elapsed = TimeSpan.Zero;
        using var proof = new CpuLiveGetterAdmission(cancellation.Token, () => lifetime,
            _ => { full++; return true; }, () => elapsed,
            continuity: _ => { fresh++; return true; }, transportCurrent: () => transport);
        Assert.True(proof.Allows());
        for (int i = 0; i < 100; i++) { Assert.True(proof.TransportCurrent()); }
        Assert.Equal(1, full); Assert.Equal(0, fresh);
        Assert.True(proof.Allows()); Assert.Equal(1, fresh);
        if (fault == "cancel") { cancellation.Cancel(); }
        if (fault == "deadline") { elapsed = TimeSpan.FromSeconds(10); }
        if (fault == "lifetime") { lifetime = false; }
        if (fault == "transport") { transport = false; }
        Assert.False(proof.TransportCurrent());
        lifetime = transport = true; elapsed = TimeSpan.Zero;
        Assert.False(proof.TransportCurrent()); Assert.False(proof.Revalidate());
    }

    [Fact]
    public void MissingTransportGuardRetainsFreshAdmissionAndUnadmittedGuardCannotStartReads()
    {
        int full = 0;
        using var legacy = new CpuLiveGetterAdmission(default, () => true, _ => { full++; return true; });
        Assert.True(legacy.TransportCurrent()); Assert.True(legacy.TransportCurrent()); Assert.Equal(2, full);
        using var scoped = new CpuLiveGetterAdmission(default, () => true, _ => true, transportCurrent: () => true);
        Assert.False(scoped.TransportCurrent()); Assert.False(scoped.Allows());
    }

    [Fact]
    public void CompoundLifetimeSurvivesTenSecondsButCannotBeRenewedPastThirty()
    {
        TimeSpan elapsed = TimeSpan.Zero;
        var diagnostic = new CpuSavedAdmissionDiagnostic();
        using var ordinary = new CpuLiveGetterAdmission(default, () => true, _ => true, () => elapsed);
        using var compound = new CpuLiveGetterAdmission(default, () => true, _ => true, () => elapsed,
            diagnostic: diagnostic, maximumLifetime: TimeSpan.FromSeconds(30));
        Assert.True(ordinary.Allows()); Assert.True(compound.Allows());
        elapsed = TimeSpan.FromMilliseconds(10016);
        Assert.False(ordinary.Allows()); Assert.True(compound.Allows());
        elapsed = TimeSpan.FromMilliseconds(29999);
        Assert.True(compound.Revalidate());
        elapsed = TimeSpan.FromSeconds(30);
        Assert.False(compound.Allows());
        Assert.Equal("getter deadline; elapsed 30000 ms", diagnostic.FirstFailure);
        elapsed = TimeSpan.Zero;
        Assert.False(ordinary.Revalidate()); Assert.False(compound.Revalidate());
    }

    [Theory]
    [InlineData("cancel")]
    [InlineData("lifetime")]
    [InlineData("continuity")]
    [InlineData("full-admission")]
    public void CompoundLifetimeNeverOverridesCallerOrFreshAdmission(string failure)
    {
        using var cancellation = new CancellationTokenSource();
        bool lifetime = true, continuity = true, full = true;
        TimeSpan elapsed = TimeSpan.Zero;
        using var proof = new CpuLiveGetterAdmission(cancellation.Token, () => lifetime, _ => full, () => elapsed,
            continuity: _ => continuity, maximumLifetime: TimeSpan.FromSeconds(30));
        Assert.True(proof.Allows());
        elapsed = TimeSpan.FromMilliseconds(10016);
        Assert.True(proof.Allows());
        if (failure == "cancel") { cancellation.Cancel(); }
        if (failure == "lifetime") { lifetime = false; }
        if (failure == "continuity") { continuity = false; }
        if (failure == "full-admission") { full = false; }
        Assert.False(failure == "full-admission" ? proof.Revalidate() : proof.Allows());
        lifetime = true; continuity = true; full = true; elapsed = TimeSpan.Zero;
        Assert.False(proof.Allows()); Assert.False(proof.Revalidate());
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(0)]
    [InlineData(30001)]
    public void LifetimeCannotBeUnbounded(int milliseconds) =>
        Assert.Throws<ArgumentOutOfRangeException>(() => new CpuLiveGetterAdmission(default, () => true, _ => true,
            maximumLifetime: TimeSpan.FromMilliseconds(milliseconds)));

    [Theory]
    [InlineData("revoked", "getter revoked")]
    [InlineData("cancel", "getter cancellation")]
    [InlineData("lifetime", "getter lifetime")]
    [InlineData("deadline", "getter deadline; elapsed 10025 ms")]
    [InlineData("lifetime-exception", "getter lifetime exception")]
    [InlineData("clock-exception", "getter deadline exception")]
    public void ActiveFailureNamesFirstGuardAndNeverResamplesAfterRevocation(string failure, string expected)
    {
        var diagnostic = new CpuSavedAdmissionDiagnostic();
        using var cancellation = new CancellationTokenSource();
        int lifetimes = 0, clocks = 0, admissions = 0;
        using var proof = new CpuLiveGetterAdmission(cancellation.Token,
            () => { lifetimes++; if (failure == "lifetime-exception") { throw new IOException("private"); } return failure != "lifetime"; },
            _ => { admissions++; return true; },
            () => { clocks++; if (failure == "clock-exception") { throw new IOException("private"); } return TimeSpan.FromMilliseconds(10025); },
            diagnostic: diagnostic);
        if (failure == "revoked") { proof.Dispose(); }
        if (failure == "cancel") { cancellation.Cancel(); }
        Assert.False(proof.Allows());
        Assert.Equal(expected, diagnostic.FirstFailure);
        Assert.Equal(failure is "revoked" or "cancel" ? 0 : 1, lifetimes);
        Assert.Equal(failure is "deadline" or "clock-exception" ? 1 : 0, clocks);
        Assert.Equal(0, admissions);
        Assert.False(proof.Allows());
        Assert.Equal(expected, diagnostic.FirstFailure);
        Assert.Equal(failure is "deadline" or "clock-exception" ? 1 : 0, clocks);
    }

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
