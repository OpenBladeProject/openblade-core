using OpenBlade.Tray;

namespace OpenBlade.Tray.Tests;

public sealed class IntegerEditorValueTests
{
    [Theory]
    [InlineData(0, 0d)]
    [InlineData(80, 80d)]
    [InlineData(255, 255d)]
    public void ToDouble_PreservesEditorValue(int value, double expected) =>
        Assert.Equal(expected, IntegerEditorValue.ToDouble(value));

    [Theory]
    [InlineData(63.5, 64)]
    [InlineData(63.49, 63)]
    [InlineData(-63.5, -64)]
    [InlineData(-63.49, -63)]
    public void ToInt32_RoundsMidpointsAwayFromZero(double value, int expected) =>
        Assert.Equal(expected, IntegerEditorValue.ToInt32(value));

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(double.NegativeInfinity)]
    public void ToInt32_RejectsNonFiniteValues(double value) =>
        Assert.Throws<ArgumentOutOfRangeException>(() => IntegerEditorValue.ToInt32(value));

    [Theory]
    [InlineData(2147483648d)]
    [InlineData(-2147483649d)]
    [InlineData(double.MaxValue)]
    [InlineData(double.MinValue)]
    public void ToInt32_RejectsFiniteOverflow(double value) =>
        Assert.Throws<OverflowException>(() => IntegerEditorValue.ToInt32(value));
}
