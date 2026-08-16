using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Windows.Interop;
using System.Windows.Threading;
using WixToolset.BootstrapperApplicationApi;

namespace OpenBlade.Installer.UI;

internal sealed class OpenBladeBootstrapperApplication : BootstrapperApplication
{
    private const string ProgressPipeVariable = "OpenBladeSetupProgressPipe";
    private const string PurgeProfilesVariable = "OpenBladePurgeProfiles";
    private const string BundleOwnerIdVariable = "OpenBladeBundleOwnerId";
    private const string InstalledVersionVariable = "OpenBladeInstalledVersion";
    private const string LaunchTargetVariable = "LaunchTarget";
    private const string LaunchWorkingFolderVariable = "LaunchWorkingFolder";
    private const string BundleLogVariable = "WixBundleLog";
    private const string BundleVersionVariable = "WixBundleVersion";
    private const string OpenBladeSetupPackage = "OpenBladeSetup";
    private const int ProductVersionConflict = unchecked((int)0x80070666);
    private const int InstallerConfigurationCorrupt = unchecked((int)0x8007064A);

    private IBootstrapperCommand? command;
    private BootstrapperApplicationData? applicationData;
    private Dispatcher? dispatcher;
    private InstallerWindow? window;
    private HwndSource? hiddenApplyOwner;
    private IntPtr hiddenApplyOwnerHandle;
    private InstallerProgressPipe? progressPipe;
    private LaunchAction plannedAction = LaunchAction.Unknown;
    private bool installed;
    private bool applying;
    private bool cancellationRequested;
    private bool allowNewerVersionOverwrite;
    private bool purgeProfilesRequested;
    private bool sameVersionRelatedBundleCached;
    private bool sameVersionMaintenance;
    private int phaseCount = 2;
    private int cacheProgress;
    private int executeProgress;
    private int result;
    private string? lastError;
    private string bundleVersion = "unknown";
    private string? newerRelatedBundleVersion;
    private string? sameVersionRelatedBundleCode;
    private string? sameVersionRelatedBundleVersion;
    private readonly HashSet<string> newerRelatedBundleCodes =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> upgradeRelatedBundleVersions =
        new(StringComparer.OrdinalIgnoreCase);

    private IEngine Engine => engine;

    public OpenBladeBootstrapperApplication()
    {
        DetectBegin += OnDetectBegin;
        DetectRelatedBundle += OnDetectRelatedBundle;
        DetectComplete += OnDetectComplete;
        PlanBegin += OnPlanBegin;
        PlanRelatedBundle += OnPlanRelatedBundle;
        PlanRelatedBundleType += OnPlanRelatedBundleType;
        PlanPackageBegin += OnPlanPackageBegin;
        PlanComplete += OnPlanComplete;
        ApplyBegin += OnApplyBegin;
        CacheAcquireProgress += OnCacheAcquireProgress;
        CacheContainerOrPayloadVerifyProgress += OnCacheContainerOrPayloadVerifyProgress;
        CachePayloadExtractProgress += OnCachePayloadExtractProgress;
        CacheVerifyProgress += OnCacheVerifyProgress;
        ExecutePackageBegin += OnExecutePackageBegin;
        ExecuteProgress += OnExecuteProgress;
        ExecutePackageComplete += OnExecutePackageComplete;
        Progress += OnProgress;
        Error += OnError;
        ApplyDowngrade += OnApplyDowngrade;
        ApplyComplete += OnApplyComplete;
    }

