namespace OpenBlade.Tray;

internal enum PageHeaderProfileActionPlacement
{
    Hidden,
    TitleRow,
    LowerRowHorizontal,
    LowerRowVertical,
}

internal static class PageHeaderLayoutPolicy
{
    internal const double MinimumTitleActionGapDip = 16;
    internal const double InterActionGroupGapDip = 12;

    public static PageHeaderProfileActionPlacement Resolve(
        double availableWidth,
        double titleWidth,
        double profileActionsWidth,
        double? customizeWidth,
        bool profileActionsVisible)
    {
        if (!profileActionsVisible)
        {
            return PageHeaderProfileActionPlacement.Hidden;
        }

        if (!IsUsableWidth(titleWidth)
            || !IsUsableWidth(profileActionsWidth)
            || customizeWidth is double visibleCustomizeWidth
                && !IsUsableWidth(visibleCustomizeWidth))
        {
            return PageHeaderProfileActionPlacement.LowerRowVertical;
        }

        if (double.IsPositiveInfinity(availableWidth))
        {
            return PageHeaderProfileActionPlacement.TitleRow;
        }

        if (!IsUsableWidth(availableWidth))
        {
            return PageHeaderProfileActionPlacement.LowerRowVertical;
        }

        double titleRowWidth = titleWidth
            + MinimumTitleActionGapDip
            + profileActionsWidth;
        if (customizeWidth is double measuredCustomizeWidth)
        {
            titleRowWidth += InterActionGroupGapDip + measuredCustomizeWidth;
        }
        if (availableWidth + 0.5 >= titleRowWidth)
        {
            return PageHeaderProfileActionPlacement.TitleRow;
        }

        return availableWidth + 0.5 >= profileActionsWidth
            ? PageHeaderProfileActionPlacement.LowerRowHorizontal
            : PageHeaderProfileActionPlacement.LowerRowVertical;
    }

    private static bool IsUsableWidth(double value) =>
        double.IsFinite(value) && value > 0;
}
