using OpenBlade.Tray;

namespace OpenBlade.Tray.Tests;

public sealed class CardOrderPreferenceTests
{
    [Fact]
    public void NormalizeKeepsKnownOrderDropsUnknownDuplicatesAndAppendsNewRows()
    {
        Assert.Equal(["brightness", "effect", "new-row"],
            CardOrderPreferencePolicy.Normalize(
                ["another-section", "brightness", "brightness", "effect"],
                ["effect", "brightness", "new-row"]));
    }

    [Fact]
    public void MissingOrderUsesDeclaredRowsWithoutMutatingKnownList()
    {
        string[] known = ["effect", "brightness"];
        Assert.Equal(known, CardOrderPreferencePolicy.Normalize(null, known));
        Assert.Equal(["effect", "brightness"], known);
    }

    [Fact]
    public async Task CardOrdersRoundTripSeparatelyFromSectionVisibility()
    {
        string directory = Path.Combine(Path.GetTempPath(), $"OpenBlade.CardOrders.{Guid.NewGuid():N}");
        string path = Path.Combine(directory, "preferences.json");
        try
        {
            var store = new UiPreferenceStore(path);
            Assert.Null((await store.LoadAsync()).CardOrders);
            await store.SaveAsync(new UiPreferences(ThemePreference.Dark,
                CardOrders: [new("Page.LightingKeyboard.EffectRows", ["brightness", "effect"])],
                PageLayouts: [new(CustomizablePageKind.Lighting,
                    [new(PageSectionKind.LightingKeyboard, false)])]));

            UiPreferences reloaded = await store.LoadAsync();
            CardOrderPreference order = Assert.Single(reloaded.CardOrders!);
            Assert.Equal("Page.LightingKeyboard.EffectRows", order.GroupId);
            Assert.Equal(["brightness", "effect"], order.CardIds);
            Assert.False(reloaded.EffectivePageLayouts.Single(layout =>
                layout.Page == CustomizablePageKind.Lighting).Sections.Single(section =>
                section.Kind == PageSectionKind.LightingKeyboard).IsVisible);
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, true);
            }
        }
    }
}
