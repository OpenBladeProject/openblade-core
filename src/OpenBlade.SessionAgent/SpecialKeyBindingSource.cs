using System.Threading.Channels;
using OpenBlade.Tray;

namespace OpenBlade.SessionAgent;

public interface ISpecialKeyBindingSource
{
    SpecialKeyAction GetAction(SpecialKeySlot slot);

    bool ShouldHandleInDriverMode(SpecialKeySlot slot);
}

/// <summary>
/// Keeps the current-user special-key bindings current from the tray
/// preferences file. Changes arrive through a file-system watcher so the
/// Session Agent does not wake every second; bounded polling is used only
/// while a watcher cannot be armed, for example before the preferences
/// directory exists. Failed reads back off to a capped retry interval and
/// report only the first failure until a read succeeds.
/// </summary>
public sealed class SpecialKeyBindingSource : ISpecialKeyBindingSource
{
    internal static readonly TimeSpan FallbackRefreshInterval = TimeSpan.FromSeconds(1);
    internal static readonly TimeSpan ChangeSettleDelay = TimeSpan.FromMilliseconds(100);
    internal static readonly TimeSpan WatcherRearmDelay = TimeSpan.FromSeconds(1);
    internal static readonly TimeSpan MaximumReadRetryInterval = TimeSpan.FromSeconds(30);

