using OpenBlade.Tray;

namespace OpenBlade.SessionAgent;

public interface ISpecialKeyBindingSource
{
    SpecialKeyAction GetAction(SpecialKeySlot slot);

    bool ShouldHandleInDriverMode(SpecialKeySlot slot);
}

public sealed class SpecialKeyBindingSource : ISpecialKeyBindingSource
{
    private static readonly TimeSpan RefreshInterval = TimeSpan.FromSeconds(1);
    private readonly string preferencesPath;
    private readonly Func<CancellationToken, Task<UiPreferences>> loadPreferences;
    private readonly Action<Exception>? errorHandler;
    private FileSignature observedSignature;
    private bool signatureInitialized;
    private SpecialKeyBindingPreference[] current =
        SpecialKeyBindingDefaults.Bindings.ToArray();

    public SpecialKeyBindingSource(
        string preferencesPath,
        Action<Exception>? errorHandler = null)
        : this(
            preferencesPath,
            new UiPreferenceStore(Path.GetFullPath(preferencesPath)).LoadAsync,
            errorHandler)
    {
    }

    internal SpecialKeyBindingSource(
        string preferencesPath,
        Func<CancellationToken, Task<UiPreferences>> loadPreferences,
        Action<Exception>? errorHandler = null)
    {
        this.preferencesPath = Path.GetFullPath(preferencesPath);
        this.loadPreferences = loadPreferences
            ?? throw new ArgumentNullException(nameof(loadPreferences));
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

    public bool ShouldHandleInDriverMode(SpecialKeySlot slot) =>
        ShouldHandleInDriverMode(slot, GetAction(slot));

    internal static bool ShouldHandleInDriverMode(
        SpecialKeySlot slot,
        SpecialKeyAction action)
    {
        SpecialKeyAction normalized = SpecialKeyBindingDefaults.Normalize(action);
        return (slot is SpecialKeySlot.M3 or SpecialKeySlot.M5
                && normalized.Kind == SpecialKeyActionKind.Default)
            || SpecialKeyBindingDefaults.OverridesDefaultBehavior(normalized);
    }

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        await ReloadIfChangedAsync(force: true, cancellationToken).ConfigureAwait(false);
        using var timer = new PeriodicTimer(RefreshInterval);
        while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
        {
            await ReloadIfChangedAsync(force: false, cancellationToken).ConfigureAwait(false);
        }
    }

    internal async Task ReloadIfChangedAsync(
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

            UiPreferences preferences = await loadPreferences(cancellationToken)
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
