using OpenBlade.Contracts;

namespace OpenBlade.Tray;

public static partial class RazerChromaFileAdapter
{
    private static IReadOnlyDictionary<int, byte> CreateBlade18Projection(byte? region)
    {
        // Razer's generic RZKEY canvas, not another laptop's HID coordinates:
        // HTML5ChromaSDK ec61d7a1924a6facc0e136a84a00b8b87f7ce4ba/ChromaSDKImpl.js.
        // Scan codes resolve through the pinned exact-product regional definitions.
        var map = new Dictionary<int, byte>();
        void Add(int key, ushort scan, bool extended = false)
        {
            if (Blade18KeyboardLightingLayout.TryMapScanCode(region, scan, extended, out byte slot))
            {
                map.Add(((key >> 8) * 22) + (key & 255), slot);
            }
        }
        Add(0x0001, 0x01);
        for (int i = 0; i < 10; i++)
        {
            Add(0x0003 + i, (ushort)(0x3b + i));
            Add(0x0102 + i, (ushort)(0x02 + i));
            Add(0x0202 + i, (ushort)(0x10 + i));
        }
        Add(0x000d, 0x57);
        Add(0x000e, 0x58);
        Add(0x000f, 0x37, true);
        Add(0x0101, 0x29);
        Add(0x010c, 0x0c);
        Add(0x010d, 0x0d);
        Add(0x010e, 0x0e);
        Add(0x010f, 0x52, true);
        Add(0x0201, 0x0f);
        Add(0x020c, 0x1a);
        Add(0x020d, 0x1b);
        // ISO's OEM5 is the # key on the home row; ANSI/Japanese use OEM6.
        Add(region is 3 or 4 or 6 or 7 or 12 ? 0x030d : 0x020e, 0x2b);
        Add(0x020f, 0x53, true);
        Add(0x0301, 0x3a);
        for (int i = 0; i < 11; i++)
        {
            Add(0x0302 + i, (ushort)(0x1e + i));
        }
        Add(0x030e, 0x1c);
        Add(0x0401, 0x2a);
        Add(0x0402, 0x56);
        for (int i = 0; i < 10; i++)
        {
            Add(0x0403 + i, (ushort)(0x2c + i));
        }
        Add(0x040e, 0x36);
        Add(0x0410, 0x48, true);
        Add(0x0501, 0x1d);
        Add(0x0502, 0x5b, true);
        Add(0x0503, 0x38);
        Add(0x0507, 0x39);
        Add(0x050b, 0x38, true);
        Add(0x050d, 0x5d, true);
        Add(0x050e, 0x1d, true);
        Add(0x050f, 0x4b, true);
        Add(0x0510, 0x50, true);
        Add(0x0511, 0x4d, true);
        Add(0x0015, 0x7d);
        Add(0x040d, 0x73);
        Add(0x0504, 0x7b);
        Add(0x0509, 0x79);
        Add(0x050a, 0x70);
        return map;
    }
}
