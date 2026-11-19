using System.Security.Cryptography;
using OpenBlade.Contracts;

namespace OpenBlade.Protocol.Tests;

public sealed class Pid02E0FireEffectTests
{
    // Recorded from the existing compiled 6x16 renderer before extracting the
    // pure kernel. Preserve its colors exactly; these are software vectors.
    [Theory]
    [InlineData(0, "4A9FA6C00341B855025BD0623AA812F6120DF1234319D4757AB4D8BDB7A7A2D4")]
    [InlineData(1250, "F1F48AA63E468134FCFBCB428041A65ED18A4592920833759DC204650518956A")]
    [InlineData(2999, "3F2DC046E66776620EA1CCF3DB9CB78DA33D1434375A0C4A176E30498C79E995")]
    [InlineData(12000, "4A9FA6C00341B855025BD0623AA812F6120DF1234319D4757AB4D8BDB7A7A2D4")]
    public void SharedFireKernel_PreservesExistingGridKnownAnswers(int milliseconds, string expected)
    {
        var effect = new ExperimentalGridLightingEffect(LightingSettingsFactory.Fire(100));
        IReadOnlyList<RgbColor> colors = effect.RenderFrame(TimeSpan.FromMilliseconds(milliseconds));
        byte[] bytes = colors.SelectMany(color => new[] { color.Red, color.Green, color.Blue }).ToArray();

        Assert.Equal(expected, Convert.ToHexString(SHA256.HashData(bytes)));
    }

    [Fact]
    public void Fire_UsesOwnSeventeenColumnCanvasAndKeepsItsHolesBlack()
    {
        Pid02E0MatrixFrame frame = new Pid02E0FireEffect().RenderFrame(TimeSpan.Zero);

        Assert.Equal(102, frame.ToArray().Length);
        Assert.False(frame.LogicalKeyColors);
        Assert.Equal(new MatrixRgb(119, 30, 0), frame[1]);
        Assert.Equal(new MatrixRgb(103, 23, 0), frame[16]);
        Assert.Equal(new MatrixRgb(223, 107, 0), frame[101]);
        foreach (int slot in Pid02E0MatrixLayout.CapturedHoleSlots)
        {
            Assert.Equal(MatrixRgb.Black, frame[slot]);
        }
    }

    [Fact]
    public void Fire_IsCycleAddressedAndIndependentOfRenderOrder()
    {
        var effect = new Pid02E0FireEffect();
        Pid02E0MatrixFrame first = effect.RenderFrame(TimeSpan.FromMilliseconds(1250));
        _ = effect.RenderFrame(TimeSpan.FromSeconds(8));

        Assert.Equal(first.ToArray(), effect.RenderFrame(TimeSpan.FromMilliseconds(1250)).ToArray());
        Assert.Equal(first.ToArray(), effect.RenderFrame(TimeSpan.FromMilliseconds(13250)).ToArray());
        Assert.Equal(new MatrixRgb(199, 86, 0), first[101]);
        Assert.NotEqual(first[101], effect.RenderFrame(TimeSpan.Zero)[101]);
    }

    [Fact]
    public void Fire_RejectsNegativeTimeAndUsesTheExistingSelectableCadence()
    {
        var effect = new Pid02E0FireEffect();
        Assert.Throws<ArgumentOutOfRangeException>(() => effect.RenderFrame(TimeSpan.FromTicks(-1)));
        Assert.Throws<ArgumentOutOfRangeException>(() => effect.GetNextFrameDelay(TimeSpan.FromTicks(-1)));
        Assert.Equal(Pid02E0LightingTiming.AnimatedFrameInterval, effect.GetNextFrameDelay(TimeSpan.Zero));
        Assert.Equal(TimeSpan.FromTicks(TimeSpan.TicksPerSecond / 60),
            ProductionLightingPort.ApplyRefreshRateToDelay(effect,
                Pid02E0LightingTiming.AnimatedFrameInterval, LightingRefreshRate.SixtyHz));
    }
}