    public void StartAction(
        LaunchAction action,
        bool purgeProfiles,
        bool overwriteNewerVersion = false)
    {
        if (applying || command is null)
        {
            return;
        }

        if (ShouldBlockMultipleRelatedBundles(
                action,
                upgradeRelatedBundleVersions.Count))
        {
            RejectMultipleRelatedBundles(upgradeRelatedBundleVersions.Count);
            return;
        }

        if (sameVersionRelatedBundleVersion is string sameVersion
            && ShouldRejectSameVersionAction(
                action,
                CanOfferSameVersionMaintenance(),
                installed))
        {
            RejectSameVersionReplacement(sameVersion);
            return;
        }

        if (newerRelatedBundleVersion is not null
            && action is LaunchAction.Install or LaunchAction.Repair
            && (!overwriteNewerVersion || command.Display != Display.Full))
        {
            RequireNewerVersionOverwriteConfirmation();
            return;
        }

        if (RequiresBundleOwnerId(action) && !EnsureBundleOwnerId())
        {
            return;
        }

        plannedAction = action;
        sameVersionMaintenance = command.Relation == RelationType.None
            && action is LaunchAction.Repair or LaunchAction.Uninstall
            && CanOfferSameVersionMaintenance();
        allowNewerVersionOverwrite = overwriteNewerVersion
            && newerRelatedBundleVersion is not null
            && command.Display == Display.Full;
        cancellationRequested = false;
        purgeProfilesRequested = purgeProfiles;
        cacheProgress = 0;
        executeProgress = 0;
        Engine.SetVariableNumeric(PurgeProfilesVariable, purgeProfiles ? 1 : 0);

        progressPipe?.Dispose();
        progressPipe = null;
        if (command.Display == Display.Full)
        {
            progressPipe = InstallerProgressPipe.Start(AddPipeDetail);
            Engine.SetVariableString(ProgressPipeVariable, progressPipe.Name, false);
        }
        else
        {
            Engine.SetVariableString(ProgressPipeVariable, string.Empty, false);
        }

        Ui(() => window?.ShowProgress(action));
        AddDetail($"Planning {DescribeAction(action).ToLowerInvariant()}.");
        Engine.Plan(
            SelectEngineAction(action, sameVersionMaintenance),
            BundleScope.Default);
    }

    public void RequestCancel()
    {
        if (!applying || cancellationRequested)
        {
            return;
        }

        cancellationRequested = true;
        Ui(() => window?.MarkCancellationRequested());
    }

    public bool CanClose()
    {
        if (!applying)
        {
            return true;
        }

        RequestCancel();
        return false;
    }

    public void LaunchOpenBlade()
    {
        try
        {
            string target = Engine.FormatString(
                Engine.GetVariableString(LaunchTargetVariable));
            string workingFolder = Engine.FormatString(
                Engine.GetVariableString(LaunchWorkingFolderVariable));
            Process.Start(new ProcessStartInfo(target)
            {
                UseShellExecute = true,
                WorkingDirectory = workingFolder,
            });
            window?.Close();
        }
        catch (Exception exception)
        {
            Ui(() => window?.ShowFailure(
                $"OpenBlade was installed, but it could not be launched: {exception.Message}",
                TryGetVariable(BundleLogVariable)));
        }
    }

    public void OpenPath(string path)
    {
        try
        {
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        }
        catch (Exception exception)
        {
            Ui(() => window?.ShowFailure(exception.Message, TryGetVariable(BundleLogVariable)));
        }
    }

    public void Close()
    {
        if (applying)
        {
            return;
        }

        progressPipe?.Dispose();
        progressPipe = null;
        dispatcher?.BeginInvokeShutdown(DispatcherPriority.Normal);
    }

    protected override void OnCreate(CreateEventArgs args)
    {
        base.OnCreate(args);
        command = args.Command;
        applicationData = new BootstrapperApplicationData();
    }

    protected override void Run()
    {
        try
        {
            RunCore();
        }
        catch (Exception exception)
        {
            QuitAfterRunFailure(exception);
        }
    }

    private void RunCore()
    {
        if (command is null)
        {
            Engine.Quit(unchecked((int)0x80004005));
            return;
        }

        dispatcher = Dispatcher.CurrentDispatcher;
        bundleVersion = TryGetVariable(BundleVersionVariable) ?? "unknown";
        hiddenApplyOwner = CreateHiddenApplyOwner();
        hiddenApplyOwnerHandle = hiddenApplyOwner.Handle;
        try
        {
            if (command.Display is Display.Full or Display.Passive)
            {
                window = new InstallerWindow(
                    this,
                    bundleVersion,
                    allowInteraction: command.Display == Display.Full);
                window.Show();
            }

            Engine.Detect();
            Dispatcher.Run();
        }
        finally
        {
            progressPipe?.Dispose();
            progressPipe = null;
            DisposeHiddenApplyOwner();
        }

        Engine.Quit(NormalizeExitCode(result));
    }

