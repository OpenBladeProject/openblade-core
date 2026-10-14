namespace OpenBlade.Tray;

public interface IExternalUriLauncher
{
    Task<bool> OpenAsync(Uri uri, CancellationToken token);
}
