using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using OpenBlade.Contracts;
using Windows.UI.ViewManagement;
using Color = Windows.UI.Color;

namespace OpenBlade.Tray;

public sealed class AccentColorManager : IDisposable
{
    private readonly FrameworkElement root;
    private readonly DispatcherQueue dispatcherQueue;
    private readonly UISettings uiSettings = new();
    private AppAccentColorMode mode;
    private string customColor = AppAccentColorPreferenceDefaults.RazerGreenHex;
    private int disposed;

    public AccentColorManager(FrameworkElement root, DispatcherQueue dispatcherQueue)
    {
        this.root = root ?? throw new ArgumentNullException(nameof(root));
        this.dispatcherQueue = dispatcherQueue
            ?? throw new ArgumentNullException(nameof(dispatcherQueue));
        uiSettings.ColorValuesChanged += OnColorValuesChanged;
    }

    public event EventHandler? ColorChanged;

    public Color CurrentColor { get; private set; } = ParseHexColor(
        AppAccentColorPreferenceDefaults.RazerGreenHex);

    public void Apply(AppAccentColorMode selectedMode, string? selectedCustomColor)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
        mode = Enum.IsDefined(selectedMode)
            ? selectedMode
            : AppAccentColorMode.RazerGreen;
        customColor = AppAccentColorPreferenceDefaults.NormalizeCustomColor(
            selectedCustomColor);