    private void OnDetectBegin(object? sender, DetectBeginEventArgs e)
    {
        installed = e.RegistrationType == RegistrationType.Full;
        allowNewerVersionOverwrite = false;
        newerRelatedBundleCodes.Clear();
        upgradeRelatedBundleVersions.Clear();
        newerRelatedBundleVersion = null;
        sameVersionRelatedBundleCode = null;
        sameVersionRelatedBundleVersion = null;
        sameVersionRelatedBundleCached = false;
        sameVersionMaintenance = false;
        AddDetail("Checking the installed OpenBlade version.");
    }

    private void OnDetectRelatedBundle(object? sender, DetectRelatedBundleEventArgs e)
    {
        if (applicationData is not null
            && !applicationData.Bundle.Packages.ContainsKey(e.ProductCode))
        {
            applicationData.Bundle.AddRelatedBundleAsPackage(
                e.ProductCode,
                e.RelationType,
                e.PerMachine,
                e.Version);
        }

        if (e.RelationType == RelationType.Upgrade
            && !string.IsNullOrWhiteSpace(e.ProductCode))
        {
            // Upgrade relations share OpenBlade's stable upgrade code and are the
            // only related bundles Burn can plan as replacement predecessors.
            upgradeRelatedBundleVersions[e.ProductCode] = e.Version;
            if (IsSameVersion(bundleVersion, e.Version))
            {
                sameVersionRelatedBundleCode = e.ProductCode;
                sameVersionRelatedBundleVersion = e.Version;
                sameVersionRelatedBundleCached = !e.MissingFromCache;
            }
            if (IsNewerVersion(bundleVersion, e.Version))
            {
                newerRelatedBundleCodes.Add(e.ProductCode);
                if (newerRelatedBundleVersion is null
                    || IsNewerVersion(newerRelatedBundleVersion, e.Version))
                {
                    newerRelatedBundleVersion = e.Version;
                }
            }
        }
    }

    private void OnDetectComplete(object? sender, DetectCompleteEventArgs e)
    {
        if (Failed(e.Status))
        {
            CompleteWithFailure(e.Status, "OpenBlade setup could not inspect the installed state.");
            return;
        }

        if (command is null)
        {
            CompleteWithFailure(unchecked((int)0x80004005), "The setup command is unavailable.");
            return;
        }

        if (command.Action == LaunchAction.Help)
        {
            result = 0;
            if (command.Display == Display.Full)
            {
                Ui(() => window?.ShowHelp());
            }
            else
            {
                dispatcher?.BeginInvokeShutdown(DispatcherPriority.Normal);
            }

            return;
        }

        string? installedVersion = TryGetVariable(InstalledVersionVariable);
        if (installedVersion is not null
            && IsNewerVersion(bundleVersion, installedVersion)
            && (newerRelatedBundleVersion is null
                || IsNewerVersion(newerRelatedBundleVersion, installedVersion)))
        {
            newerRelatedBundleVersion = installedVersion;
        }

        LaunchAction detectionAction = SelectDetectionGateAction(
            command.Action,
            command.Display);
        if (ShouldBlockMultipleRelatedBundles(
                detectionAction,
                upgradeRelatedBundleVersions.Count))
        {
            RejectMultipleRelatedBundles(upgradeRelatedBundleVersions.Count);
            return;
        }

        if (sameVersionRelatedBundleVersion is string maintenanceVersion
            && ShouldOfferSameVersionMaintenance(
                command.Action,
                command.Display,
                CanOfferSameVersionMaintenance()))
        {
            Ui(() => window?.ShowSameVersionMaintenance(maintenanceVersion));
            return;
        }

        if (sameVersionRelatedBundleVersion is string sameVersion
            && ShouldRejectSameVersionAction(
                detectionAction,
                CanOfferSameVersionMaintenance(),
                installed))
        {
            RejectSameVersionReplacement(sameVersion);
            return;
        }

        if (newerRelatedBundleVersion is not null
            && detectionAction is LaunchAction.Install or LaunchAction.Repair)
        {
            RequireNewerVersionOverwriteConfirmation();
            return;
        }

        if (command.Action == LaunchAction.Layout || command.Display != Display.Full)
        {
            StartAction(command.Action, purgeProfiles: false);
            return;
        }

        Ui(() => window?.ShowReady(installed));
    }

