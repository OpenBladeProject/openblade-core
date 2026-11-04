using Microsoft.UI.Composition;
using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Xaml;
using OpenBlade.Contracts;
using WinRT;

namespace OpenBlade.Tray;

internal sealed class MicaBackdropHost : IDisposable
{
    private readonly Window window;
    private FrameworkElement? root;
    private MicaController? controller;
    private SystemBackdropConfiguration? configuration;
    private int disposed;

    public MicaBackdropHost(Window window, FrameworkElement root)
    {
        this.window = window ?? throw new ArgumentNullException(nameof(window));
        ArgumentNullException.ThrowIfNull(root);

        if (!MicaController.IsSupported())
        {
            return;
        }

        try
        {
            configuration = new SystemBackdropConfiguration
            {
                IsInputActive = true,
                Theme = ToBackdropTheme(root.ActualTheme),
            };
            controller = new MicaController
            {
                Kind = MicaKind.Base,
            };
            controller.AddSystemBackdropTarget(
                window.As<ICompositionSupportsSystemBackdrop>());
            controller.SetSystemBackdropConfiguration(configuration);
            window.Activated += OnWindowActivated;
            SetThemeSource(root);
        }
        catch
        {
            try { Dispose(); }
            catch (Exception exception)
            {
                DiagnosticTrace.TraceException("OpenBlade.Tray.MicaBackdropHost", "FailedInitializationCleanup", exception);
            }
            throw;
        }
    }

    internal void SetThemeSource(FrameworkElement? source)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
        if (ReferenceEquals(root, source))
        {
            return;
        }
        FrameworkElement? previous = root;
        root = null;
        if (previous is not null)
        {
            previous.ActualThemeChanged -= OnActualThemeChanged;
        }
        if (source is not null && configuration is not null)
        {
            root = source;
            source.ActualThemeChanged += OnActualThemeChanged;
            configuration.Theme = ToBackdropTheme(source.ActualTheme);
        }
    }

    internal void DetachThemeSource(FrameworkElement source)
    {
        if (ReferenceEquals(root, source))
        {
            SetThemeSource(null);
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0)
        {
            return;
        }

        FrameworkElement? previous = root;
        root = null;
        MicaController? previousController = controller;
        controller = null;
        configuration = null;
        try
        {
            window.Activated -= OnWindowActivated;
        }
        finally
        {
            try
            {
                if (previous is not null)
                {
                    previous.ActualThemeChanged -= OnActualThemeChanged;
                }
            }
            finally
            {
                previousController?.Dispose();
            }
        }
    }

    private void OnWindowActivated(object sender, WindowActivatedEventArgs args)
    {
        if (configuration is not null)
        {
            configuration.IsInputActive =
                args.WindowActivationState != WindowActivationState.Deactivated;
        }
    }

    private void OnActualThemeChanged(FrameworkElement sender, object args)
    {
        if (configuration is not null && ReferenceEquals(root, sender))
        {
            configuration.Theme = ToBackdropTheme(sender.ActualTheme);
        }
    }

    private static SystemBackdropTheme ToBackdropTheme(ElementTheme theme) => theme switch
    {
        ElementTheme.Light => SystemBackdropTheme.Light,
        ElementTheme.Dark => SystemBackdropTheme.Dark,
        ElementTheme.Default => SystemBackdropTheme.Default,
        _ => throw new ArgumentOutOfRangeException(nameof(theme)),
    };
}
