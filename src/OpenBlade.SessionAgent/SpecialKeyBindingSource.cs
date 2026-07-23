using OpenBlade.Tray;

namespace OpenBlade.SessionAgent;

public interface ISpecialKeyBindingSource
{
    SpecialKeyAction GetAction(SpecialKeySlot slot);

    bool OverridesDefaultBehavior(SpecialKeySlot slot);
}

public sealed class SpecialKeyBindingSource : ISpecialKeyBindingSource
{
    private static readonly TimeSpan RefreshInterval = TimeSpan.FromSeconds(1);
    private readonly UiPreferenceStore store;
    private SpecialKeyBindingPreference[] current =
        SpecialKeyBindingDefaults.Bindings.ToArray();

    public SpecialKeyBindingSource(string preferencesPath)
    {
        store = new UiPreferenceStore(preferencesPath);
    }

    public SpecialKeyAction GetAction(SpecialKeySlot slot)
    {
        if (!Enum.IsDefined(slot))
        {
            return new SpecialKeyAction(SpecialKeyActionKind.Default);
        }

        SpecialKeyBindingPreference[] snapshot = Volatile.Read(ref current);
        return snapshot[(int)slot].Action;
    }

    public bool OverridesDefaultBehavior(SpecialKeySlot slot) =>
        SpecialKeyBindingDefaults.OverridesDefaultBehavior(GetAction(slot));

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        await ReloadAsync(cancellationToken).ConfigureAwait(false);
        using var timer = new PeriodicTimer(RefreshInterval);
        while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
        {
            await ReloadAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task ReloadAsync(CancellationToken cancellationToken)
    {
        UiPreferences preferences = await store.LoadAsync(cancellationToken)
            .ConfigureAwait(false);
        Volatile.Write(
            ref current,
            preferences.EffectiveSpecialKeyBindings.ToArray());
    }
}
