using System.Text.RegularExpressions;

namespace OpenBlade.Service.Tests;

public sealed partial class BrightnessHidDriverSourceContractTests
{
    [Fact]
    public void ReportStructures_ArePackedAndAssertedAtThreeBytes()
    {
        string header = File.ReadAllText(DriverFile("BrightnessHid.h"));

        Assert.Contains("#pragma pack(push, 1)", header, StringComparison.Ordinal);
        Assert.Contains("#pragma pack(pop)", header, StringComparison.Ordinal);
        Assert.Contains(
            "C_ASSERT(sizeof(OPENBLADE_INPUT_REPORT) == 3);",
            header,
            StringComparison.Ordinal);
        Assert.Contains(
            "C_ASSERT(sizeof(OPENBLADE_FEATURE_REPORT) == 3);",
            header,
            StringComparison.Ordinal);
    }

    [Fact]
    public void ReportDescriptor_DeclaresConsumerBrightnessAndRestrictedControlCollections()
    {
        string source = File.ReadAllText(DriverFile("BrightnessHid.c"));
        string descriptor = ReportDescriptorRegex().Match(source).Value;

        Assert.Contains("0x05, 0x0C", descriptor, StringComparison.Ordinal);
        Assert.Contains("0x26, 0xFF, 0x03", descriptor, StringComparison.Ordinal);
        Assert.Contains("0x75, 0x10", descriptor, StringComparison.Ordinal);
        Assert.Contains("0x81, 0x00", descriptor, StringComparison.Ordinal);
        Assert.Contains("0x06, 0x00, 0xFF", descriptor, StringComparison.Ordinal);
        Assert.Contains("0xB1, 0x00", descriptor, StringComparison.Ordinal);
    }

    [Fact]
    public void Package_UsesExactIdentityNamesAndSystemAdminDriverAcl()
    {
        string inf = File.ReadAllText(DriverFile("OpenBlade.BrightnessHid.inx"));

        Assert.Contains(
            @"ROOT\OSSBLADE_OPENBLADE_BRIGHTNESS_HID",
            inf,
            StringComparison.Ordinal);
        Assert.Contains("CatalogFile=OpenBlade.BrightnessHid.cat", inf, StringComparison.Ordinal);
        Assert.Contains("OpenBlade.BrightnessHid.dll", inf, StringComparison.Ordinal);
        Assert.Contains(
            "D:P(A;;GA;;;SY)(A;;GA;;;BA)(A;;GA;;;UD)",
            inf,
            StringComparison.Ordinal);
    }

    private static string DriverFile(string fileName) => Path.Combine(
        FindRepositoryRoot(),
        "src",
        "OpenBlade.BrightnessHid",
        fileName);

    private static string FindRepositoryRoot()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "OpenBlade.slnx")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("The OpenBlade repository root was not found.");
    }

    [GeneratedRegex(
        @"static const UCHAR OpenBladeReportDescriptor\[\]\s*=\s*\{.*?\};",
        RegexOptions.Singleline | RegexOptions.CultureInvariant)]
    private static partial Regex ReportDescriptorRegex();
}
