using OpenBlade.Tray;

namespace OpenBlade.Tray.Tests;

public sealed class PageHeaderLayoutPolicyTests
{
    [Fact]
    public void SaveOnlyUsesTitleRowWhenItFits()
    {
        PageHeaderProfileActionPlacement placement = PageHeaderLayoutPolicy.Resolve(
            availableWidth: 620,
            titleWidth: 120,
            profileActionsWidth: 180,
            customizeWidth: 110,
            profileActionsVisible: true);

        Assert.Equal(PageHeaderProfileActionPlacement.TitleRow, placement);
    }

    [Fact]
    public void RestoreOnlyUsesTitleRowWhenItFits()
    {
        PageHeaderProfileActionPlacement placement = PageHeaderLayoutPolicy.Resolve(
            availableWidth: 540,
            titleWidth: 120,
            profileActionsWidth: 150,
            customizeWidth: 110,
            profileActionsVisible: true);

        Assert.Equal(PageHeaderProfileActionPlacement.TitleRow, placement);
    }

    [Fact]
    public void SaveAndRestoreMoveTogetherToLowerRowWhenTitleRowIsConstrained()
    {
        PageHeaderProfileActionPlacement placement = PageHeaderLayoutPolicy.Resolve(
            availableWidth: 520,
            titleWidth: 140,
            profileActionsWidth: 310,
            customizeWidth: 110,
            profileActionsVisible: true);

        Assert.Equal(PageHeaderProfileActionPlacement.LowerRowHorizontal, placement);
    }

    [Fact]
    public void LongLocalizedActionsStackInsteadOfClipping()
    {
        PageHeaderProfileActionPlacement placement = PageHeaderLayoutPolicy.Resolve(
            availableWidth: 520,
            titleWidth: 140,
            profileActionsWidth: 610,
            customizeWidth: 160,
            profileActionsVisible: true);

        Assert.Equal(PageHeaderProfileActionPlacement.LowerRowVertical, placement);
    }

    [Fact]
    public void HiddenActionsDoNotReserveAHeaderRow()
    {
        PageHeaderProfileActionPlacement placement = PageHeaderLayoutPolicy.Resolve(
            availableWidth: 520,
            titleWidth: 140,
            profileActionsWidth: 310,
            customizeWidth: 110,
            profileActionsVisible: false);

        Assert.Equal(PageHeaderProfileActionPlacement.Hidden, placement);
    }

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(0)]
    public void InvalidMeasurementsFailToVerticalLowerRow(double invalidWidth)
    {
        PageHeaderProfileActionPlacement placement = PageHeaderLayoutPolicy.Resolve(
            availableWidth: invalidWidth,
            titleWidth: 140,
            profileActionsWidth: 310,
            customizeWidth: 110,
            profileActionsVisible: true);

        Assert.Equal(PageHeaderProfileActionPlacement.LowerRowVertical, placement);
    }
}
