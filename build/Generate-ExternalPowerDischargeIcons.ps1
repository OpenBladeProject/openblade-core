[CmdletBinding()]
param(
    [string]$Repository,
    [string]$PreviewPath,
    [string]$DetailPath
)

$ErrorActionPreference = 'Stop'
if ([string]::IsNullOrWhiteSpace($Repository)) {
    $Repository = Split-Path -Parent $PSScriptRoot
}
Add-Type -AssemblyName System.Drawing
Add-Type -ReferencedAssemblies System.Drawing -TypeDefinition @'
using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Globalization;
using System.IO;
using System.Text;

public static class ExternalPowerDischargeIcons
{
    private static readonly string[] Bases = {
        "ac-balanced", "ac-silent", "ac-performance", "ac-custom",
        "ac-hyperboost", "usb-c-balanced"
    };
    private static readonly int[] Sizes = { 16, 20, 24, 32, 48 };
    // A lower-right status badge, opposite the existing source badge.
    // Its halo follows the arrow contour; it never clears a rectangular tile.
    private static readonly PointF[][] Arrow = {
        new[] { new PointF(13, 11.1f), new PointF(13, 14.8f) },
        new[] { new PointF(11.2f, 13), new PointF(13, 14.8f), new PointF(14.8f, 13) }
    };
    private const float ArrowWidth = 1.4f;
    private const float ClearanceWidth = ArrowWidth + 1.5f;

    private static string ArrowData()
    {
        var data = new StringBuilder();
        foreach (var segment in Arrow)
        for (int i = 0; i < segment.Length; i++)
        {
            data.Append(i == 0 ? "M" : " L");
            data.Append(segment[i].X.ToString(CultureInfo.InvariantCulture));
            data.Append(" ");
            data.Append(segment[i].Y.ToString(CultureInfo.InvariantCulture));
            data.Append(" ");
        }
        return data.ToString().Trim();
    }

    private static void WriteSvg(string directory)
    {
        string data = ArrowData();
        string svg =
            "<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"16\" height=\"16\" viewBox=\"0 0 16 16\">\n" +
            "  <title>OpenBlade battery discharge badge</title>\n" +
            "  <desc>Apply discharge-clearance to the existing mode and source icon, then draw this badge above it.</desc>\n" +
            "  <defs><mask id=\"discharge-clearance\" maskUnits=\"userSpaceOnUse\" x=\"0\" y=\"0\" width=\"16\" height=\"16\" style=\"mask-type:luminance\">\n" +
            "    <rect width=\"16\" height=\"16\" fill=\"white\"/>\n" +
            "    <path d=\"" + data + "\" fill=\"none\" stroke=\"black\" stroke-width=\"" + ClearanceWidth.ToString(CultureInfo.InvariantCulture) + "\" stroke-linecap=\"round\" stroke-linejoin=\"round\"/>\n" +
            "  </mask></defs>\n" +
            "  <path d=\"" + data + "\" fill=\"none\" stroke=\"currentColor\" stroke-width=\"" + ArrowWidth.ToString(CultureInfo.InvariantCulture) + "\" stroke-linecap=\"round\" stroke-linejoin=\"round\"/>\n" +
            "</svg>\n";
        File.WriteAllText(Path.Combine(directory, "external-power-discharge-badge.svg"),
            svg.Replace("\n", "\r\n"), new UTF8Encoding(false));
    }

    private static Bitmap Coverage(int size, float width)
    {
        using (var canvas = new Bitmap(size * 8, size * 8))
        using (var g = Graphics.FromImage(canvas))
        using (var path = new GraphicsPath())
        using (var pen = new Pen(Color.White, width))
        {
            g.Clear(Color.Black);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.ScaleTransform(size / 2f, size / 2f);
            pen.StartCap = pen.EndCap = LineCap.Round;
            pen.LineJoin = LineJoin.Round;
            foreach (var segment in Arrow)
            {
                path.StartFigure();
                path.AddLines(segment);
            }
            g.DrawPath(pen, path);
            var result = new Bitmap(size, size);
            using (var output = Graphics.FromImage(result))
            {
                output.InterpolationMode = InterpolationMode.HighQualityBicubic;
                output.PixelOffsetMode = PixelOffsetMode.HighQuality;
                output.DrawImage(canvas, new Rectangle(0, 0, size, size));
            }
            return result;
        }
    }

    private static Bitmap Compose(Bitmap basis, Color color)
    {
        // Keep the native approved frame: no scaling or rerasterization of its
        // mode or source glyph. Only pixels covered by the badge/halo change.
        var result = (Bitmap)basis.Clone();
        using (var clearance = Coverage(basis.Width, ClearanceWidth))
        using (var arrow = Coverage(basis.Width, ArrowWidth))
        {
            for (int y = 0; y < basis.Height; y++)
            for (int x = 0; x < basis.Width; x++)
            {
                int cut = clearance.GetPixel(x, y).R;
                int ink = arrow.GetPixel(x, y).R;
                if (cut == 0 && ink == 0) { continue; }
                double remaining = basis.GetPixel(x, y).A * (255-cut) / 255.0;
                int alpha = (int)Math.Round(ink + remaining * (255-ink) / 255.0);
                result.SetPixel(x, y, Color.FromArgb(alpha, color));
            }
        }
        return result;
    }

