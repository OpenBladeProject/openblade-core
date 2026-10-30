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
using System.IO;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

public static class ExternalPowerDischargeIcons
{
    // Same 16-unit silhouette, source badge and clearance as the approved
    // openblade-branding/tools/tray_geometry.py artwork (2026-10-29).
    // One ordered mask definition drives both the SVG and the ICO frames.
    private static readonly string[] Paths = {
        "M2.6 1.5 L13.4 1.5 C14.2836556 1.5 15 2.2163444 15 3.1 L15 10.4 C15 11.2836556 14.2836556 12 13.4 12 L2.6 12 C1.7163444 12 1 11.2836556 1 10.4 L1 3.1 C1 2.2163444 1.7163444 1.5 2.6 1.5 Z",
        "M0.5 13 L15.5 13 L14 14.5 L2 14.5 Z",
        "M8 4.25 L8 9.25 M5.5 6.75 L8 9.25 L10.5 6.75",
        "M1.4 10.5 L2.7 10.5 L2.7 12 L4.3 12 L4.3 10.5 L5.6 10.5 L5.6 12 L6.3 12 L6.3 13.1 Q6.3 14.7 4.2 14.7 L4.2 15.7 L2.8 15.7 L2.8 14.7 Q0.7 14.7 0.7 13.1 L0.7 12 L1.4 12 Z"
    };
    private static readonly int[] PathOrder = { 0, 1, 2, 3, 3, 3 };
    private static readonly bool[] White = { true, true, false, false, false, true };
    private static readonly float[] Stroke = { 0, 0, 1.85f, 0, 1.5f, 0 };

    private static float Number(MatchCollection tokens, ref int index)
    {
        return float.Parse(tokens[index++].Value, CultureInfo.InvariantCulture);
    }

    private static GraphicsPath Shape(string data)
    {
        var path = new GraphicsPath();
        var tokens = Regex.Matches(data, @"[MLCQZ]|-?\d+(?:\.\d+)?");
        PointF current = new PointF();
        for (int i = 0; i < tokens.Count; )
        {
            string command = tokens[i++].Value;
            if (command == "Z") { path.CloseFigure(); continue; }
            var next = new PointF(Number(tokens, ref i), Number(tokens, ref i));
            if (command == "M") { path.StartFigure(); current = next; continue; }
            if (command == "L") { path.AddLine(current, next); current = next; continue; }
            var end = new PointF(Number(tokens, ref i), Number(tokens, ref i));
            if (command == "Q")
            {
                path.AddBezier(current,
                    new PointF(current.X + (next.X-current.X)*2/3, current.Y + (next.Y-current.Y)*2/3),
                    new PointF(end.X + (next.X-end.X)*2/3, end.Y + (next.Y-end.Y)*2/3), end);
            }
            else
            {
                var last = new PointF(Number(tokens, ref i), Number(tokens, ref i));
                path.AddBezier(current, next, end, last);
                end = last;
            }
            current = end;
        }
        return path;
    }

    private static void WriteSvg(string directory)
    {
        var svg = new StringBuilder();
        svg.AppendLine("<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"16\" height=\"16\" viewBox=\"0 0 16 16\">");
        svg.AppendLine("  <title>OpenBlade \u2014 plugged in, battery discharging</title>");
        svg.AppendLine("  <defs><mask id=\"discharge\" maskUnits=\"userSpaceOnUse\" x=\"0\" y=\"0\" width=\"16\" height=\"16\" style=\"mask-type:luminance\">");
        svg.AppendLine("    <rect width=\"16\" height=\"16\" fill=\"black\"/>");
        for (int i = 0; i < PathOrder.Length; i++)
        {
            string color = White[i] ? "white" : "black";
            string style = Stroke[i] == 0 ? "fill=\"" + color + "\"" :
                "fill=\"none\" stroke=\"" + color + "\" stroke-width=\"" + Stroke[i].ToString(CultureInfo.InvariantCulture) + "\" stroke-linecap=\"round\" stroke-linejoin=\"round\"";
            svg.AppendLine("    <path d=\"" + Paths[PathOrder[i]] + "\" " + style + "/>");
        }
        svg.AppendLine("  </mask></defs>");
        svg.AppendLine("  <rect width=\"16\" height=\"16\" fill=\"currentColor\" mask=\"url(#discharge)\"/>");
        svg.AppendLine("</svg>");
        File.WriteAllText(Path.Combine(directory, "external-power-discharging.svg"),
            svg.ToString().Replace("\r\n", "\n").Replace("\n", "\r\n"), new UTF8Encoding(false));
    }

