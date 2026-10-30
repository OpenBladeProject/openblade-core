[CmdletBinding()]
param(
    [string]$Repository,
    [string]$PreviewPath
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

public static class ExternalPowerDischargeIcons
{
    private static Bitmap Frame(int size, Color color)
    {
        using (var canvas = new Bitmap(size * 8, size * 8))
        using (var g = Graphics.FromImage(canvas))
        using (var fill = new SolidBrush(color))
        using (var clear = new SolidBrush(Color.Transparent))
        using (var body = new GraphicsPath())
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.ScaleTransform(size / 2f, size / 2f);
            body.AddArc(1, 1.5f, 3.2f, 3.2f, 180, 90);
            body.AddArc(11.8f, 1.5f, 3.2f, 3.2f, 270, 90);
            body.AddArc(11.8f, 8.8f, 3.2f, 3.2f, 0, 90);
            body.AddArc(1, 8.8f, 3.2f, 3.2f, 90, 90);
            body.CloseFigure();
            g.FillPath(fill, body);
            g.FillPolygon(fill, new[] { new PointF(.5f, 13), new PointF(15.5f, 13),
                new PointF(14, 14.5f), new PointF(2, 14.5f) });
            g.CompositingMode = CompositingMode.SourceCopy;
            g.FillRectangle(clear, 3.5f, 4.25f, 8.3f, 4.5f);
            g.FillRectangle(clear, 11.8f, 5.4f, .9f, 2.2f);
            g.FillRectangle(fill, 4.5f, 5.25f, 6.3f, 2.5f);
            g.FillRectangle(clear, 5.5f, 6.05f, 4.3f, .9f);
            var result = new Bitmap(size, size);
            using (var output = Graphics.FromImage(result))
            {
                output.CompositingMode = CompositingMode.SourceCopy;
                output.InterpolationMode = InterpolationMode.HighQualityBicubic;
                output.PixelOffsetMode = PixelOffsetMode.HighQuality;
                output.DrawImage(canvas, new Rectangle(0, 0, size, size));
            }
            return result;
        }
    }

    public static void Write(string directory, string previewPath)
    {
        int[] sizes = { 16, 20, 24, 32, 48 };
        Directory.CreateDirectory(directory);
        using (var preview = new Bitmap(640, 340))
        using (var graphics = Graphics.FromImage(preview))
        {
            for (int theme = 0; theme < 2; theme++)
            {
                Color foreground = ColorTranslator.FromHtml(theme == 0 ? "#FFC857" : "#9C5700");
                using (var background = new SolidBrush(theme == 0 ? Color.FromArgb(32, 32, 32) : Color.White))
                {
                    graphics.FillRectangle(background, 0, theme * 170, 640, 170);
                }
                var frames = new byte[sizes.Length][];
                for (int i = 0; i < sizes.Length; i++)
                {
                    using (var bitmap = Frame(sizes[i], foreground))
                    using (var stream = new MemoryStream())
                    {
                        bitmap.Save(stream, ImageFormat.Png);
                        frames[i] = stream.ToArray();
                        graphics.DrawImageUnscaled(bitmap, i * 128 + 16, theme * 170 + 8);
                        graphics.InterpolationMode = InterpolationMode.NearestNeighbor;
                        graphics.DrawImage(bitmap, i * 128 + 16, theme * 170 + 62, sizes[i] * 2, sizes[i] * 2);
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
            if (!string.IsNullOrEmpty(previewPath)) { preview.Save(previewPath, ImageFormat.Png); }
        }
    }
}
'@

[ExternalPowerDischargeIcons]::Write(
    (Join-Path $Repository 'src/OpenBlade.Tray/Assets/Brand/Tray'), $PreviewPath)