    private void OnPlanBegin(object? sender, PlanBeginEventArgs e)
    {
        AddDetail($"Planning {DescribeAction(plannedAction).ToLowerInvariant()} packages.");
        e.Cancel = cancellationRequested;
    }

    private void OnPlanRelatedBundle(object? sender, PlanRelatedBundleEventArgs e)
    {
        bool detectedSameVersionBundle = IsSameVersionRelatedBundle(e.BundleCode);
        RequestState selected = SelectRelatedBundleRequestState(
            sameVersionMaintenance,
            plannedAction,
            detectedSameVersionBundle,
            e.RecommendedState);
        if (selected != e.State)
        {
            e.State = selected;
            AddDetail("Removing the installed equal-version OpenBlade bundle.");
        }

        e.Cancel = cancellationRequested;
    }

    private void OnPlanRelatedBundleType(object? sender, PlanRelatedBundleTypeEventArgs e)
    {
        bool detectedNewerBundle = newerRelatedBundleCodes.Contains(e.BundleCode);
        bool detectedSameVersionBundle =
            upgradeRelatedBundleVersions.TryGetValue(e.BundleCode, out string? relatedVersion)
            && IsSameVersion(bundleVersion, relatedVersion);
        RelatedBundlePlanType selected = SelectRelatedBundlePlanType(
            allowNewerVersionOverwrite,
            sameVersionMaintenance,
            command?.Relation != RelationType.None,
            detectedNewerBundle,
            detectedSameVersionBundle,
            e.RecommendedType);
        if (selected != e.Type)
        {
            e.Type = selected;
            AddDetail(selected == RelatedBundlePlanType.None
                ? $"Skipping an earlier OpenBlade {relatedVersion} registration to avoid an equal-version replacement cycle."
                : $"Replacing newer OpenBlade {relatedVersion} after user confirmation.");
        }

        e.Cancel = cancellationRequested;
    }

    private void OnPlanPackageBegin(object? sender, PlanPackageBeginEventArgs e)
    {
        RequestState selected = SelectPackageRequestState(
            allowNewerVersionOverwrite,
            sameVersionMaintenance,
            plannedAction,
            e.PackageId,
            e.RecommendedState);
        if (selected != e.State)
        {
            e.State = selected;
            AddDetail("Forcing the confirmed OpenBlade version replacement.");
        }

        AddDetail($"Evaluating {GetPackageName(e.PackageId)}.");
        e.Cancel = cancellationRequested;
    }

    private void OnPlanComplete(object? sender, PlanCompleteEventArgs e)
    {
        if (Failed(e.Status))
        {
            CompleteWithFailure(e.Status, lastError ?? "OpenBlade setup could not prepare the requested action.");
            return;
        }

        applying = true;
        AddDetail("Applying the requested changes.");
        Engine.Apply(GetApplyOwnerHandle());
    }

    private void OnApplyBegin(object? sender, ApplyBeginEventArgs e)
    {
        phaseCount = Math.Max(1, e.PhaseCount);
        e.Cancel = cancellationRequested;
    }

    private void OnCacheAcquireProgress(object? sender, CacheAcquireProgressEventArgs e)
    {
        cacheProgress = e.OverallPercentage;
        UpdateCalculatedProgress();
        e.Cancel = cancellationRequested;
    }

    private void OnCacheContainerOrPayloadVerifyProgress(
        object? sender,
        CacheContainerOrPayloadVerifyProgressEventArgs e)
    {
        cacheProgress = e.OverallPercentage;
        UpdateCalculatedProgress();
        e.Cancel = cancellationRequested;
    }

