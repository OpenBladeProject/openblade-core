using System.Xml.Linq;

namespace OpenBlade.Tray.Tests;

public sealed class AppTypographyContractTests
{
    [Fact]
    public void ApplicationMergesTypographyAfterNativeControlResources()
    {
        XDocument application = XDocument.Parse(ReadRepositoryFile(
            "src", "OpenBlade.Tray", "App.xaml"));
        XElement mergedDictionaries = Assert.Single(application.Descendants(),
            element => element.Name.LocalName == "ResourceDictionary.MergedDictionaries");
        XElement[] resources = mergedDictionaries.Elements().ToArray();

        Assert.Equal("XamlControlsResources", resources[0].Name.LocalName);
        Assert.Equal("ResourceDictionary", resources[1].Name.LocalName);
        Assert.Equal("Themes/Typography.xaml", Attribute(resources[1], "Source"));
    }

    [Fact]
    public void TypographyUsesSemanticWinUiBaseStyles()
    {
        XDocument typography = ReadTypography();

        AssertStyle(typography, "AppPageTitleTextStyle", "{StaticResource TitleTextBlockStyle}");
        AssertStyle(typography, "AppSectionTitleTextStyle", "{StaticResource SubtitleTextBlockStyle}");
        AssertStyle(typography, "AppBodyTextStyle", "{StaticResource BodyTextBlockStyle}");
        AssertStyle(typography, "AppBodyStrongTextStyle", "{StaticResource BodyStrongTextBlockStyle}");
        XElement caption = AssertStyle(
            typography,
            "AppCaptionTextStyle",
            "{StaticResource CaptionTextBlockStyle}");
        AssertSetter(
            caption,
            "Foreground",
            "{ThemeResource TextFillColorSecondaryBrush}");
        XElement data = AssertStyle(
            typography,
            "AppDataTextStyle",
            "{StaticResource BodyTextBlockStyle}");
        AssertSetter(data, "Typography.NumeralAlignment", "Tabular");

        Assert.DoesNotContain(typography.Descendants(), element =>
            Attribute(element, "Property") is "FontFamily" or "FontSize" or "LineHeight");
    }

    [Fact]
    public void CommandButtonPreservesNativeBehaviorAndSharedMinimumSize()
    {
        XDocument typography = ReadTypography();
        XElement commandTextTemplate = Assert.Single(typography.Descendants(), element =>
            element.Name.LocalName == "DataTemplate"
            && Attribute(element, "Key") == "AppCommandTextTemplate");
        XElement commandText = Assert.Single(commandTextTemplate.Elements(), element =>
            element.Name.LocalName == "TextBlock");
        Assert.Equal("{Binding}", Attribute(commandText, "Text"));
        Assert.Equal("{StaticResource AppBodyTextStyle}", Attribute(commandText, "Style"));
        Assert.Equal("Center", Attribute(commandText, "TextAlignment"));
        Assert.Equal("Wrap", Attribute(commandText, "TextWrapping"));

        XElement command = AssertStyle(
            typography,
            "AppCommandButtonStyle",
            "{StaticResource DefaultButtonStyle}",
            targetType: "Button");

        AssertSetter(command, "MinHeight", "40");
        AssertSetter(command, "Padding", "12,6");
        AssertSetter(command, "HorizontalContentAlignment", "Center");
        AssertSetter(command, "VerticalContentAlignment", "Center");
        AssertSetter(command, "ContentTemplate", "{StaticResource AppCommandTextTemplate}");
        Assert.DoesNotContain(command.Elements(), setter =>
            Attribute(setter, "Property") is "Height" or "FontSize" or "FontFamily");
    }

    [Fact]
    public void RuntimeHelperExposesEveryApplicationStyle()
    {
        string helper = ReadRepositoryFile(
            "src", "OpenBlade.Tray", "AppTypography.cs");

        Assert.Contains("PageTitle = \"AppPageTitleTextStyle\"", helper, StringComparison.Ordinal);
        Assert.Contains("SectionTitle = \"AppSectionTitleTextStyle\"", helper, StringComparison.Ordinal);
        Assert.Contains("Body = \"AppBodyTextStyle\"", helper, StringComparison.Ordinal);
        Assert.Contains("BodyStrong = \"AppBodyStrongTextStyle\"", helper, StringComparison.Ordinal);
        Assert.Contains("Caption = \"AppCaptionTextStyle\"", helper, StringComparison.Ordinal);
        Assert.Contains("Data = \"AppDataTextStyle\"", helper, StringComparison.Ordinal);
        Assert.Contains("CommandButton = \"AppCommandButtonStyle\"", helper, StringComparison.Ordinal);
        Assert.Contains("textBlock.Style = RequireStyle(key);", helper, StringComparison.Ordinal);
        Assert.Contains("Application.Current.Resources[key]", helper, StringComparison.Ordinal);
    }

    private static XDocument ReadTypography() => XDocument.Parse(ReadRepositoryFile(
        "src", "OpenBlade.Tray", "Themes", "Typography.xaml"));

    private static XElement AssertStyle(
        XDocument document,
        string key,
        string basedOn,
        string targetType = "TextBlock")
    {
        XElement style = Assert.Single(document.Descendants(), element =>
            element.Name.LocalName == "Style" && Attribute(element, "Key") == key);
        Assert.Equal(targetType, Attribute(style, "TargetType"));
        Assert.Equal(basedOn, Attribute(style, "BasedOn"));
        return style;
    }

    private static void AssertSetter(XElement style, string property, string value)
    {
        XElement setter = Assert.Single(style.Elements(), element =>
            element.Name.LocalName == "Setter"
            && Attribute(element, "Property") == property);
        Assert.Equal(value, Attribute(setter, "Value"));
    }

    private static string? Attribute(XElement element, string localName) =>
        element.Attributes().FirstOrDefault(attribute =>
            attribute.Name.LocalName == localName)?.Value;

    private static string ReadRepositoryFile(params string[] parts)
    {
        string? directory = AppContext.BaseDirectory;
        while (directory is not null
            && !File.Exists(Path.Combine(directory, "OpenBlade.slnx")))
        {
            directory = Directory.GetParent(directory)?.FullName;
        }

        Assert.False(string.IsNullOrWhiteSpace(directory));
        return File.ReadAllText(Path.Combine([directory!, .. parts]));
    }
}
