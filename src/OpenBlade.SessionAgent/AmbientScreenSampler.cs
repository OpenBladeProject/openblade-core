using System.ComponentModel;
using System.Runtime.InteropServices;
using OpenBlade.Contracts;

namespace OpenBlade.SessionAgent;

public interface IAmbientScreenSampler : IDisposable
{
    AmbientLightingFrame Capture(AmbientAwarenessRegion region);
}

public interface IAmbientScreenSamplerFactory
{
    IAmbientScreenSampler Create();
}

public sealed class WindowsAmbientScreenSamplerFactory : IAmbientScreenSamplerFactory
{
    public static WindowsAmbientScreenSamplerFactory Instance { get; } = new();

    private WindowsAmbientScreenSamplerFactory()
    {
    }

    public IAmbientScreenSampler Create() => new WindowsAmbientScreenSampler();
}

public sealed class WindowsAmbientScreenSampler : IAmbientScreenSampler
{
    private const int BiRgb = 0;
    private const int DibRgbColors = 0;
    private const int Halftone = 4;
    private const int SmCxScreen = 0;
    private const int SmCyScreen = 1;
    private const uint SrcCopy = 0x00CC0020;

    private readonly byte[] bgra = new byte[AmbientLightingFrame.SlotCount * 4];
    private readonly IntPtr memoryDc;
    private readonly IntPtr bitmap;
    private readonly IntPtr previousObject;
    private readonly IntPtr bits;
    private int disposed;