    private static Bitmap Frame(int size, Color color)
    {
        using (var canvas = new Bitmap(size * 8, size * 8))
        using (var g = Graphics.FromImage(canvas))
        {
            g.Clear(Color.Black);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.ScaleTransform(size / 2f, size / 2f);
            for (int i = 0; i < PathOrder.Length; i++)
            using (var shape = Shape(Paths[PathOrder[i]]))
            using (var brush = new SolidBrush(White[i] ? Color.White : Color.Black))
            {
                if (Stroke[i] == 0) { g.FillPath(brush, shape); }
                else using (var pen = new Pen(brush, Stroke[i]))
                {
                    pen.StartCap = pen.EndCap = LineCap.Round;
                    pen.LineJoin = LineJoin.Round;
                    g.DrawPath(pen, shape);
                }
            }
            var result = new Bitmap(size, size);
            using (var output = Graphics.FromImage(result))
            {
                output.CompositingMode = CompositingMode.SourceCopy;
                output.InterpolationMode = InterpolationMode.HighQualityBicubic;
                output.PixelOffsetMode = PixelOffsetMode.HighQuality;
                output.DrawImage(canvas, new Rectangle(0, 0, size, size));
            }
            // Resample coverage only, then assign the family's exact theme RGB.
            // Transparent pixels never carry a taskbar-colored rectangle.
            for (int y = 0; y < size; y++)
            for (int x = 0; x < size; x++)
                result.SetPixel(x, y, Color.FromArgb(result.GetPixel(x, y).R, color));
            return result;
        }
    }

    public static void Write(string directory, string previewPath, string detailPath)
    {
        int[] sizes = { 16, 20, 24, 32, 48 };
        Directory.CreateDirectory(directory);
        WriteSvg(directory);
        for (int theme = 0; theme < 2; theme++)
        {
            Color foreground = ColorTranslator.FromHtml(theme == 0 ? "#F7F8FA" : "#202124");
            var frames = new byte[sizes.Length][];
            for (int i = 0; i < sizes.Length; i++)
            {
                using (var bitmap = Frame(sizes[i], foreground))
                using (var stream = new MemoryStream())
                {
                    bitmap.Save(stream, ImageFormat.Png);
                    frames[i] = stream.ToArray();
                }
            }
            using (var writer = new BinaryWriter(File.Create(Path.Combine(directory,
                "external-power-discharging-" + (theme == 0 ? "light" : "dark") + ".ico"))))
            {
                writer.Write((ushort)0); writer.Write((ushort)1); writer.Write((ushort)sizes.Length);
                int offset = 6 + sizes.Length * 16;
                for (int i = 0; i < sizes.Length; i++)
                {
                    writer.Write((byte)sizes[i]); writer.Write((byte)sizes[i]);
                    writer.Write((byte)0); writer.Write((byte)0);
                    writer.Write((ushort)1); writer.Write((ushort)32);
                    writer.Write(frames[i].Length); writer.Write(offset);
                    offset += frames[i].Length;
                }
                foreach (byte[] frame in frames) { writer.Write(frame); }
            }
        }
        if (!string.IsNullOrEmpty(previewPath)) { Comparison(directory, previewPath); }
        if (!string.IsNullOrEmpty(detailPath)) { Detail(directory, detailPath); }
    }