    private void OnCachePayloadExtractProgress(object? sender, CachePayloadExtractProgressEventArgs e)
    {
        cacheProgress = e.OverallPercentage;
        UpdateCalculatedProgress();
        e.Cancel = cancellationRequested;
    }

    private void OnCacheVerifyProgress(object? sender, CacheVerifyProgressEventArgs e)
    {
        cacheProgress = e.OverallPercentage;
        UpdateCalculatedProgress();
        e.Cancel = cancellationRequested;
    }

    private void OnExecutePackageBegin(object? sender, ExecutePackageBeginEventArgs e)
    {
        string package = GetPackageName(e.PackageId);
        bool relatedBundle = upgradeRelatedBundleVersions.TryGetValue(
            e.PackageId,
            out string relatedVersion);
        string phase = relatedBundle
            ? DescribeRelatedBundleExecution(relatedVersion)
            : package;
        AddDetail(relatedBundle
            ? $"{phase}."
            : $"Running {phase}.");
        Ui(() => window?.SetProgress(CalculatedProgress(), phase));
        e.Cancel = cancellationRequested;
    }

    private void OnExecuteProgress(object? sender, ExecuteProgressEventArgs e)
    {
        executeProgress = e.OverallPercentage;
        UpdateCalculatedProgress();
        e.Cancel = cancellationRequested;
    }

    private void OnExecutePackageComplete(object? sender, ExecutePackageCompleteEventArgs e)
    {
        AddDetail(Failed(e.Status)
            ? $"{GetPackageName(e.PackageId)} failed with 0x{e.Status:X8}."
            : $"{GetPackageName(e.PackageId)} completed.");
    }

    private void OnProgress(object? sender, ProgressEventArgs e)
    {
        e.Cancel = cancellationRequested;
    }

    private void OnError(object? sender, WixToolset.BootstrapperApplicationApi.ErrorEventArgs e)
    {
        lastError = e.ErrorMessage;
        AddDetail($"Error: {e.ErrorMessage}");
        if (cancellationRequested)
        {
            e.Result = Result.Cancel;
        }
    }

    private void OnApplyDowngrade(object? sender, ApplyDowngradeEventArgs e)
    {
        int selected = SelectApplyDowngradeStatus(allowNewerVersionOverwrite, e.Status);
        if (selected != e.Status)
        {
            e.Status = selected;
            AddDetail("Continuing with the confirmed OpenBlade version replacement.");
        }
    }

    private void OnApplyComplete(object? sender, ApplyCompleteEventArgs e)
    {
        applying = false;
        result = e.Status;
        progressPipe?.Dispose();
        progressPipe = null;

        if (command?.Display != Display.Full)
        {
            dispatcher?.BeginInvokeShutdown(DispatcherPriority.Normal);
            return;
        }

        if (Failed(e.Status))
        {
            Ui(() => window?.ShowFailure(
                cancellationRequested
                    ? "Setup was cancelled and the previous state was restored."
                    : lastError ?? $"Setup failed with 0x{e.Status:X8}.",
                TryGetVariable(BundleLogVariable)));
        }
        else
        {
            Ui(() => window?.ShowSuccess(plannedAction, purgeProfilesRequested));
        }
    }

    private void CompleteWithFailure(int status, string message)
    {
        result = status;
        if (command?.Display == Display.Full)
        {
            Ui(() => window?.ShowFailure(message, TryGetVariable(BundleLogVariable)));
        }
        else
        {
            dispatcher?.BeginInvokeShutdown(DispatcherPriority.Normal);
        }
    }

    private void RequireNewerVersionOverwriteConfirmation()
    {
        string installedVersion = newerRelatedBundleVersion
            ?? throw new InvalidOperationException("The newer OpenBlade version is unavailable.");
        AddDetail(
            $"OpenBlade {installedVersion} is already installed; this installer contains {bundleVersion}.");
        if (command?.Display == Display.Full)
        {
            Ui(() => window?.ShowNewerVersionConfirmation(
                installedVersion,
                bundleVersion,
                installed));
        }
        else
        {
            CompleteWithFailure(
                ProductVersionConflict,
                $"OpenBlade {installedVersion} is already installed. "
                + "Run this installer interactively to confirm overwriting it.");
        }
    }