    private static Bitmap ReadFrame(string path, int size)
    {
        using (var reader = new BinaryReader(File.OpenRead(path)))
        {
            if (reader.ReadUInt16() != 0 || reader.ReadUInt16() != 1)
                throw new InvalidDataException("Not an ICO: " + path);
            int count = reader.ReadUInt16();
            for (int i = 0; i < count; i++)
            {
                int width = reader.ReadByte();
                reader.ReadBytes(7);
                int length = reader.ReadInt32();
                int offset = reader.ReadInt32();
                if (width != size) { continue; }
                reader.BaseStream.Position = offset;
                using (var stream = new MemoryStream(reader.ReadBytes(length)))
                using (var image = (Bitmap)Image.FromStream(stream))
                {
                    // Drawing an Image into a Bitmap round-trips premultiplied
                    // alpha and changes edge RGB. Copy exact decoded pixels.
                    var result = new Bitmap(image.Width, image.Height);
                    for (int y = 0; y < image.Height; y++)
                    for (int x = 0; x < image.Width; x++)
                        result.SetPixel(x, y, image.GetPixel(x, y));
                    return result;
                }
            }
        }
        throw new InvalidDataException("Missing native ICO frame: " + path + " at " + size);
    }

    private static void WriteIco(string path, byte[][] frames)
    {
        using (var writer = new BinaryWriter(File.Create(path)))
        {
            writer.Write((ushort)0); writer.Write((ushort)1); writer.Write((ushort)Sizes.Length);
            int offset = 6 + Sizes.Length * 16;
            for (int i = 0; i < Sizes.Length; i++)
            {
                writer.Write((byte)Sizes[i]); writer.Write((byte)Sizes[i]);
                writer.Write((byte)0); writer.Write((byte)0);
                writer.Write((ushort)1); writer.Write((ushort)32);
                writer.Write(frames[i].Length); writer.Write(offset);
                offset += frames[i].Length;
            }
            foreach (byte[] frame in frames) { writer.Write(frame); }
        }
    }

    public static void Write(string directory, string previewPath, string detailPath)
    {
        WriteSvg(directory);
        foreach (string basis in Bases)
        foreach (string theme in new[] { "light", "dark" })
        {
            Color color = ColorTranslator.FromHtml(theme == "light" ? "#F7F8FA" : "#202124");
            var frames = new byte[Sizes.Length][];
            for (int i = 0; i < Sizes.Length; i++)
            using (var original = ReadFrame(Path.Combine(directory, basis + "-" + theme + ".ico"), Sizes[i]))
            using (var bitmap = Compose(original, color))
            using (var stream = new MemoryStream())
            {
                bitmap.Save(stream, ImageFormat.Png);
                frames[i] = stream.ToArray();
            }
            WriteIco(Path.Combine(directory, basis + "-discharging-" + theme + ".ico"), frames);
        }
        if (!string.IsNullOrEmpty(previewPath)) { Preview(directory, previewPath, false); }
        if (!string.IsNullOrEmpty(detailPath)) { Preview(directory, detailPath, true); }
    }

    private static void DrawIcon(Graphics g, string directory, string name, string theme,
        int size, int x, int y, int scale)
    {
        using (var frame = ReadFrame(Path.Combine(directory, name + "-" + theme + ".ico"), size))
        {
            if (scale == 1) { g.DrawImageUnscaled(frame, x, y); }
            else
            {
                g.InterpolationMode = InterpolationMode.NearestNeighbor;
                g.PixelOffsetMode = PixelOffsetMode.Half;
                g.DrawImage(frame, x, y, size*scale, size*scale);
            }
        }
    }

    private static void Preview(string directory, string path, bool detail)
    {
        int panelHeight = detail ? 760 : 530;
        int rowHeight = detail ? 104 : 66;
        string[] labels = { "AC / Balanced", "AC / Silent", "AC / Performance", "AC / Custom", "AC / HyperBoost", "USB-C / Balanced" };
        using (var preview = new Bitmap(1120, panelHeight*2))
        using (var g = Graphics.FromImage(preview))
        using (var font = new Font("Segoe UI", 10))
        using (var heading = new Font("Segoe UI", 12, FontStyle.Bold))
        {
            for (int themeIndex = 0; themeIndex < 2; themeIndex++)
            using (var bg = new SolidBrush(themeIndex == 0 ? Color.FromArgb(32,32,32) : Color.White))
            using (var text = new SolidBrush(themeIndex == 0 ? Color.FromArgb(247,248,250) : Color.FromArgb(32,33,36)))
            {
                string theme = themeIndex == 0 ? "light" : "dark";
                int top = themeIndex * panelHeight;
                g.FillRectangle(bg, 0, top, 1120, panelHeight);
                g.DrawString(themeIndex == 0 ? "Dark taskbar / light glyphs" : "Light taskbar / dark glyphs", heading, text, 20, top+12);
                g.DrawString("Normal  /  discharging (same mode and power source)", font, text, 20, top+43);
                int[] sizes = detail ? new[] { 16, 20, 48 } : Sizes;
                for (int column = 0; column < sizes.Length; column++)
                {
                    int scale = detail ? (sizes[column] == 48 ? 2 : 4) : 1;
                    int columnX = 240 + column*(detail ? 280 : 170);
                    g.DrawString(sizes[column] + " px" + (detail ? " at " + scale + "x" : ""), font, text, columnX, top+74);
                    for (int row = 0; row < Bases.Length; row++)
                    {
                        int y = top + 112 + row*rowHeight;
                        if (column == 0) g.DrawString(labels[row], font, text, 20, y+8);
                        int gap = detail ? 125 : 72;
                        DrawIcon(g, directory, Bases[row], theme, sizes[column], columnX, y, scale);
                        DrawIcon(g, directory, Bases[row]+"-discharging", theme, sizes[column], columnX+gap, y, scale);
                    }
                }
            }
            preview.Save(path, ImageFormat.Png);
        }
    }
}
'@

[ExternalPowerDischargeIcons]::Write(
    (Join-Path $Repository 'src/OpenBlade.Tray/Assets/Brand/Tray'), $PreviewPath, $DetailPath)
