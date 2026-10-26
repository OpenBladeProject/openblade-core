using System.Runtime.InteropServices;
using System.Text.Json;
using OpenBlade.Contracts;

namespace OpenBlade.Tray;

/// <summary>WUA automation using asynchronous jobs, default server/policy selection and retained search identities.</summary>
/// <remarks>Never construct this in tests. Callback IIDs/signatures are from Windows SDK 10.0.26100 wuapi.h.
/// WUA offers hardware IDs, not device instance targeting: the caller must admit every matching present devnode.</remarks>
internal sealed class RazerKeyboardWindowsUpdateNativeSession : IRazerKeyboardWindowsUpdateSession
{
    private readonly List<object> objects = [];
    private readonly Dictionary<RazerKeyboardWindowsUpdateIdentity, object> updates = [];
    private readonly dynamic session;
    private readonly TimeSpan searchTimeout;
    private readonly TimeSpan transferTimeout;
    private int pendingJobs;
    private bool disposed;
    private readonly Action<RazerKeyboardWindowsUpdateOperation>? lateOperationEvidence;

    public RazerKeyboardWindowsUpdateNativeSession(TimeSpan? searchTimeout = null, TimeSpan? transferTimeout = null,
        Action<RazerKeyboardWindowsUpdateOperation>? lateOperationEvidence = null)
    {
        this.lateOperationEvidence = lateOperationEvidence ?? (operation => DiagnosticTrace.TraceException(
            "SnapTapWindowsUpdate", operation.Stage,
            new InvalidOperationException("Late native completion does not clear the unresolved installation journal."),
            JsonSerializer.Serialize(operation)));
        this.searchTimeout = searchTimeout ?? TimeSpan.FromMinutes(3);
        this.transferTimeout = transferTimeout ?? TimeSpan.FromMinutes(10);
        session = Track(Activator.CreateInstance(Type.GetTypeFromProgID("Microsoft.Update.Session", throwOnError: true)!)!);
        session.ClientApplicationID = "OpenBlade reviewed keyboard driver setup";
    }

    public async Task<RazerKeyboardWindowsUpdateSearchResult> SearchAsync(string criteria, CancellationToken cancellationToken)
    {
        if (criteria != RazerKeyboardWindowsUpdateSource.AutomaticCriteria && criteria != RazerKeyboardWindowsUpdateSource.OptionalCriteria)
        {
            throw RazerKeyboardWindowsUpdateException.Refused("NativeAdmission", "SearchCriteriaNotAdmitted", new { Criteria = criteria });
        }

        dynamic searcher = Track(session.CreateUpdateSearcher());
        // Do not override ServerSelection, ServiceID, Online, policies, or Windows Update services.
        var callback = new RazerKeyboardWindowsUpdateSearchCallback();
        dynamic result = await RunJobAsync(() => searcher.BeginSearch(criteria, callback, null),
            job => searcher.EndSearch(job), callback.Completion, searchTimeout, cancellationToken).ConfigureAwait(false);
        dynamic collection = Track(result.Updates);
        if ((int)collection.Count > 512)
        {
            throw RazerKeyboardWindowsUpdateException.Refused("SearchMetadata", "SearchOfferCountExceeded", new { OfferCount = (int)collection.Count });
        }

        List<RazerKeyboardWindowsUpdateOffer> offers = [];
        for (int i = 0; i < (int)collection.Count; i++)
        {
            dynamic update = Track(collection.Item(i));
            RazerKeyboardWindowsUpdateOffer offer = ReadOffer(update);
            updates[offer.Identity] = update;
            offers.Add(offer);
        }
        List<int> warnings = [];
        dynamic nativeWarnings = Track(result.Warnings);
        for (int i = 0; i < (int)nativeWarnings.Count; i++)
        {
            dynamic warning = Track(nativeWarnings.Item(i));
            warnings.Add((int)warning.HResult);
        }
        return new(offers, new("Search", (int)result.ResultCode, 0, []) { NativeErrors = warnings.ToArray() });
    }

    public async Task<RazerKeyboardWindowsUpdateOperation> DownloadAsync(RazerKeyboardWindowsUpdateSelection selection,
        CancellationToken cancellationToken)
    {
        dynamic downloader = Track(session.CreateUpdateDownloader());
        downloader.Updates = BuildCollection(selection);
        var callback = new RazerKeyboardWindowsUpdateDownloadCallback();
        dynamic result = await RunJobAsync(() => downloader.BeginDownload(null, callback, null),
            job => downloader.EndDownload(job), callback.Completion, transferTimeout, cancellationToken,
            late => lateOperationEvidence?.Invoke(ReadResult("Download after abort", late, selection, install: false))).ConfigureAwait(false);
        return ReadResult("Download", result, selection, install: false);
    }