    public WindowsAmbientScreenSampler()
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException();
        }

        memoryDc = CreateCompatibleDC(IntPtr.Zero);
        if (memoryDc == IntPtr.Zero)
        {
            throw LastWin32("Unable to create the Ambient Awareness capture context.");
        }

        var info = new BitmapInfo
        {
            Header = new BitmapInfoHeader
            {
                Size = checked((uint)Marshal.SizeOf<BitmapInfoHeader>()),
                Width = AmbientLightingFrame.Columns,
                Height = -AmbientLightingFrame.Rows,
                Planes = 1,
                BitCount = 32,
                Compression = BiRgb,
            },
        };
        bitmap = CreateDIBSection(
            memoryDc,
            ref info,
            DibRgbColors,
            out bits,
            IntPtr.Zero,
            0);
        if (bitmap == IntPtr.Zero || bits == IntPtr.Zero)
        {
            DeleteDC(memoryDc);
            throw LastWin32("Unable to create the Ambient Awareness sample buffer.");
        }

        previousObject = SelectObject(memoryDc, bitmap);
        if (previousObject == IntPtr.Zero || previousObject == new IntPtr(-1))
        {
            DeleteObject(bitmap);
            DeleteDC(memoryDc);
            throw LastWin32("Unable to select the Ambient Awareness sample buffer.");
        }

        _ = SetStretchBltMode(memoryDc, Halftone);
        _ = SetBrushOrgEx(memoryDc, 0, 0, IntPtr.Zero);
    }

    public AmbientLightingFrame Capture(AmbientAwarenessRegion region)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
        if (!Enum.IsDefined(region))
        {
            throw new ArgumentOutOfRangeException(nameof(region));
        }

        int width = GetSystemMetrics(SmCxScreen);
        int height = GetSystemMetrics(SmCyScreen);
        if (width <= 0 || height <= 0)
        {
            throw new InvalidOperationException("The primary display dimensions are unavailable.");
        }

        (int x, int y, int sourceWidth, int sourceHeight) = Crop(region, width, height);
        IntPtr screenDc = GetDC(IntPtr.Zero);
        if (screenDc == IntPtr.Zero)
        {
            throw LastWin32("Unable to open the primary display for Ambient Awareness.");
        }

        try
        {
            if (!StretchBlt(
                    memoryDc,
                    0,
                    0,
                    AmbientLightingFrame.Columns,
                    AmbientLightingFrame.Rows,
                    screenDc,
                    x,
                    y,
                    sourceWidth,
                    sourceHeight,
                    SrcCopy))
            {
                throw LastWin32("Unable to sample the primary display for Ambient Awareness.");
            }
        }
        finally
        {
            _ = ReleaseDC(IntPtr.Zero, screenDc);
        }

        return ReadCompletedFrame(bits, bgra, GdiFlush);
    }

    internal static AmbientLightingFrame ReadCompletedFrame(
        IntPtr source,
        byte[] buffer,
        Func<bool> flush)
    {
        // CreateDIBSection requires synchronization before direct pixel access.
        // A failed batch must not publish stale pixels as a successful frame.
        if (!flush())
        {
            throw new InvalidOperationException("Unable to complete the Ambient Awareness capture batch.");
        }
        Marshal.Copy(source, buffer, 0, buffer.Length);
        return AmbientFrameMapper.FromBgra32(buffer);
    }
    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0)
        {
            return;
        }

        _ = SelectObject(memoryDc, previousObject);
        _ = DeleteObject(bitmap);
        _ = DeleteDC(memoryDc);
    }

    private static (int X, int Y, int Width, int Height) Crop(
        AmbientAwarenessRegion region,
        int width,
        int height) => region switch
        {
            AmbientAwarenessRegion.Full => (0, 0, width, height),
            AmbientAwarenessRegion.Left => (0, 0, Math.Max(1, width / 2), height),
            AmbientAwarenessRegion.Top => (0, 0, width, Math.Max(1, height / 2)),
            AmbientAwarenessRegion.Right =>
                (width / 2, 0, Math.Max(1, width - (width / 2)), height),
            AmbientAwarenessRegion.Bottom =>
                (0, height / 2, width, Math.Max(1, height - (height / 2))),
            _ => throw new ArgumentOutOfRangeException(nameof(region)),
        };

    private static Win32Exception LastWin32(string message) =>
        new(Marshal.GetLastWin32Error(), message);

    [StructLayout(LayoutKind.Sequential)]
    private struct BitmapInfoHeader
    {
        internal uint Size;
        internal int Width;
        internal int Height;
        internal ushort Planes;
        internal ushort BitCount;
        internal int Compression;
        internal uint SizeImage;
        internal int XPelsPerMeter;
        internal int YPelsPerMeter;
        internal uint ClrUsed;
        internal uint ClrImportant;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BitmapInfo
    {
        internal BitmapInfoHeader Header;
        internal uint Colors;
    }

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int index);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr GetDC(IntPtr window);

    [DllImport("user32.dll")]
    private static extern int ReleaseDC(IntPtr window, IntPtr dc);

    [DllImport("gdi32.dll", SetLastError = true)]
    private static extern IntPtr CreateCompatibleDC(IntPtr dc);

    [DllImport("gdi32.dll", SetLastError = true)]
    private static extern bool DeleteDC(IntPtr dc);

    [DllImport("gdi32.dll", SetLastError = true)]
    private static extern IntPtr CreateDIBSection(
        IntPtr dc,
        ref BitmapInfo info,
        int usage,
        out IntPtr bits,
        IntPtr section,
        uint offset);

    [DllImport("gdi32.dll", SetLastError = true)]
    private static extern IntPtr SelectObject(IntPtr dc, IntPtr value);

    [DllImport("gdi32.dll", SetLastError = true)]
    private static extern bool DeleteObject(IntPtr value);

    [DllImport("gdi32.dll")]
    private static extern bool GdiFlush();

    [DllImport("gdi32.dll")]
    private static extern int SetStretchBltMode(IntPtr dc, int mode);

    [DllImport("gdi32.dll")]
    private static extern bool SetBrushOrgEx(IntPtr dc, int x, int y, IntPtr previous);

    [DllImport("gdi32.dll", SetLastError = true)]
    private static extern bool StretchBlt(
        IntPtr destination,
        int destinationX,
        int destinationY,
        int destinationWidth,
        int destinationHeight,
        IntPtr source,
        int sourceX,
        int sourceY,
        int sourceWidth,
        int sourceHeight,
        uint rasterOperation);
}

public static class AmbientFrameMapper
{
    public static AmbientLightingFrame FromBgra32(ReadOnlySpan<byte> bgra)
    {
        if (bgra.Length != AmbientLightingFrame.SlotCount * 4)
        {
            throw new ArgumentOutOfRangeException(nameof(bgra));
        }

        RgbColor[] colors = new RgbColor[AmbientLightingFrame.SlotCount];
        for (int slot = 0; slot < AmbientLightingFrame.SlotCount; slot++)
        {
            if (AmbientLightingFrame.IsHole(slot))
            {
                continue;
            }

            int source = slot * 4;
            colors[slot] = new RgbColor(
                bgra[source + 2],
                bgra[source + 1],
                bgra[source]);
        }

        return new AmbientLightingFrame(colors);
    }
}
