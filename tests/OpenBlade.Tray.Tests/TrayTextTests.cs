using System.Globalization;
using System.Resources;
using OpenBlade.Contracts;
using OpenBlade.Tray;

namespace OpenBlade.Tray.Tests;

[Collection(LocalizationTestCollection.Name)]
public sealed class TrayTextTests
{
    [Fact]
    public void Get_MissingCultureFallsBackToAuthoritativeEnglish()
    {
        using var culture = new CultureScope("fr-FR");

        Assert.Equal("Battery protection", TrayText.Get("Battery protection"));
    }

    [Theory]
    [InlineData("zh-CN", "电池保护")]
    [InlineData("ru-RU", "Защита батареи")]
    public void Get_SupportedCultureLoadsTranslatedResource(
        string cultureName,
        string expected)
    {
        using var culture = new CultureScope(cultureName);

        Assert.Equal(expected, TrayText.Get("Battery protection"));
    }

    [Fact]
    public void Get_MissingRequiredKeyFallsBackWithoutThrowing()
    {
        using var culture = new CultureScope("qps-ploc");

        Assert.Equal("Missing.Test.Key", TrayText.Get("Missing.Test.Key"));
    }

    [Fact]
    public void Format_MalformedTranslationFallsBackToEnglishFormat()
    {
        Assert.Equal(
            "Value: 42",
            TrayText.FormatLocalized("Broken {", "Value: {0}", 42));
    }

    [Fact]
    public void PseudoCatalog_HasSameKeysAndPreservesPlaceholders()
    {
        ResourceManager resources = new(
            "OpenBlade.Tray.Core.Properties.Resources",
            typeof(TrayText).Assembly);
        CultureInfo english = CultureInfo.GetCultureInfo("en-US");
        CultureInfo pseudo = CultureInfo.GetCultureInfo("qps-ploc");
        using ResourceSet englishSet = resources.GetResourceSet(
            english,
            createIfNotExists: true,
            tryParents: false)!;
        using ResourceSet pseudoSet = resources.GetResourceSet(
            pseudo,
            createIfNotExists: true,
            tryParents: false)!;

        Dictionary<string, string> englishValues = ReadStrings(englishSet);
        Dictionary<string, string> pseudoValues = ReadStrings(pseudoSet);
        Assert.Equal(englishValues.Keys.Order(), pseudoValues.Keys.Order());
        Assert.All(englishValues, pair =>
        {
            string[] englishPlaceholders = Placeholders(pair.Value);
            string[] pseudoPlaceholders = Placeholders(pseudoValues[pair.Key]);
            Assert.Equal(englishPlaceholders, pseudoPlaceholders);
            if (!System.Text.RegularExpressions.Regex.IsMatch(pair.Value, @"\p{L}"))
            {
                Assert.Equal(pair.Value, pseudoValues[pair.Key]);
            }
        });
    }

    [Fact]
    public void PseudoCulture_UsesExpandedResource()
    {
        using var culture = new CultureScope("qps-ploc");

        string value = TrayText.Get("Battery protection");

        Assert.StartsWith("[!! ", value, StringComparison.Ordinal);
        Assert.EndsWith(" !!]", value, StringComparison.Ordinal);
        Assert.NotEqual("Battery protection", value);
    }

    [Fact]
    public void PresentationDefaultsResolveTheCurrentCultureOnEveryRequest()
    {
        string englishCard;
        string englishSection;
        string englishControl;
        string englishSlider;
        string englishFan;
        using (new CultureScope("en-US"))
        {
            englishCard = DashboardCardPreferenceDefaults.CreateOptions(null)[0].Label;
            englishSection = PageLayoutPreferenceDefaults.CreateOptions(
                CustomizablePageKind.Profiles,
                null)[0].Label;
            englishControl = TrayDashboardPreferenceDefaults.CreateControlOptions(null)[0].Label;
            englishSlider = TrayDashboardPreferenceDefaults.CreateSliderOptions(null)[0].Label;
            englishFan = FanModePresentation.CreateOptions(
                new HashSet<FanMode> { FanMode.Automatic })[0].Label;
        }

        using var culture = new CultureScope("qps-ploc");
        AssertPseudoAndChanged(
            englishCard,
            DashboardCardPreferenceDefaults.CreateOptions(null)[0].Label);
        AssertPseudoAndChanged(
            englishSection,
            PageLayoutPreferenceDefaults.CreateOptions(
                CustomizablePageKind.Profiles,
                null)[0].Label);
        AssertPseudoAndChanged(
            englishControl,
            TrayDashboardPreferenceDefaults.CreateControlOptions(null)[0].Label);
        AssertPseudoAndChanged(
            englishSlider,
            TrayDashboardPreferenceDefaults.CreateSliderOptions(null)[0].Label);
        AssertPseudoAndChanged(
            englishFan,
            FanModePresentation.CreateOptions(
                new HashSet<FanMode> { FanMode.Automatic })[0].Label);
    }

