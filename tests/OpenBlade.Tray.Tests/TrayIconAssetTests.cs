using System.Buffers.Binary;
using System.Runtime.CompilerServices;

namespace OpenBlade.Tray.Tests;

public sealed class TrayIconAssetTests
{
    [Fact]
    public void EveryStateHasBothThemesAndAllNativeTraySizes()
    {
        string root = RepositoryRoot();
        string loader = File.ReadAllText(Path.Combine(root, "src", "OpenBlade.Tray", "OpenBladeIconAssets.cs"));
        string brand = Path.Combine(root, "src", "OpenBlade.Tray", "Assets", "Brand");
        string[] keys = ["ac-balanced", "ac-silent", "ac-performance", "ac-custom",
            "usb-c-balanced", "battery-balanced", "battery-saver", "ac-hyperboost",
            "external-power-discharging"];
        TrayIconState[] states = [TrayIconState.AcBalanced, TrayIconState.AcSilent,
            TrayIconState.AcPerformance, TrayIconState.AcCustom, TrayIconState.UsbCBalanced,
            TrayIconState.BatteryBalanced, TrayIconState.BatterySaver, TrayIconState.AcHyperBoost,
            TrayIconState.ExternalPowerDischarging];
        Assert.Equal(Enum.GetValues<TrayIconState>().Length - 1, states.Length);
        for (int i = 0; i < states.Length; i++)
        {
            Assert.Contains($"[TrayIconState.{states[i]}] = \"{keys[i]}\"", loader, StringComparison.Ordinal);
        }

        foreach (string theme in new[] { "dark", "light" })
        {
            var distinct = new HashSet<string>(StringComparer.Ordinal);
            foreach (string key in keys.Append("generic-error"))
            {
                string folder = key == "generic-error" ? "TrayStatus" : "Tray";
                byte[] data = File.ReadAllBytes(Path.Combine(brand, folder, $"{key}-{theme}.ico"));
                Assert.True(distinct.Add(Convert.ToBase64String(data)), $"Duplicate icon: {key}/{theme}");
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

    private static string RepositoryRoot([CallerFilePath] string path = "") =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(path)!, "..", ".."));
}
