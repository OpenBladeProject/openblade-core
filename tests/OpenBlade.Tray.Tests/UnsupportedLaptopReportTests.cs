using System.IO.Compression;
using System.Text;
using OpenBlade.Contracts;
using OpenBlade.Tray;

namespace OpenBlade.Tray.Tests;

public sealed class UnsupportedLaptopReportTests
{
    [Fact]
    public void Create_KeepsOnlyIdentityEntriesByteForByte()
    {
        DiagnosticArchive source = Build(
            ("versions.json", "{\"v\":1}"),
            ("device-verification.json", "{\"d\":2}"),
            ("device.json", "{\"serial\":\"x\"}"),
            ("logs.jsonl", "log"),
            ("configuration.json", "{}"));

        DiagnosticArchive report = UnsupportedLaptopReport.Create(source);

        Assert.Equal("OpenBlade-identity-report.zip", report.SuggestedFileName);
        Assert.Equal(UnsupportedLaptopReport.SuggestedFileName, report.SuggestedFileName);
        using var zip = new ZipArchive(new MemoryStream(report.ZipBytes));
        Assert.Equal(
            ["device-verification.json", "versions.json"],
            zip.Entries.Select(entry => entry.FullName).Order(StringComparer.Ordinal));
        Assert.Equal("{\"v\":1}", Read(zip, "versions.json"));
        Assert.Equal("{\"d\":2}", Read(zip, "device-verification.json"));
    }

    [Fact]
    public void Create_AllowsMissingVersions()
    {
        DiagnosticArchive report = UnsupportedLaptopReport.Create(
            Build(("device-verification.json", "{}")));

        using var zip = new ZipArchive(new MemoryStream(report.ZipBytes));
        Assert.Equal("device-verification.json", Assert.Single(zip.Entries).FullName);
    }

    [Fact]
    public void Create_RequiresDeviceVerification()
    {
        Assert.Throws<InvalidDataException>(() => UnsupportedLaptopReport.Create(
            Build(("versions.json", "{}"), ("device.json", "{}"))));
    }

    [Fact]
    public void Create_RejectsUnreadableArchive()
    {
        Assert.Throws<InvalidDataException>(() => UnsupportedLaptopReport.Create(
            new DiagnosticArchive("x.zip", [0x50, 0x4B, 0x03, 0x04, 0x01])));
    }

    [Fact]
    public void IssueFormUri_IsTheDeviceSupportTemplate()
    {
        Assert.Equal(
            "https://github.com/OpenBladeProject/openblade-core/issues/new?template=device-support.yml",
            UnsupportedLaptopReport.IssueFormUri.ToString());
        Assert.Equal(
            ["versions.json", "device-verification.json"],
            UnsupportedLaptopReport.IncludedEntries);
    }

    private static DiagnosticArchive Build(params (string Name, string Content)[] entries)
    {
        using var output = new MemoryStream();
        using (var zip = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach ((string name, string content) in entries)
            {
                using Stream stream = zip.CreateEntry(name).Open();
                stream.Write(Encoding.UTF8.GetBytes(content));
            }
        }

        return new DiagnosticArchive("OpenBlade-diagnostics.zip", output.ToArray());
    }

    private static string Read(ZipArchive zip, string name)
    {
        using var reader = new StreamReader(zip.GetEntry(name)!.Open(), Encoding.UTF8);
        return reader.ReadToEnd();
    }
}
