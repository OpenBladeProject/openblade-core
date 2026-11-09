using System.Buffers.Binary;
using System.Runtime.CompilerServices;

namespace OpenBlade.Tray.Tests;

public sealed class TrayIconAssetTests
{
    [Fact]
    public void EveryStateHasBothThemesAndAllNativeTraySizes()
    {
        string root = RepositoryRoot();
        string brand = Path.Combine(root, "src", "OpenBlade.Tray", "Assets", "Brand");
        string[] keys = ["ac-balanced", "ac-silent", "ac-performance", "ac-custom",
            "usb-c-balanced", "battery-balanced", "battery-saver", "ac-hyperboost"];
        TrayIconState[] states = [TrayIconState.AcBalanced, TrayIconState.AcSilent,
            TrayIconState.AcPerformance, TrayIconState.AcCustom, TrayIconState.UsbCBalanced,
            TrayIconState.BatteryBalanced, TrayIconState.BatterySaver, TrayIconState.AcHyperBoost];
        Assert.Equal(Enum.GetValues<TrayIconState>().Length - 1, states.Length);
        for (int i = 0; i < states.Length; i++)
        {
            Assert.Equal(Path.Combine("Tray", $"{keys[i]}-dark.ico"),
                TrayIconAssetPath.Resolve(states[i], useLightGlyphs: false));
        }

        foreach (string theme in new[] { "dark", "light" })
        {
            var distinct = new HashSet<string>(StringComparer.Ordinal);
            foreach (string relativePath in Enum.GetValues<TrayIconState>()
                .SelectMany(state => new[] { false, true }
                    .Select(discharge => TrayIconAssetPath.Resolve(state, theme == "light", discharge)))
                .Distinct(StringComparer.Ordinal))
            {
                byte[] data = File.ReadAllBytes(Path.Combine(brand, relativePath));
                Assert.True(distinct.Add(Convert.ToBase64String(data)), $"Duplicate icon: {relativePath}");
                Assert.Equal(0, BinaryPrimitives.ReadUInt16LittleEndian(data));
                Assert.Equal(1, BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(2)));
                Assert.Equal(5, BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(4)));
                int[] sizes = [16, 20, 24, 32, 48];
                for (int frame = 0; frame < sizes.Length; frame++)
                {
                    int entry = 6 + frame * 16;
                    Assert.Equal(sizes[frame], data[entry]);
                    Assert.Equal(sizes[frame], data[entry + 1]);
                    int length = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(entry + 8)));
                    int offset = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(entry + 12)));
                    Assert.InRange(offset, 86, data.Length - length);
                    Assert.Equal(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }, data[offset..(offset + 8)]);
                    Assert.Equal(sizes[frame], BinaryPrimitives.ReadInt32BigEndian(data.AsSpan(offset + 16)));
                    Assert.Equal(sizes[frame], BinaryPrimitives.ReadInt32BigEndian(data.AsSpan(offset + 20)));
                }
            }
        }
    }

    [Theory]
    [InlineData(TrayIconState.AcBalanced, "ac-balanced")]
    [InlineData(TrayIconState.AcSilent, "ac-silent")]
    [InlineData(TrayIconState.AcPerformance, "ac-performance")]
    [InlineData(TrayIconState.AcCustom, "ac-custom")]
    [InlineData(TrayIconState.AcHyperBoost, "ac-hyperboost")]
    [InlineData(TrayIconState.UsbCBalanced, "usb-c-balanced")]
    public void DischargeBadgeRetainsTheSpecificModeAndPowerSource(TrayIconState state, string key)
    {
        foreach (bool light in new[] { false, true })
        {
            string theme = light ? "light" : "dark";
            Assert.Equal(Path.Combine("Tray", $"{key}-discharging-{theme}.ico"),
                TrayIconAssetPath.Resolve(state, light, externalPowerDischarging: true));
            Assert.Equal(Path.Combine("Tray", $"{key}-{theme}.ico"),
                TrayIconAssetPath.Resolve(state, light, externalPowerDischarging: false));
        }
    }

    [Theory]
    [InlineData(TrayIconState.GenericError)]
    [InlineData(TrayIconState.BatteryBalanced)]
    [InlineData(TrayIconState.BatterySaver)]
    [InlineData((TrayIconState)999)]
    public void DischargeCannotBadgeUnknownErrorOrBatteryPresentation(TrayIconState state)
    {
        Assert.False(TrayIconStateResolver.CanShowDischargeBadge(state));
        Assert.Equal(TrayIconAssetPath.Resolve(state, true), TrayIconAssetPath.Resolve(state, true, true));
    }

    private static string RepositoryRoot([CallerFilePath] string path = "") =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(path)!, "..", ".."));
}