    private void RejectSameVersionReplacement(string installedVersion)
    {
        bool canOfferMaintenance = CanOfferSameVersionMaintenance();
        string message =
            $"Another OpenBlade {installedVersion} installer build is already registered. "
            + SelectSameVersionConflictGuidance(
                canOfferMaintenance,
                sameVersionRelatedBundleCached);
        RejectRelatedBundleReplacement(
            SelectRelatedBundleConflictStatus(multipleRegistrations: false),
            message);
    }

    private void RejectMultipleRelatedBundles(int relatedBundleCount)
    {
        string message =
            $"OpenBlade setup found {relatedBundleCount} related installer registrations. "
            + "Setup stopped before making changes because executing one legacy bundle can recursively launch the others. "
            + "Use Uninstall on each registered OpenBlade entry when available. "
            + "If duplicates remain, use Windows installer recovery before running this installer again.";
        RejectRelatedBundleReplacement(
            SelectRelatedBundleConflictStatus(multipleRegistrations: true),
            message);
    }

    private void RejectRelatedBundleReplacement(int status, string message)
    {
        AddDetail(message);
        result = status;
        if (command?.Display == Display.Full)
        {
            Ui(() => window?.ShowRegistrationConflict(message, installed));
        }
        else
        {
            dispatcher?.BeginInvokeShutdown(DispatcherPriority.Normal);
        }
    }

    private void UpdateCalculatedProgress()
    {
        int percentage = CalculatedProgress();
        Ui(() => window?.SetProgress(percentage));
        if (command?.Display == Display.Embedded)
        {
            Engine.SendEmbeddedProgress(executeProgress, percentage);
        }
    }

    private int CalculatedProgress() => (cacheProgress + executeProgress) / phaseCount;

    private string GetPackageName(string packageId)
    {
        if (upgradeRelatedBundleVersions.TryGetValue(packageId, out string? relatedVersion))
        {
            return $"OpenBlade {relatedVersion}";
        }

        if (applicationData?.Bundle.Packages.TryGetValue(packageId, out IPackageInfo? package) == true)
        {
            return string.IsNullOrWhiteSpace(package.DisplayName) ? packageId : package.DisplayName;
        }

        return packageId;
    }

    private bool CanOfferSameVersionMaintenance() =>
        CanOfferSameVersionMaintenance(
            installed,
            newerRelatedBundleVersion is not null,
            sameVersionRelatedBundleCached,
            sameVersionRelatedBundleCode is not null,
            upgradeRelatedBundleVersions.Count);

    private bool IsSameVersionRelatedBundle(string bundleCode) =>
        sameVersionRelatedBundleCode is not null
        && string.Equals(
            bundleCode,
            sameVersionRelatedBundleCode,
            StringComparison.OrdinalIgnoreCase);

    private bool EnsureBundleOwnerId()
    {
        string? current = TryGetVariable(BundleOwnerIdVariable);
        if (IsValidBundleOwnerId(current))
        {
            return true;
        }

        if (installed)
        {
            CompleteWithFailure(
                InstallerConfigurationCorrupt,
                "The registered OpenBlade installer ownership token is missing or malformed. "
                + "Use Windows installer recovery before trying again.");
            return false;
        }

        Engine.SetVariableString(
            BundleOwnerIdVariable,
            Guid.NewGuid().ToString("D"),
            false);
        return true;
    }

    private void AddDetail(string message)
    {
        Engine.Log(LogLevel.Standard, message);
        Ui(() => window?.AddDetail(message));
    }

    private void AddPipeDetail(string message)
    {
        Ui(() =>
        {
            Engine.Log(LogLevel.Standard, message);
            window?.AddDetail(message);
        });
    }

    private void QuitAfterRunFailure(Exception exception)
    {
        int status = exception.HResult < 0
            ? exception.HResult
            : unchecked((int)0x80004005);
        result = status;
        progressPipe?.Dispose();
        progressPipe = null;

        try
        {
            Engine.Log(
                LogLevel.Error,
                $"The OpenBlade installer UI failed: {exception.GetType().Name} (0x{status:X8}).");
        }
        catch
        {
        }

        try
        {
            Engine.Quit(NormalizeExitCode(status));
        }
        catch
        {
        }
    }

