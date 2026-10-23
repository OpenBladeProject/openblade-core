using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace OpenBlade.Tray;

internal static class AppTypography
{
    public const string PageTitle = "AppPageTitleTextStyle";
    public const string SectionTitle = "AppSectionTitleTextStyle";
    public const string Body = "AppBodyTextStyle";
    public const string BodyStrong = "AppBodyStrongTextStyle";
    public const string Caption = "AppCaptionTextStyle";
    public const string Data = "AppDataTextStyle";
    public const string CustomCommandButton = "AppCustomCommandButtonStyle";
    public const string CommandButton = "AppCommandButtonStyle";
    public const string ToolbarButton = "AppToolbarButtonStyle";
    public const string ToolbarToggleButton = "AppToolbarToggleButtonStyle";
    public const string ToolbarIcon = "AppToolbarIconStyle";
    public const string InlineCommand = "AppInlineCommandStyle";

    public static void Apply(TextBlock textBlock, string key)
    {
        ArgumentNullException.ThrowIfNull(textBlock);
        textBlock.Style = RequireStyle(key);
    }

    public static Style RequireStyle(string key)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        object resource = Microsoft.UI.Xaml.Application.Current.Resources[key];
        return resource as Style
            ?? throw new InvalidOperationException(
                $"Application resource '{key}' is not a Style.");
    }
}