    [Fact]
    public void ExplicitLanguageOverrideWinsOverCapturedThreadCulture()
    {
        ExecutionContext? russianContext;
        using (new CultureScope("ru-RU"))
        {
            russianContext = ExecutionContext.Capture();
        }

        Assert.NotNull(russianContext);
        try
        {
            TrayLocalization.SetLanguageOverride("zh-CN");
            ExecutionContext.Run(
                russianContext,
                _ =>
                {
                    Assert.Equal("ru-RU", CultureInfo.CurrentUICulture.Name);
                    Assert.Equal("电池", TrayText.Get("Battery"));

                    IReadOnlyList<FanModeOption> fanOptions =
                        FanModePresentation.CreateOptions(
                            new HashSet<FanMode> { FanMode.Automatic });
                    Assert.Equal("自动", fanOptions[0].Label);

                    var viewModel = new MainViewModel(
                        new UnavailableClient(),
                        new InlineDispatcher());
                    try
                    {
                        Assert.Equal(
                            "使用电池",
                            viewModel.ProfileComparisonOptions.Single(
                                option => option.Source == PowerSource.Battery).Label);
                        Assert.Equal(
                            "保持不变",
                            viewModel.AvailableWindowsPowerModeOptions[0].Label);
                        Assert.Equal(
                            "保留当前 Windows 设置。",
                            viewModel.AvailableWindowsPowerModeOptions[0].Description);
                    }
                    finally
                    {
                        viewModel.DisposeAsync().AsTask().GetAwaiter().GetResult();
                    }
                },
                state: null);
        }
        finally
        {
            TrayLocalization.SetLanguageOverride(null);
        }
    }

    [Fact]
    public void TemporaryLanguageOverrideIsIsolatedAndExpiresAcrossCapturedContexts()
    {
        ExecutionContext? previousContext;
        ExecutionContext? temporaryContext;
        try
        {
            TrayLocalization.SetLanguageOverride("ru-RU");
            previousContext = ExecutionContext.Capture();
            Assert.NotNull(previousContext);

            using (TrayLocalization.BeginTemporaryLanguageOverride("zh-CN"))
            {
                Assert.Equal("电池", TrayText.Get("Battery"));
                ExecutionContext.Run(
                    previousContext,
                    _ => Assert.Equal("Батарея", TrayText.Get("Battery")),
                    state: null);

                temporaryContext = ExecutionContext.Capture();
                Assert.NotNull(temporaryContext);
                TrayLocalization.SetLanguageOverride("en-US");
                Assert.Equal("电池", TrayText.Get("Battery"));
            }

            ExecutionContext.Run(
                temporaryContext,
                _ => Assert.Equal("Battery", TrayText.Get("Battery")),
                state: null);
        }
        finally
        {
            TrayLocalization.SetLanguageOverride(null);
        }
    }

    private static Dictionary<string, string> ReadStrings(ResourceSet resources) =>
        resources.Cast<System.Collections.DictionaryEntry>()
            .ToDictionary(
                entry => Assert.IsType<string>(entry.Key),
                entry => Assert.IsType<string>(entry.Value),
                StringComparer.Ordinal);

    private static string[] Placeholders(string value) =>
        System.Text.RegularExpressions.Regex.Matches(value, @"\{[^{}]+\}")
            .Select(match => match.Value)
            .Order(StringComparer.Ordinal)
            .ToArray();

    private static void AssertPseudoAndChanged(string english, string pseudo)
    {
        Assert.StartsWith("[!! ", pseudo, StringComparison.Ordinal);
        Assert.NotEqual(english, pseudo);
    }

    private sealed class UnavailableClient : OpenBlade.Testing.UnavailableServiceClient;

    private sealed class InlineDispatcher : IUiDispatcher
    {
        public Task InvokeAsync(Action action, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            action();
            return Task.CompletedTask;
        }
    }

}