    private string? TryGetVariable(string name)
    {
        try
        {
            return Engine.ContainsVariable(name) ? Engine.GetVariableString(name) : null;
        }
        catch
        {
            return null;
        }
    }

    private void Ui(Action action)
    {
        Dispatcher? current = dispatcher;
        if (current is null || current.HasShutdownStarted)
        {
            return;
        }

        _ = current.BeginInvoke(action);
    }

    private IntPtr GetApplyOwnerHandle()
    {
        IntPtr visibleWindowHandle = window?.WindowHandle ?? IntPtr.Zero;
        IntPtr handle = visibleWindowHandle != IntPtr.Zero
            ? visibleWindowHandle
            : hiddenApplyOwnerHandle;
        if (handle == IntPtr.Zero)
        {
            throw new InvalidOperationException("The installer apply owner window is unavailable.");
        }

        return handle;
    }

    internal static HwndSource CreateHiddenApplyOwner()
    {
        const int windowStylePopup = unchecked((int)0x80000000);
        const int extendedWindowStyleToolWindow = 0x00000080;
        return new HwndSource(new HwndSourceParameters("OpenBlade Setup Apply Owner")
        {
            Width = 0,
            Height = 0,
            WindowStyle = windowStylePopup,
            ExtendedWindowStyle = extendedWindowStyleToolWindow,
        });
    }

    private void DisposeHiddenApplyOwner()
    {
        hiddenApplyOwner?.Dispose();
        hiddenApplyOwner = null;
        hiddenApplyOwnerHandle = IntPtr.Zero;
    }

    private static bool Failed(int status) => status < 0;

    internal static bool IsNewerVersion(string currentVersion, string relatedVersion)
    {
        Version? current = ParseBurnComparableVersion(currentVersion);
        Version? related = ParseBurnComparableVersion(relatedVersion);
        return current is not null && related is not null && related > current;
    }

    internal static bool IsSameVersion(string currentVersion, string relatedVersion)
    {
        Version? current = ParseBurnComparableVersion(currentVersion);
        Version? related = ParseBurnComparableVersion(relatedVersion);
        return current is not null && related is not null && related == current;
    }

    internal static string DescribeRelatedBundleExecution(string version) =>
        $"Removing OpenBlade {version}";

    private static Version? ParseBurnComparableVersion(string value)
    {
        if (!Version.TryParse(value, out Version? parsed))
        {
            return null;
        }

        return new Version(
            parsed.Major,
            parsed.Minor,
            Math.Max(0, parsed.Build),
            Math.Max(0, parsed.Revision));
    }

    internal static RelatedBundlePlanType SelectRelatedBundlePlanType(
        bool allowOverwrite,
        bool allowSameVersionMaintenance,
        bool relatedBundleInvocation,
        bool detectedNewerBundle,
        bool detectedSameVersionBundle,
        RelatedBundlePlanType recommendedType)
    {
        if (allowOverwrite
            && detectedNewerBundle
            && recommendedType == RelatedBundlePlanType.Downgrade)
        {
            return RelatedBundlePlanType.Upgrade;
        }

        if (detectedSameVersionBundle
            && (!allowSameVersionMaintenance || relatedBundleInvocation)
            && recommendedType == RelatedBundlePlanType.Upgrade)
        {
            // Related bundles must never plan the top-level equal-version bundle
            // that invoked them, or the cached installers can recurse.
            return RelatedBundlePlanType.None;
        }

        return recommendedType;
    }

    internal static bool ShouldRejectSameVersionAction(
        LaunchAction action,
        bool canOfferMaintenance,
        bool runningBundleInstalled) =>
        action switch
        {
            LaunchAction.Install => true,
            LaunchAction.Repair => !canOfferMaintenance,
            LaunchAction.Uninstall => !canOfferMaintenance && !runningBundleInstalled,
            _ => false,
        };