    private readonly string preferencesPath;
    private readonly Func<CancellationToken, Task<UiPreferences>> loadPreferences;
    private readonly Func<string, Action, Action, IDisposable?> createWatcher;
    private readonly Func<TimeSpan, CancellationToken, Task> delay;
    private readonly Action<Exception>? errorHandler;
    private readonly Channel<WatchSignal> signals = Channel.CreateBounded<WatchSignal>(
        new BoundedChannelOptions(1)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
            SingleWriter = false,
        });
    private FileSignature observedSignature;
    private bool signatureInitialized;
    private TimeSpan readRetryInterval;
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
        Action<Exception>? errorHandler = null,
        Func<string, Action, Action, IDisposable?>? createWatcher = null,
        Func<TimeSpan, CancellationToken, Task>? delay = null)
    {
        this.preferencesPath = Path.GetFullPath(preferencesPath);
        this.loadPreferences = loadPreferences
            ?? throw new ArgumentNullException(nameof(loadPreferences));
        this.errorHandler = errorHandler;
        this.createWatcher = createWatcher ?? CreateFileSystemWatcher;
        this.delay = delay ?? Task.Delay;
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
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            // Failure is sticky for this watcher: a later Changed signal must
            // not overwrite it in the capacity-one notification channel.
            int watcherFailed = 0;
            using IDisposable? watcher = TryCreateWatcher(() => Volatile.Write(ref watcherFailed, 1));
            if (watcher is null)
            {
                await delay(readRetryInterval == TimeSpan.Zero ? FallbackRefreshInterval : readRetryInterval,
                    cancellationToken).ConfigureAwait(false);
                await ReloadIfChangedAsync(force: false, cancellationToken)
                    .ConfigureAwait(false);
                continue;
            }

            // A write that landed while the watcher was being armed produced
            // no event, so compare the on-disk signature once before waiting.
            DrainSignals();
            if (Volatile.Read(ref watcherFailed) != 0)
            {
                await delay(WatcherRearmDelay, cancellationToken).ConfigureAwait(false);
                continue;
            }
            bool loaded = readRetryInterval == TimeSpan.Zero
                && await ReloadIfChangedAsync(force: false, cancellationToken).ConfigureAwait(false);
            if (!await WatchUntilFailureAsync(!loaded, () => Volatile.Read(ref watcherFailed) != 0,
                cancellationToken).ConfigureAwait(false))
            {
                await delay(WatcherRearmDelay, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private async Task<bool> WatchUntilFailureAsync(
        bool retryPending,
        Func<bool> watcherHasFailed,
        CancellationToken cancellationToken)
    {
        while (true)
        {
            if (watcherHasFailed())
            {
                return false;
            }
            if (retryPending)
            {
                // Releasing a file lock produces no change event. Keep one
                // bounded retry pending until the read succeeds or we stop.
                await delay(readRetryInterval, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                WatchSignal signal = await signals.Reader.ReadAsync(cancellationToken)
                    .ConfigureAwait(false);
                if (signal == WatchSignal.Failed)
                {
                    return false;
                }

                // Coalesce an atomic replacement's burst into one signature compare.
                await delay(ChangeSettleDelay, cancellationToken).ConfigureAwait(false);
            }
            if (DrainSignals() == WatchSignal.Failed || watcherHasFailed())
            {
                return false;
            }
            retryPending = !await ReloadIfChangedAsync(force: false, cancellationToken).ConfigureAwait(false);
        }
    }

    private IDisposable? TryCreateWatcher(Action markFailed)
    {
        try
        {
            return createWatcher(
                preferencesPath,
                () => signals.Writer.TryWrite(WatchSignal.Changed),
                () =>
                {
                    markFailed();
                    signals.Writer.TryWrite(WatchSignal.Failed);
                });
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            ReportFailure(exception);
            return null;
        }
    }

    private WatchSignal DrainSignals()
    {
        WatchSignal drained = WatchSignal.Changed;
        while (signals.Reader.TryRead(out WatchSignal signal))
        {
            if (signal == WatchSignal.Failed)
            {
                drained = WatchSignal.Failed;
            }
        }

        return drained;
    }

    internal async Task<bool> ReloadIfChangedAsync(
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
                readRetryInterval = TimeSpan.Zero;
                return true;
            }

            UiPreferences preferences = await loadPreferences(cancellationToken)
                .ConfigureAwait(false);
            Volatile.Write(
                ref current,
                preferences.EffectiveSpecialKeyBindings.ToArray());
            observedSignature = signature;
            signatureInitialized = true;
            readRetryInterval = TimeSpan.Zero;
            return true;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            if (readRetryInterval == TimeSpan.Zero)
            {
                readRetryInterval = FallbackRefreshInterval;
                ReportFailure(exception);
            }
            else
            {
                readRetryInterval = TimeSpan.FromSeconds(Math.Min(
                    readRetryInterval.TotalSeconds * 2, MaximumReadRetryInterval.TotalSeconds));
            }
            return false;
        }
    }

    internal static bool IsPreferencesFileEvent(
        string preferencesFileName,
        string? name,
        string? oldName = null) =>
        MatchesFileName(preferencesFileName, name)
            || MatchesFileName(preferencesFileName, oldName);

    private static bool MatchesFileName(string preferencesFileName, string? candidate) =>
        candidate is not null
            && string.Equals(
                Path.GetFileName(candidate),
                preferencesFileName,
                StringComparison.OrdinalIgnoreCase);

    private static IDisposable? CreateFileSystemWatcher(
        string preferencesPath,
        Action changed,
        Action failed)
    {
        string? directory = Path.GetDirectoryName(preferencesPath);
        if (string.IsNullOrEmpty(directory) || !Directory.Exists(directory))
        {
            return null;
        }

        string fileName = Path.GetFileName(preferencesPath);
        FileSystemWatcher? watcher = null;
        try
        {
            watcher = new FileSystemWatcher(directory)
            {
                NotifyFilter = NotifyFilters.FileName
                    | NotifyFilters.LastWrite
                    | NotifyFilters.Size
                    | NotifyFilters.CreationTime,
                IncludeSubdirectories = false,
            };
            watcher.Changed += (_, args) => OnFileEvent(args.Name);
            watcher.Created += (_, args) => OnFileEvent(args.Name);
            watcher.Deleted += (_, args) => OnFileEvent(args.Name);
            watcher.Renamed += (_, args) => OnFileEvent(args.Name, args.OldName);
            watcher.Error += (_, _) => failed();
            watcher.EnableRaisingEvents = true;
            return watcher;
        }
        catch (Exception exception) when (exception is
            ArgumentException or IOException or UnauthorizedAccessException
            or PlatformNotSupportedException)
        {
            watcher?.Dispose();
            return null;
        }

        void OnFileEvent(string? name, string? oldName = null)
        {
            if (IsPreferencesFileEvent(fileName, name, oldName))
            {
                changed();
            }
        }
    }

    private void ReportFailure(Exception exception)
    {
        try
        {
            errorHandler?.Invoke(exception);
        }
        catch
        {
        }
    }

    private enum WatchSignal
    {
        Changed,
        Failed,
    }

    private readonly record struct FileSignature(
        bool Exists,
        long Length,
        DateTime LastWriteTimeUtc);
}