    private static Bitmap ReadFrame(string path, int size)
    {
        using (var reader = new BinaryReader(File.OpenRead(path)))
        {
            reader.ReadUInt16(); reader.ReadUInt16();
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
                using (var image = Image.FromStream(stream)) { return new Bitmap(image); }
            }
        }
        throw new InvalidDataException("Missing native ICO frame: " + path + " at " + size);
    }

    private static void Comparison(string directory, string previewPath)
    {
        string[] names = { "ac-balanced", "ac-hyperboost", "usb-c-balanced", "battery-balanced", "generic-error", "external-power-discharging" };
        string[] labels = { "AC / Balanced", "AC / HyperBoost", "USB-C / Balanced", "Battery / Balanced", "Unknown / error", "Plugged in / discharging" };
        int[] sizes = { 16, 20, 24, 32, 48 };
        using (var preview = new Bitmap(960, 760))
        using (var g = Graphics.FromImage(preview))
        using (var font = new Font("Segoe UI", 10))
        using (var heading = new Font("Segoe UI", 12, FontStyle.Bold))
        {
            for (int theme = 0; theme < 2; theme++)
            using (var bg = new SolidBrush(theme == 0 ? Color.FromArgb(32, 32, 32) : Color.White))
            using (var text = new SolidBrush(theme == 0 ? Color.FromArgb(247,248,250) : Color.FromArgb(32,33,36)))
            {
                int top = theme * 380;
                g.FillRectangle(bg, 0, top, 960, 380);
                g.DrawString(theme == 0 ? "Dark taskbar / light glyphs" : "Light taskbar / dark glyphs", heading, text, 20, top+12);
                for (int j = 0; j < sizes.Length; j++) g.DrawString(sizes[j] + " px", font, text, 290+j*100, top+50);
                g.DrawString("16 px at 2x", font, text, 810, top+50);
                for (int row = 0; row < names.Length; row++)
                {
                    int y = top+88+row*46;
                    g.DrawString(labels[row], font, text, 20, y);
                    string folder = names[row] == "generic-error" ? Path.Combine(directory, "..", "TrayStatus") : directory;
                    string path = Path.Combine(folder, names[row]+(theme == 0 ? "-light.ico" : "-dark.ico"));
                    for (int j = 0; j < sizes.Length; j++)
                    using (var frame = ReadFrame(path, sizes[j]))
                        g.DrawImageUnscaled(frame, 310+j*100-sizes[j]/2, y+9-sizes[j]/2);
                    using (var frame = ReadFrame(path, 16))
                    {
                        // 2x here keeps the sheet compact; separate detail holds larger views.
                        g.InterpolationMode = InterpolationMode.NearestNeighbor;
                        g.PixelOffsetMode = PixelOffsetMode.Half;
                        g.DrawImage(frame, 823, y-8, 32, 32);
                    }
                }
            }
            preview.Save(previewPath, ImageFormat.Png);
        }
    }

    private static void Detail(string directory, string path)
    {
        using (var preview = new Bitmap(640, 340))
        using (var g = Graphics.FromImage(preview))
        using (var font = new Font("Segoe UI", 11))
        {
            for (int theme = 0; theme < 2; theme++)
            using (var bg = new SolidBrush(theme == 0 ? Color.FromArgb(32,32,32) : Color.White))
            using (var text = new SolidBrush(theme == 0 ? Color.FromArgb(247,248,250) : Color.FromArgb(32,33,36)))
            {
                int left = theme * 320;
                g.FillRectangle(bg, left, 0, 320, 340);
                using (var frame = ReadFrame(Path.Combine(directory,
                    "external-power-discharging-" + (theme == 0 ? "light" : "dark") + ".ico"), 48))
                {
                    g.InterpolationMode = InterpolationMode.NearestNeighbor;
                    g.PixelOffsetMode = PixelOffsetMode.Half;
                    g.DrawImage(frame, left+64, 32, 192, 192);
                }
                g.DrawString("48 px ICO frame at 4x", font, text, left+80, 242);
                foreach (int size in new[] { 16, 20 })
                using (var frame = ReadFrame(Path.Combine(directory,
                    "external-power-discharging-" + (theme == 0 ? "light" : "dark") + ".ico"), size))
                {
                    int x = left + (size == 16 ? 80 : 180);
                    g.DrawImageUnscaled(frame, x, 286);
                    g.DrawString(size + " px", font, text, x+27, 285);
                }
            }
            preview.Save(path, ImageFormat.Png);
        }
    }
}
'@

[ExternalPowerDischargeIcons]::Write(
    (Join-Path $Repository 'src/OpenBlade.Tray/Assets/Brand/Tray'), $PreviewPath, $DetailPath)
