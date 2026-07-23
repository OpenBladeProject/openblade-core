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
    private readonly string preferencesPath;
    private readonly UiPreferenceStore store;
    private readonly Action<Exception>? errorHandler;
    private FileSignature observedSignature;
    private bool signatureInitialized;
    private SpecialKeyBindingPreference[] current =
        SpecialKeyBindingDefaults.Bindings.ToArray();

    public SpecialKeyBindingSource(
        string preferencesPath,
        Action<Exception>? errorHandler = null)
    {
        this.preferencesPath = Path.GetFullPath(preferencesPath);
        store = new UiPreferenceStore(this.preferencesPath);
        this.errorHandler = errorHandler;
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
        await ReloadIfChangedAsync(force: true, cancellationToken).ConfigureAwait(false);
        using var timer = new PeriodicTimer(RefreshInterval);
        while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
        {
            await ReloadIfChangedAsync(force: false, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task ReloadIfChangedAsync(
        bool force,
        CancellationToken cancellationToken)
    {
        try
        {
            var file = new FileInfo(preferencesPath);
            var signature = new FileSignature(
                file.Exists,
                file.Exists ? file.Length : 0,
                file.Exists ? file.LastWriteTimeUtc : default);
            if (!force && signatureInitialized && signature == observedSignature)
            {
                return;
            }

            UiPreferences preferences = await store.LoadAsync(cancellationToken)
                .ConfigureAwait(false);
            Volatile.Write(
                ref current,
                preferences.EffectiveSpecialKeyBindings.ToArray());
            observedSignature = signature;
            signatureInitialized = true;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            try
            {
                errorHandler?.Invoke(exception);
            }
            catch
            {
            }
        }
    }

    private readonly record struct FileSignature(
        bool Exists,
        long Length,
        DateTime LastWriteTimeUtc);
}