    internal static bool CanOfferSameVersionMaintenance(
        bool runningBundleInstalled,
        bool newerVersionDetected,
        bool relatedBundleCached,
        bool relatedBundleCodeDetected,
        int relatedBundleCount) =>
        !runningBundleInstalled
        && !newerVersionDetected
        && relatedBundleCached
        && relatedBundleCodeDetected
        && relatedBundleCount == 1;

    internal static bool ShouldOfferSameVersionMaintenance(
        LaunchAction action,
        Display display,
        bool canOfferMaintenance) =>
        canOfferMaintenance
        && display == Display.Full
        && action is LaunchAction.Unknown
            or LaunchAction.Install
            or LaunchAction.Repair
            or LaunchAction.Uninstall;

    internal static bool IsValidBundleOwnerId(string? value) =>
        Guid.TryParseExact(value, "D", out _);

    internal static bool RequiresBundleOwnerId(LaunchAction action) =>
        action is LaunchAction.Unknown
            or LaunchAction.Install
            or LaunchAction.Repair
            or LaunchAction.Uninstall;

    internal static string SelectSameVersionConflictGuidance(
        bool canOfferMaintenance,
        bool relatedBundleCached) =>
        canOfferMaintenance
            ? "Run this installer interactively and choose Repair or Uninstall."
            : !relatedBundleCached
                ? "Its cached installer is unavailable, so setup cannot safely repair or remove it. "
                    + "Use Windows installer recovery before trying again."
                : "Setup cannot safely choose one maintenance owner for this state. "
                    + "Use Windows Installed apps to repair or remove the registered bundle first.";

    internal static bool ShouldBlockMultipleRelatedBundles(
        LaunchAction action,
        int relatedBundleCount) =>
        relatedBundleCount > 1
        && action is LaunchAction.Install or LaunchAction.Repair;

    internal static LaunchAction SelectDetectionGateAction(
        LaunchAction action,
        Display display) =>
        display == Display.Full && action == LaunchAction.Unknown
            ? LaunchAction.Install
            : action;

    internal static int SelectRelatedBundleConflictStatus(bool multipleRegistrations) =>
        multipleRegistrations ? InstallerConfigurationCorrupt : ProductVersionConflict;

    internal static RequestState SelectPackageRequestState(
        bool allowOverwrite,
        bool sameVersionMaintenance,
        LaunchAction action,
        string packageId,
        RequestState recommendedState)
    {
        if (!string.Equals(packageId, OpenBladeSetupPackage, StringComparison.Ordinal))
        {
            return recommendedState;
        }

        if (sameVersionMaintenance)
        {
            // Keep Burn's recommended Absent state for uninstall. The incoming setup is
            // ownership-guarded and runs before the related bundle that owns the payload.
            return action switch
            {
                LaunchAction.Repair => RequestState.ForcePresent,
                _ => recommendedState,
            };
        }

        return allowOverwrite && action == LaunchAction.Install
            ? RequestState.ForcePresent
            : recommendedState;
    }

    internal static RequestState SelectRelatedBundleRequestState(
        bool sameVersionMaintenance,
        LaunchAction action,
        bool detectedSameVersionBundle,
        RequestState recommendedState) =>
        sameVersionMaintenance
        && action == LaunchAction.Uninstall
        && detectedSameVersionBundle
            ? RequestState.Absent
            : recommendedState;

    internal static LaunchAction SelectEngineAction(
        LaunchAction action,
        bool sameVersionMaintenance) =>
        sameVersionMaintenance && action == LaunchAction.Repair
            ? LaunchAction.Install
            : action;

    internal static int SelectApplyDowngradeStatus(bool allowOverwrite, int status) =>
        allowOverwrite && status == ProductVersionConflict ? 0 : status;

    private static int NormalizeExitCode(int status) =>
        (status & unchecked((int)0xFFFF0000)) == unchecked((int)0x80070000)
            ? status & 0xFFFF
            : status;

    private static string DescribeAction(LaunchAction action) => action switch
    {
        LaunchAction.Uninstall => "Uninstall",
        LaunchAction.Repair => "Repair",
        LaunchAction.Layout => "Layout",
        _ => "Install",
    };
}
