using System.Drawing;
using System.Drawing.Imaging;
using Microsoft.UI.Xaml.Media.Imaging;

namespace OpenBlade.Tray;

/// <summary>Reads embedded executable icons with bounded concurrency. It never starts the executable.</summary>
internal static class ApplicationExecutableIconLoader
{
    private static readonly SemaphoreSlim Gate = new(initialCount: 4, maxCount: 4);

    internal static async Task<BitmapImage?> LoadAsync(string executablePath, CancellationToken token)
    {
        if (!IsAdmittedLocalExecutablePath(executablePath))
        {
            return null;
        }

        await Gate.WaitAsync(token);
        try
        {
            byte[]? encoded = await Task.Run(() => ReadPng(executablePath), token);
            if (encoded is null)
            {
                return null;
            }

            token.ThrowIfCancellationRequested();
            using var stream = new MemoryStream(encoded, writable: false);
            var image = new BitmapImage();
            await image.SetSourceAsync(stream.AsRandomAccessStream());
            token.ThrowIfCancellationRequested();
            return image;
        }
        catch (Exception exception) when (exception is ArgumentException or IOException
            or UnauthorizedAccessException or System.Runtime.InteropServices.ExternalException)
        {
            return null;
        }
        finally
        {
            Gate.Release();
        }
    }

    private static bool IsAdmittedLocalExecutablePath(string executablePath)
    {
        if (string.IsNullOrWhiteSpace(executablePath)
            || !string.Equals(Path.GetExtension(executablePath), ".exe", StringComparison.OrdinalIgnoreCase)
            || !Path.IsPathFullyQualified(executablePath)
            || executablePath.StartsWith(@"\\", StringComparison.Ordinal)
            || executablePath.StartsWith("//", StringComparison.Ordinal))
        {
            return false;
        }

        try
        {
            string? root = Path.GetPathRoot(executablePath);
            return root is not null
                && new DriveInfo(root).DriveType != DriveType.Network
                && File.Exists(executablePath);
        }
        catch (Exception exception) when (exception is ArgumentException or IOException
            or NotSupportedException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static byte[]? ReadPng(string executablePath)
    {
        using Icon? icon = Icon.ExtractAssociatedIcon(executablePath);
        if (icon is null)
        {
            return null;
        }

        using Bitmap bitmap = icon.ToBitmap();
        using var output = new MemoryStream();
        bitmap.Save(output, ImageFormat.Png);
        return output.ToArray();
    }
}