    public async Task<RazerKeyboardWindowsUpdateOperation> InstallAsync(RazerKeyboardWindowsUpdateSelection selection,
        Action beforeSubmission, CancellationToken cancellationToken)
    {
        dynamic installer = Track(session.CreateUpdateInstaller());
        installer.Updates = BuildCollection(selection);
        installer.AllowSourcePrompts = false;
        // IsBusy is advisory only; BeginInstall can still report a concurrent Windows-owned operation.
        if ((bool)installer.IsBusy)
        {
            throw new RazerKeyboardWindowsUpdateException("Windows Update is already installing updates.", unchecked((int)0x80240009));
        }

        if ((bool)installer.RebootRequiredBeforeInstallation)
        {
            throw new RazerKeyboardWindowsUpdateException("Windows requires a restart before driver installation.", unchecked((int)0x80240016), restartRequired: true);
        }

        var callback = new RazerKeyboardWindowsUpdateInstallCallback();
        dynamic result = await RunJobAsync(() =>
        {
            beforeSubmission();
            return installer.BeginInstall(null, callback, null);
        }, job => installer.EndInstall(job), callback.Completion, transferTimeout, cancellationToken,
            late => lateOperationEvidence?.Invoke(ReadResult("Install after abort", late, selection, install: true))).ConfigureAwait(false);
        return ReadResult("Install", result, selection, install: true);
    }

    private object BuildCollection(RazerKeyboardWindowsUpdateSelection selection)
    {
        dynamic collection = Track(Activator.CreateInstance(Type.GetTypeFromProgID("Microsoft.Update.UpdateColl", throwOnError: true)!)!);
        foreach (RazerKeyboardWindowsUpdateOffer expected in selection.Offers)
        {
            if (!updates.TryGetValue(expected.Identity, out object? update))
            {
                throw RazerKeyboardWindowsUpdateException.Refused("NativeAdmission", "SelectedUpdateNotInSession", new { Offer = expected });
            }

            RazerKeyboardWindowsUpdateOffer current = ReadOffer(update);
            string? refusal = current.Identity != expected.Identity ? "SelectedUpdateIdentityChanged"
                : !current.Entries.SequenceEqual(expected.Entries) ? "SelectedUpdateEntriesChanged"
                : !current.IsDriver ? "NonDriverOffer"
                : current.IsHidden ? "HiddenOffer"
                : current.IsInstalled ? "AlreadyInstalledOffer"
                : current.HasBundledUpdates ? "BundledUpdatesRefused"
                : current.RequiresUserInput ? "InteractiveUpdateRefused"
                : !current.EulaAccepted ? "LicenseNotAccepted" : null;
            if (refusal is not null)
            {
                throw RazerKeyboardWindowsUpdateException.Refused("NativeAdmission", refusal,
                    new { ExpectedOffer = expected, Offer = current });
            }

            collection.Add(update); // Actual searched COM object, never a re-query or arbitrary update ID.
        }
        return collection;
    }

    private RazerKeyboardWindowsUpdateOffer ReadOffer(dynamic update)
    {
        dynamic identity = Track(update.Identity);
        var id = new RazerKeyboardWindowsUpdateIdentity((string)identity.UpdateID, (int)identity.RevisionNumber);
        dynamic bundled = Track(update.BundledUpdates);
        dynamic behavior = Track(update.InstallationBehavior);
        List<RazerKeyboardWindowsUpdateEntry> entries = [];
        if ((int)update.Type == 2)
        {
            // Require IWindowsDriverUpdate4 metadata. If unavailable, fail closed rather than assume a single target.
            dynamic driverEntries = Track(update.WindowsDriverUpdateEntries);
            if ((int)driverEntries.Count > 16)
            {
                throw RazerKeyboardWindowsUpdateException.Refused("SearchMetadata", "DriverEntryCountExceeded",
                    new
                    {
                        Identity = id,
                        DriverEntryCount = (int)driverEntries.Count,
                        Entries = entries,
                        IsDriver = true,
                        IsHidden = (bool)update.IsHidden,
                        IsInstalled = (bool)update.IsInstalled,
                        HasBundledUpdates = (int)bundled.Count != 0,
                        RequiresUserInput = (bool)behavior.CanRequestUserInput,
                        EulaAccepted = (bool)update.EulaAccepted,
                    });
            }

            for (int i = 0; i < (int)driverEntries.Count; i++)
            {
                dynamic entry = Track(driverEntries.Item(i));
                entries.Add(new((string)entry.DriverHardwareID, (string)entry.DriverProvider));
            }
            // Older metadata can have no entries. Do not invent a target from the title.
            if (entries.Count == 0)
            {
                throw RazerKeyboardWindowsUpdateException.Refused("SearchMetadata", "DriverEntriesMissing", new
                {
                    Identity = id,
                    Entries = entries,
                    IsDriver = true,
                    IsHidden = (bool)update.IsHidden,
                    IsInstalled = (bool)update.IsInstalled,
                    HasBundledUpdates = (int)bundled.Count != 0,
                    RequiresUserInput = (bool)behavior.CanRequestUserInput,
                    EulaAccepted = (bool)update.EulaAccepted,
                });
            }
        }
        return new(id, entries.ToArray(), (int)update.Type == 2, (bool)update.IsHidden, (bool)update.IsInstalled,
            (int)bundled.Count != 0, (bool)behavior.CanRequestUserInput, (bool)update.EulaAccepted);
    }

