using Windows.System;

namespace OpenBlade.Tray;

internal sealed class WinUiExternalUriLauncher : IExternalUriLauncher
{
    public async Task<bool> OpenAsync(Uri uri, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(uri);
        if (!uri.IsAbsoluteUri || uri.Scheme != Uri.UriSchemeHttps)
        {
            throw new ArgumentException("Only absolute https links can be opened.", nameof(uri));
        }

        token.ThrowIfCancellationRequested();
        return await Launcher.LaunchUriAsync(uri);
    }
}
