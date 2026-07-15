using Microsoft.UI.Composition;
using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Xaml;
using WinRT;

namespace OpenBlade.Tray;

internal sealed class MicaBackdropHost : IDisposable
{
    private readonly Window window;
    private readonly FrameworkElement root;
    private MicaController? controller;
    private SystemBackdropConfiguration? configuration;
    private int disposed;

    public MicaBackdropHost(Window window, FrameworkElement root)
    {
        this.window = window ?? throw new ArgumentNullException(nameof(window));
        this.root = root ?? throw new ArgumentNullException(nameof(root));

        if (!MicaController.IsSupported())
        {
            return;
        }

        configuration = new SystemBackdropConfiguration
        {
            IsInputActive = true,
        };
        configuration.Theme = ToBackdropTheme(root.ActualTheme);

        controller = new MicaController
        {
            Kind = MicaKind.Base,
        };
        controller.AddSystemBackdropTarget(
            window.As<ICompositionSupportsSystemBackdrop>());
        controller.SetSystemBackdropConfiguration(configuration);

        window.Activated += OnWindowActivated;
        root.ActualThemeChanged += OnActualThemeChanged;
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0)
        {
            return;
        }

        window.Activated -= OnWindowActivated;
        root.ActualThemeChanged -= OnActualThemeChanged;

        if (controller is not null)
        {
            controller.Dispose();
            controller = null;
        }

        configuration = null;
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
        if (configuration is not null)
        {
            configuration.Theme = ToBackdropTheme(root.ActualTheme);
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