    private RazerKeyboardWindowsUpdateOperation ReadResult(string stage, dynamic result,
        RazerKeyboardWindowsUpdateSelection selection, bool install)
    {
        List<RazerKeyboardWindowsUpdateItemResult> items = [];
        for (int i = 0; i < selection.Offers.Count; i++)
        {
            dynamic item = Track(result.GetUpdateResult(i));
            items.Add(new(selection.Offers[i].Identity, (int)item.ResultCode, (int)item.HResult,
                install && (bool)item.RebootRequired));
        }
        return new(stage, (int)result.ResultCode, (int)result.HResult, items.ToArray(), install && (bool)result.RebootRequired);
    }

    private Task<object> RunJobAsync(Func<object> begin, Func<dynamic, object> end,
        Task completion, TimeSpan timeout, CancellationToken cancellationToken, Action<dynamic>? lateResult = null)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (objects)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (pendingJobs != 0)
            {
                throw RazerKeyboardWindowsUpdateException.Refused("NativeAdmission", "NativeJobAlreadyActive", new { PendingJobs = pendingJobs });
            }
            pendingJobs++;
        }

        return RazerKeyboardWindowsUpdateJob.RunAsync(() => Track(begin()), job => Track(end(job)),
            job => ((dynamic)job).RequestAbort(), job => ((dynamic)job).CleanUp(), completion,
            timeout, cancellationToken, JobFinished, late => lateResult?.Invoke(late));
    }
    private object Track(object value)
    {
        lock (objects)
        {
            objects.Add(value);
        }

        return value;
    }

    private void JobFinished()
    {
        lock (objects)
        {
            pendingJobs--;
            if (disposed && pendingJobs == 0)
            {
                ReleaseObjects();
            }
        }
    }

    public void Dispose()
    {
        lock (objects)
        {
            disposed = true;
            if (pendingJobs == 0)
            {
                ReleaseObjects();
            }
        }
    }

    private void ReleaseObjects()
    {
        // Release each acquired RCW reference once; never FinalRelease shared COM identity.
        for (int i = objects.Count - 1; i >= 0; i--)
        {
            if (Marshal.IsComObject(objects[i]))
            {
                Marshal.ReleaseComObject(objects[i]);
            }
        }

        objects.Clear();
        updates.Clear();
    }
}

// The SDK callbacks inherit IUnknown, not IDispatch. Public visibility permits CCW QueryInterface.
[ComVisible(true), Guid("88AEE058-D4B0-4725-A2F1-814A67AE964C"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface IRazerKeyboardWindowsUpdateSearchCallback
{
    void Invoke([MarshalAs(UnmanagedType.Interface)] object job, [MarshalAs(UnmanagedType.Interface)] object args);
}
[ComVisible(true), ClassInterface(ClassInterfaceType.None)]
public sealed class RazerKeyboardWindowsUpdateSearchCallback : IRazerKeyboardWindowsUpdateSearchCallback
{
    private readonly TaskCompletionSource completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    internal Task Completion => completion.Task;
    public void Invoke(object job, object args) => completion.TrySetResult();
}
[ComVisible(true), Guid("77254866-9F5B-4C8E-B9E2-C77A8530D64B"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface IRazerKeyboardWindowsUpdateDownloadCallback
{
    void Invoke([MarshalAs(UnmanagedType.Interface)] object job, [MarshalAs(UnmanagedType.Interface)] object args);
}
[ComVisible(true), ClassInterface(ClassInterfaceType.None)]
public sealed class RazerKeyboardWindowsUpdateDownloadCallback : IRazerKeyboardWindowsUpdateDownloadCallback
{
    private readonly TaskCompletionSource completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    internal Task Completion => completion.Task;
    public void Invoke(object job, object args) => completion.TrySetResult();
}
[ComVisible(true), Guid("45F4F6F3-D602-4F98-9A8A-3EFA152AD2D3"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface IRazerKeyboardWindowsUpdateInstallCallback
{
    void Invoke([MarshalAs(UnmanagedType.Interface)] object job, [MarshalAs(UnmanagedType.Interface)] object args);
}
[ComVisible(true), ClassInterface(ClassInterfaceType.None)]
public sealed class RazerKeyboardWindowsUpdateInstallCallback : IRazerKeyboardWindowsUpdateInstallCallback
{
    private readonly TaskCompletionSource completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    internal Task Completion => completion.Task;
    public void Invoke(object job, object args) => completion.TrySetResult();
}