        AccentPalette palette = mode == AppAccentColorMode.Windows
            ? ReadWindowsPalette()
            : CreatePalette(mode == AppAccentColorMode.Custom
                ? ParseHexColor(customColor)
                : ParseHexColor(AppAccentColorPreferenceDefaults.RazerGreenHex));
        ApplyPalette(palette);
    }

    public static Color ParseHexColor(string value)
    {
        string normalized = AppAccentColorPreferenceDefaults.NormalizeCustomColor(value);
        return Color.FromArgb(
            255,
            Convert.ToByte(normalized.Substring(1, 2), 16),
            Convert.ToByte(normalized.Substring(3, 2), 16),
            Convert.ToByte(normalized.Substring(5, 2), 16));
    }

    public static string ToHexColor(Color color) =>
        $"#{color.R:X2}{color.G:X2}{color.B:X2}";

    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0)
        {
            return;
        }

        uiSettings.ColorValuesChanged -= OnColorValuesChanged;
    }

    private void OnColorValuesChanged(UISettings sender, object args)
    {
        if (Volatile.Read(ref disposed) != 0 || mode != AppAccentColorMode.Windows)
        {
            return;
        }

        dispatcherQueue.TryEnqueue(() =>
        {
            if (Volatile.Read(ref disposed) == 0 && mode == AppAccentColorMode.Windows)
            {
                ApplyPalette(ReadWindowsPalette());
            }
        });
    }

    private AccentPalette ReadWindowsPalette()
    {
        try
        {
            return new AccentPalette(
                uiSettings.GetColorValue(UIColorType.Accent),
                uiSettings.GetColorValue(UIColorType.AccentLight1),
                uiSettings.GetColorValue(UIColorType.AccentLight2),
                uiSettings.GetColorValue(UIColorType.AccentLight3),
                uiSettings.GetColorValue(UIColorType.AccentDark1),
                uiSettings.GetColorValue(UIColorType.AccentDark2),
                uiSettings.GetColorValue(UIColorType.AccentDark3));
        }
        catch (Exception exception)
        {
            DiagnosticTrace.TraceException(
                nameof(AccentColorManager),
                "ReadWindowsPalette",
                exception);
            return CreatePalette(ParseHexColor(
                AppAccentColorPreferenceDefaults.RazerGreenHex));
        }
    }

    private void ApplyPalette(AccentPalette palette)
    {
        CurrentColor = palette.Base;
        ResourceDictionary applicationResources =
            Microsoft.UI.Xaml.Application.Current.Resources;
        applicationResources["SystemAccentColor"] = palette.Base;
        applicationResources["SystemAccentColorLight1"] = palette.Light1;
        applicationResources["SystemAccentColorLight2"] = palette.Light2;
        applicationResources["SystemAccentColorLight3"] = palette.Light3;
        applicationResources["SystemAccentColorDark1"] = palette.Dark1;
        applicationResources["SystemAccentColorDark2"] = palette.Dark2;
        applicationResources["SystemAccentColorDark3"] = palette.Dark3;

        Color textOnAccent = ShouldUseDarkText(palette.Base)
            ? Color.FromArgb(255, 0, 0, 0)
            : Color.FromArgb(255, 255, 255, 255);
        foreach (ResourceDictionary resources in
                 GetAccentResourceDictionaries(applicationResources))
        {
            ApplyBrushPalette(resources);
        }

        // Application resource lookup resolves the brushes used by the active
        // Dark theme. Keep that live lookup synchronized in addition to the
        // explicit theme dictionaries needed when the root forces Light mode.
        ApplyBrushPalette(applicationResources);

        root.InvalidateMeasure();
        root.InvalidateArrange();
        ColorChanged?.Invoke(this, EventArgs.Empty);

        void ApplyBrushPalette(ResourceDictionary resources)
        {
            SetBrushResource(resources, "AccentFillColorDefaultBrush", palette.Base);
            SetBrushResource(
                resources,
                "AccentFillColorSecondaryBrush",
                WithAlpha(palette.Base, 230));
            SetBrushResource(
                resources,
                "AccentFillColorTertiaryBrush",
                WithAlpha(palette.Base, 204));
            SetBrushResource(
                resources,
                "AccentFillColorSelectedTextBackgroundBrush",
                palette.Base);
            SetBrushResource(resources, "AccentTextFillColorPrimaryBrush", palette.Base);
            SetBrushResource(
                resources,
                "AccentTextFillColorSecondaryBrush",
                WithAlpha(palette.Base, 230));
            SetBrushResource(
                resources,
                "AccentTextFillColorTertiaryBrush",
                WithAlpha(palette.Base, 204));
            SetBrushResource(
                resources,
                "TextOnAccentFillColorPrimaryBrush",
                textOnAccent);
            SetBrushResource(
                resources,
                "TextOnAccentFillColorSecondaryBrush",
                WithAlpha(textOnAccent, 179));
        }
    }

    private static IReadOnlyList<ResourceDictionary> GetAccentResourceDictionaries(
        ResourceDictionary resources)
    {
        var result = new List<ResourceDictionary>();
        var visited = new HashSet<ResourceDictionary>();
        Collect(resources);
        if (result.Count == 0)
        {
            throw new InvalidOperationException(
                "The WinUI Light and Dark accent resource dictionaries are missing.");
        }

        return result;

        void Collect(ResourceDictionary current)
        {
            if (!visited.Add(current))
            {
                return;
            }

            foreach (string theme in new[] { "Light", "Dark" })
            {
                if (current.ThemeDictionaries.TryGetValue(theme, out object? value)
                    && value is ResourceDictionary dictionary
                    && dictionary.ContainsKey("AccentFillColorDefaultBrush"))
                {
                    result.Add(dictionary);
                }
            }

            foreach (ResourceDictionary merged in current.MergedDictionaries)
            {
                Collect(merged);
            }
        }
    }

    private static void SetBrushResource(
        ResourceDictionary resources,
        string key,
        Color color)
    {
        if (resources[key] is SolidColorBrush existing)
        {
            existing.Color = color;
            return;
        }

        resources[key] = new SolidColorBrush(color);
    }

    private static AccentPalette CreatePalette(Color color) => new(
        color,
        Blend(color, Color.FromArgb(255, 255, 255, 255), 0.18),
        Blend(color, Color.FromArgb(255, 255, 255, 255), 0.36),
        Blend(color, Color.FromArgb(255, 255, 255, 255), 0.68),
        Blend(color, Color.FromArgb(255, 0, 0, 0), 0.20),
        Blend(color, Color.FromArgb(255, 0, 0, 0), 0.40),
        Blend(color, Color.FromArgb(255, 0, 0, 0), 0.60));

    private static Color Blend(Color source, Color target, double amount) => Color.FromArgb(
        255,
        (byte)Math.Round(source.R + ((target.R - source.R) * amount)),
        (byte)Math.Round(source.G + ((target.G - source.G) * amount)),
        (byte)Math.Round(source.B + ((target.B - source.B) * amount)));

    private static Color WithAlpha(Color color, byte alpha) =>
        Color.FromArgb(alpha, color.R, color.G, color.B);

    private static bool ShouldUseDarkText(Color color)
    {
        double luminance = ((0.2126 * color.R) + (0.7152 * color.G) + (0.0722 * color.B))
            / 255d;
        return luminance > 0.58;
    }

    private sealed record AccentPalette(
        Color Base,
        Color Light1,
        Color Light2,
        Color Light3,
        Color Dark1,
        Color Dark2,
        Color Dark3);
}
