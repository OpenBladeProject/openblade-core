using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Windows.Interop;
using System.Windows.Threading;
using WixToolset.BootstrapperApplicationApi;

namespace OpenBlade.Installer.UI;

internal sealed class DetectedRelatedBundle
{
    internal DetectedRelatedBundle(
        string productCode,
        RelationType relationType,
        bool perMachine,
        string version,
        bool missingFromCache)
    {
        ProductCode = productCode;
        RelationType = relationType;
        PerMachine = perMachine;
        Version = version;
        MissingFromCache = missingFromCache;
    }

    internal string ProductCode { get; }

    internal RelationType RelationType { get; }

    internal bool PerMachine { get; }

    internal string Version { get; }

    internal bool MissingFromCache { get; }
}

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
    private const string DotNetDesktopRuntimeVersionVariable =
        "OpenBladeDotNetRuntimeVersion";
    private const string MinimumDotNetRuntimeVersionVariable =
        "OpenBladeMinimumDotNetRuntimeVersion";
    private const string DotNetRuntimeSufficientVariable =
        "OpenBladeDotNetRuntimeSufficient";
    private const string LocalDevCompatibilityStableBundleCode =
        "{54764E2D-D6B4-47A7-A6D6-A1EA6721640A}";
    private const string LocalDevCompatibilityStableBundleVersion = "0.19.18";
    private const string OpenBladeSetupPackage = "OpenBladeSetup";
    private const string OpenBladeVcRuntimePackage = "OpenBladeVcRuntime";
    private const string OpenBladeDotNetRuntimePackage = "OpenBladeDotNetRuntime";
    private const int ProductVersionConflict = unchecked((int)0x80070666);
    private const int InstallerConfigurationCorrupt = unchecked((int)0x8007064A);
    private readonly IUninstallPostconditionProbe uninstallPostconditionProbe =
        new WindowsUninstallPostconditionProbe();

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
    private bool purgeProfilesRequested;
    private bool sameVersionRelatedBundleCached;
    private bool sameVersionMaintenance;
    private bool uninstallRunningBundleInstalled;
    private bool uninstallSetupPackagePlanned;
    private bool dotNetDesktopRuntimeSufficient;
    private int phaseCount = 2;
    private int cacheProgress;
    private int executeProgress;
    private int result;
    private string? lastError;
    private string bundleVersion = "unknown";
    private string? newerRelatedBundleVersion;
    private string? sameVersionRelatedBundleCode;
    private string? sameVersionRelatedBundleVersion;
    private string? uninstallBundleOwnerId;
    private string? uninstallInstallDirectory;
    private UninstallMachineState? uninstallBeforeState;
    private RegistrationType? uninstallRegistrationAfterApply;
    private readonly Dictionary<string, DetectedRelatedBundle> upgradeRelatedBundles =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly List<DetectedRelatedBundle> upgradeRelatedBundleDetections = [];

#if OPENBLADE_LOCAL_DEV_DEPLOYMENT
    private const bool LocalDevCompatibilityEnabled = true;
#else
    private const bool LocalDevCompatibilityEnabled = false;
#endif

    private IEngine Engine => engine;

    public OpenBladeBootstrapperApplication()
    {
        DetectBegin += OnDetectBegin;
        DetectPackageBegin += OnDetectPackageBegin;
        DetectRelatedBundle += OnDetectRelatedBundle;
        DetectComplete += OnDetectComplete;
        PlanBegin += OnPlanBegin;
        PlanRelatedBundle += OnPlanRelatedBundle;
        PlanRelatedBundleType += OnPlanRelatedBundleType;
        PlanPackageBegin += OnPlanPackageBegin;
        PlannedPackage += OnPlannedPackage;
        PlanComplete += OnPlanComplete;
        ApplyBegin += OnApplyBegin;
        CacheAcquireProgress += OnCacheAcquireProgress;
        CacheContainerOrPayloadVerifyProgress += OnCacheContainerOrPayloadVerifyProgress;
        CachePayloadExtractProgress += OnCachePayloadExtractProgress;
        CacheVerifyProgress += OnCacheVerifyProgress;
        ExecutePackageBegin += OnExecutePackageBegin;
        ExecuteProgress += OnExecuteProgress;
        ExecutePackageComplete += OnExecutePackageComplete;
        UnregisterBegin += OnUnregisterBegin;
        Progress += OnProgress;
        Error += OnError;
        ApplyComplete += OnApplyComplete;
    }

    public void StartAction(
        LaunchAction action,
        bool purgeProfiles)
    {
        if (applying || command is null)
        {
            return;
        }

        if (ShouldBlockMultipleRelatedBundles(
                action,
                command.Display,
                command.Relation,
                bundleVersion,
                upgradeRelatedBundleDetections,
                LocalDevCompatibilityEnabled))
        {
            RejectMultipleRelatedBundles(upgradeRelatedBundleDetections.Count);
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

        if (ShouldRejectNewerVersionAction(
                newerRelatedBundleVersion is not null,
                action))
        {
            RejectNewerVersionReplacement();
            return;
        }

        bool runtimePromptRequired = ShouldPromptForDotNetRuntime(
            action,
            command.Display,
            dotNetDesktopRuntimeSufficient);
        bool runtimeConsentGranted = !runtimePromptRequired
            || window?.ConfirmDotNetRuntimeInstall() == true;
        if (!ShouldProceedAfterDotNetRuntimePrompt(
                runtimePromptRequired,
                runtimeConsentGranted))
        {
            AddDetail("Microsoft .NET 10 Desktop Runtime installation was declined before planning.");
            return;
        }

        if (RequiresBundleOwnerId(action) && !EnsureBundleOwnerId())
        {
            return;
        }

        sameVersionMaintenance = command.Relation == RelationType.None
            && action is LaunchAction.Repair or LaunchAction.Uninstall
            && CanOfferSameVersionMaintenance();
        if (action == LaunchAction.Uninstall
            && !PrepareUninstallPostcondition())
        {
            return;
        }

        plannedAction = action;
        cancellationRequested = false;
        purgeProfilesRequested = purgeProfiles;
        cacheProgress = 0;
        executeProgress = 0;
        uninstallSetupPackagePlanned = false;
        uninstallRegistrationAfterApply = null;
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
                InstallerStrings.Format(
                    "OpenBlade was installed, but it could not be launched: {0}",
                    exception.Message),
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
            Ui(() => window?.ShowFailure(
                InstallerStrings.Format("Windows could not open the requested path: {0}", exception.Message),
                TryGetVariable(BundleLogVariable)));
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
        upgradeRelatedBundles.Clear();
        upgradeRelatedBundleDetections.Clear();
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
            var detectedRelatedBundle = new DetectedRelatedBundle(
                e.ProductCode,
                e.RelationType,
                e.PerMachine,
                e.Version,
                e.MissingFromCache);
            upgradeRelatedBundles[e.ProductCode] = detectedRelatedBundle;
            upgradeRelatedBundleDetections.Add(detectedRelatedBundle);
            if (IsSameVersion(bundleVersion, e.Version))
            {
                sameVersionRelatedBundleCode = e.ProductCode;
                sameVersionRelatedBundleVersion = e.Version;
                sameVersionRelatedBundleCached = !e.MissingFromCache;
            }
            if (IsNewerVersion(bundleVersion, e.Version))
            {
                if (newerRelatedBundleVersion is null
                    || IsNewerVersion(newerRelatedBundleVersion, e.Version))
                {
                    newerRelatedBundleVersion = e.Version;
                }
            }
        }
    }

    private void OnDetectPackageBegin(object? sender, DetectPackageBeginEventArgs e)
    {
        if (ShouldInitializeDotNetRuntimeReadiness(e.PackageId))
        {
            dotNetDesktopRuntimeSufficient = IsDotNetDesktopRuntimeSufficient(
                TryGetVariable(DotNetDesktopRuntimeVersionVariable),
                TryGetVariable(MinimumDotNetRuntimeVersionVariable));
            Engine.SetVariableNumeric(
                DotNetRuntimeSufficientVariable,
                DotNetRuntimeSufficientVariableValue(dotNetDesktopRuntimeSufficient));
        }

        e.Cancel = cancellationRequested;
    }

    private void OnDetectComplete(object? sender, DetectCompleteEventArgs e)
    {
        if (Failed(e.Status))
        {
            CompleteWithFailure(
                e.Status,
                InstallerStrings.Get("OpenBlade setup could not inspect the installed state."));
            return;
        }

        if (command is null)
        {
            CompleteWithFailure(
                unchecked((int)0x80004005),
                InstallerStrings.Get("The setup command is unavailable."));
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
        dotNetDesktopRuntimeSufficient = IsDotNetDesktopRuntimeSufficient(
            TryGetVariable(DotNetDesktopRuntimeVersionVariable),
            TryGetVariable(MinimumDotNetRuntimeVersionVariable));
        AddDetail(dotNetDesktopRuntimeSufficient
            ? "Microsoft .NET 10 Desktop Runtime satisfies the required version."
            : "Microsoft .NET 10 Desktop Runtime must be installed or updated.");
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
                command.Display,
                command.Relation,
                bundleVersion,
                upgradeRelatedBundleDetections,
                LocalDevCompatibilityEnabled))
        {
            RejectMultipleRelatedBundles(upgradeRelatedBundleDetections.Count);
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

        if (ShouldRejectNewerVersionAction(
                newerRelatedBundleVersion is not null,
                detectionAction))
        {
            RejectNewerVersionReplacement();
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
        bool detectedSameVersionBundle =
            upgradeRelatedBundles.TryGetValue(
                e.BundleCode,
                out DetectedRelatedBundle? relatedBundle)
            && IsSameVersion(bundleVersion, relatedBundle.Version);
        string? relatedVersion = relatedBundle?.Version;
        RelatedBundlePlanType selected = SelectRelatedBundlePlanType(
            sameVersionMaintenance,
            command?.Relation != RelationType.None,
            detectedSameVersionBundle,
            e.RecommendedType);
        if (selected != e.Type)
        {
            e.Type = selected;
            AddDetail(
                $"Skipping an earlier OpenBlade {relatedVersion} registration to avoid an equal-version replacement cycle.");
        }

        e.Cancel = cancellationRequested;
    }

    private void OnPlanPackageBegin(object? sender, PlanPackageBeginEventArgs e)
    {
        RequestState selected = SelectPackageRequestState(
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

    private void OnPlannedPackage(object? sender, PlannedPackageEventArgs e)
    {
        if (plannedAction == LaunchAction.Uninstall
            && string.Equals(
                e.PackageId,
                OpenBladeSetupPackage,
                StringComparison.Ordinal))
        {
            uninstallSetupPackagePlanned = true;
        }
    }

    private void OnPlanComplete(object? sender, PlanCompleteEventArgs e)
    {
        if (Failed(e.Status))
        {
            CompleteWithFailure(
                e.Status,
                lastError is null
                    ? InstallerStrings.Get("OpenBlade setup could not prepare the requested action.")
                    : InstallerStrings.Format("OpenBlade setup could not prepare the requested action: {0}", lastError));
            return;
        }

        if (ShouldRejectUninstallPlan(
                plannedAction,
                uninstallRunningBundleInstalled,
                uninstallSetupPackagePlanned))
        {
            string message = InstallerStrings.Get(
                "Windows Installer did not plan this installer entry for removal. Another installer may still depend on it. Remove any dependent OpenBlade installer entry first, then try again.");
            AddDetail(message);
            CompleteWithFailure(InstallerConfigurationCorrupt, message);
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
        bool relatedBundle = upgradeRelatedBundles.TryGetValue(
            e.PackageId,
            out DetectedRelatedBundle? detectedRelatedBundle);
        string? relatedVersion = detectedRelatedBundle?.Version;
        string diagnosticPhase = relatedBundle
            ? $"Removing OpenBlade {relatedVersion}"
            : package;
        AddDetail(relatedBundle
            ? $"{diagnosticPhase}."
            : $"Running {diagnosticPhase}.");
        string visiblePhase = DescribePackageExecutionStatus(
            e.PackageId,
            relatedBundle ? relatedVersion : null);
        Ui(() => window?.SetProgress(CalculatedProgress(), visiblePhase));
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

    private void OnUnregisterBegin(object? sender, UnregisterBeginEventArgs e)
    {
        if (plannedAction != LaunchAction.Uninstall)
        {
            return;
        }

        uninstallRegistrationAfterApply = e.RegistrationType;
        AddDetail(
            $"Burn selected {e.RegistrationType} as the final installer registration state.");
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

    private void OnApplyComplete(object? sender, ApplyCompleteEventArgs e)
    {
        applying = false;
        progressPipe?.Dispose();
        progressPipe = null;

        int completionStatus = e.Status;
        string? completionFailure = null;
        UninstallOutcome uninstallOutcome = UninstallOutcome.Removed;
        if (!Failed(e.Status) && plannedAction == LaunchAction.Uninstall)
        {
            try
            {
                UninstallMachineState after = uninstallPostconditionProbe.Capture(
                    uninstallBundleOwnerId
                        ?? throw new InvalidOperationException(
                            "The uninstall bundle owner ID is unavailable."),
                    uninstallInstallDirectory
                        ?? throw new InvalidOperationException(
                            "The uninstall installation directory is unavailable."));
                uninstallOutcome = UninstallPostconditionClassifier.Classify(
                    uninstallBeforeState
                        ?? throw new InvalidOperationException(
                            "The pre-uninstall machine state is unavailable."),
                    after,
                    uninstallRunningBundleInstalled,
                    sameVersionMaintenance,
                    uninstallSetupPackagePlanned,
                    uninstallRegistrationAfterApply);
                if (uninstallOutcome == UninstallOutcome.Failed)
                {
                    completionFailure = InstallerStrings.Get(
                        "OpenBlade uninstall could not verify complete removal. The installer ownership marker, service, or installed files are still present.");
                }
            }
            catch (Exception exception)
            {
                uninstallOutcome = UninstallOutcome.Failed;
                completionFailure = InstallerStrings.Format(
                    "OpenBlade uninstall could not verify complete removal: {0}",
                    exception.Message);
            }

            completionStatus = SelectApplyCompletionStatus(
                e.Status,
                plannedAction,
                uninstallOutcome);
            if (completionFailure is not null)
            {
                AddDetail(completionFailure);
            }
        }

        result = completionStatus;

        if (command?.Display != Display.Full)
        {
            dispatcher?.BeginInvokeShutdown(DispatcherPriority.Normal);
            return;
        }

        if (Failed(completionStatus))
        {
            Ui(() => window?.ShowFailure(
                cancellationRequested
                    ? InstallerStrings.Get("Setup was cancelled and the previous state was restored.")
                    : completionFailure
                        ?? (lastError is null
                            ? InstallerStrings.Format("Setup failed with 0x{0:X8}.", completionStatus)
                            : InstallerStrings.Format("Setup failed: {0}", lastError)),
                TryGetVariable(BundleLogVariable)));
        }
        else
        {
            Ui(() => window?.ShowSuccess(
                plannedAction,
                purgeProfilesRequested,
                uninstallOutcome));
        }
    }

    private bool PrepareUninstallPostcondition()
    {
        try
        {
            uninstallBundleOwnerId = TryGetVariable(BundleOwnerIdVariable)
                ?? throw new InvalidOperationException(
                    "The bundle owner ID is unavailable.");
            uninstallInstallDirectory = Engine.FormatString(
                Engine.GetVariableString(LaunchWorkingFolderVariable));
            uninstallBeforeState = uninstallPostconditionProbe.Capture(
                uninstallBundleOwnerId,
                uninstallInstallDirectory);
            uninstallRunningBundleInstalled = installed;
            return true;
        }
        catch (Exception exception)
        {
            string message = InstallerStrings.Format(
                "OpenBlade uninstall could not inspect the installed state: {0}",
                exception.Message);
            AddDetail(message);
            CompleteWithFailure(InstallerConfigurationCorrupt, message);
            return false;
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

    private void RejectNewerVersionReplacement()
    {
        string installedVersion = newerRelatedBundleVersion
            ?? throw new InvalidOperationException("The newer OpenBlade version is unavailable.");
        string message = InstallerStrings.Format(
            "OpenBlade {0} is installed. This installer contains older version {1}. Setup made no changes. Use version {0} or a newer installer.",
            installedVersion,
            bundleVersion);
        AddDetail(message);
        result = ProductVersionConflict;
        if (command?.Display == Display.Full)
        {
            Ui(() => window?.ShowNewerVersionBlocked(
                installedVersion,
                bundleVersion));
        }
        else
        {
            dispatcher?.BeginInvokeShutdown(DispatcherPriority.Normal);
        }
    }

    private void RejectSameVersionReplacement(string installedVersion)
    {
        bool canOfferMaintenance = CanOfferSameVersionMaintenance();
        string message = InstallerStrings.Format(
            "Another OpenBlade {0} installer build is already registered. {1}",
            installedVersion,
            SelectSameVersionConflictGuidance(
                canOfferMaintenance,
                sameVersionRelatedBundleCached));
        RejectRelatedBundleReplacement(
            SelectRelatedBundleConflictStatus(multipleRegistrations: false),
            message);
    }

    private void RejectMultipleRelatedBundles(int relatedBundleCount)
    {
        string message = InstallerStrings.Format(
            "Setup found {0} OpenBlade installer entries and stopped to avoid a conflict. Uninstall each OpenBlade entry when available. If duplicate entries remain, use Windows installer recovery, then try again.",
            relatedBundleCount);
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
        if (upgradeRelatedBundles.TryGetValue(
                packageId,
                out DetectedRelatedBundle? relatedBundle))
        {
            return $"OpenBlade {relatedBundle.Version}";
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
            upgradeRelatedBundleDetections.Count);

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
                InstallerStrings.Get(
                    "The registered OpenBlade installer ownership token is missing or malformed. Use Windows installer recovery before trying again."));
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

    private void AddPipeDetail(InstallerSetupProgress progress)
    {
        Engine.Log(LogLevel.Standard, progress.WireValue);
        Ui(() =>
        {
            PresentPipeProgress(window, progress);
        });
    }

    internal static void PresentPipeProgress(
        InstallerWindow? target,
        InstallerSetupProgress progress)
    {
        string visibleProgress = LocalizeProgress(progress);
        target?.AddDetail(visibleProgress);
        target?.SetStatus(visibleProgress);
    }

    internal static string LocalizeProgress(InstallerSetupProgress progress) => progress.Code switch
    {
        InstallerSetupProgressCode.InspectPayload =>
            InstallerStrings.Get("Preparing OpenBlade files…"),
        InstallerSetupProgressCode.ReadInstallState or
        InstallerSetupProgressCode.ReadUninstallState =>
            InstallerStrings.Get("Reading the current installation…"),
        InstallerSetupProgressCode.RemoveLegacyRegistration =>
            InstallerStrings.Get("Removing an earlier OpenBlade registration…"),
        InstallerSetupProgressCode.StopServiceForReplacement or
        InstallerSetupProgressCode.StopTrayAndSessionAgent =>
            InstallerStrings.Get("Stopping OpenBlade components…"),
        InstallerSetupProgressCode.RemoveDynamicLighting or
        InstallerSetupProgressCode.RemoveDynamicLightingAndOwnership =>
            InstallerStrings.Get("Removing Dynamic Lighting registration…"),
        InstallerSetupProgressCode.ActivateFiles =>
            InstallerStrings.Get("Activating OpenBlade files…"),
        InstallerSetupProgressCode.RegisterDynamicLighting =>
            InstallerStrings.Get("Registering Dynamic Lighting…"),
        InstallerSetupProgressCode.WriteStandaloneUninstaller =>
            InstallerStrings.Get("Preparing Windows uninstall support…"),
        InstallerSetupProgressCode.InstallService =>
            InstallerStrings.Get("Installing the OpenBlade service…"),
        InstallerSetupProgressCode.VerifyServiceHealth =>
            InstallerStrings.Get("Checking the OpenBlade service…"),
        InstallerSetupProgressCode.CreateShortcuts =>
            InstallerStrings.Get("Creating OpenBlade shortcuts…"),
        InstallerSetupProgressCode.WriteBundleOwnership =>
            InstallerStrings.Get("Recording installer ownership…"),
        InstallerSetupProgressCode.RestorePreviousState or
        InstallerSetupProgressCode.RestoreRegistrationState or
        InstallerSetupProgressCode.RestoreFiles =>
            InstallerStrings.Get("Restoring the previous installation state…"),
        InstallerSetupProgressCode.RemoveShortcuts =>
            InstallerStrings.Get("Removing OpenBlade shortcuts…"),
        InstallerSetupProgressCode.RemoveService =>
            InstallerStrings.Get("Removing the OpenBlade service…"),
        InstallerSetupProgressCode.RemoveProgramFiles =>
            InstallerStrings.Get("Removing OpenBlade files…"),
        InstallerSetupProgressCode.RemoveSavedData =>
            InstallerStrings.Get("Removing saved OpenBlade data…"),
        InstallerSetupProgressCode.CopyFile =>
            InstallerStrings.Format("Copying {0}…", progress.Argument ?? string.Empty),
        InstallerSetupProgressCode.CommitFiles =>
            InstallerStrings.Get("Committing OpenBlade files…"),
        _ => InstallerStrings.Get("Applying the requested changes…"),
    };

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
        InstallerStrings.Format("Removing OpenBlade {0}", version);

    internal static string DescribePackageExecutionStatus(
        string packageId,
        string? relatedVersion)
    {
        if (relatedVersion is not null)
        {
            return DescribeRelatedBundleExecution(relatedVersion);
        }

        if (string.Equals(packageId, OpenBladeVcRuntimePackage, StringComparison.Ordinal))
        {
            return InstallerStrings.Get("Installing required Windows components…");
        }

        if (string.Equals(
                packageId,
                OpenBladeDotNetRuntimePackage,
                StringComparison.Ordinal))
        {
            return InstallerStrings.Get("Installing Microsoft .NET 10 Desktop Runtime…");
        }

        if (string.Equals(packageId, OpenBladeSetupPackage, StringComparison.Ordinal))
        {
            return InstallerStrings.Get("Applying OpenBlade changes…");
        }

        return InstallerStrings.Get("Applying the requested changes…");
    }

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
        bool allowSameVersionMaintenance,
        bool relatedBundleInvocation,
        bool detectedSameVersionBundle,
        RelatedBundlePlanType recommendedType)
    {
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

    internal static bool ShouldRejectUninstallPlan(
        LaunchAction action,
        bool runningBundleInstalled,
        bool setupPackagePlanned) =>
        action == LaunchAction.Uninstall
        && runningBundleInstalled
        && !setupPackagePlanned;

    internal static string SelectSameVersionConflictGuidance(
        bool canOfferMaintenance,
        bool relatedBundleCached) =>
        canOfferMaintenance
            ? InstallerStrings.Get("Run this installer interactively and choose Repair or Uninstall.")
            : !relatedBundleCached
                ? InstallerStrings.Get(
                    "Its cached installer is unavailable, so setup cannot safely repair or remove it. Use Windows installer recovery before trying again.")
                : InstallerStrings.Get(
                    "Repair or remove the registered OpenBlade installation in Windows Installed apps, then try again.");

    internal static bool ShouldBlockMultipleRelatedBundles(
        LaunchAction action,
        int relatedBundleCount) =>
        relatedBundleCount > 1
        && action is LaunchAction.Install or LaunchAction.Repair;

    internal static bool ShouldBlockMultipleRelatedBundles(
        LaunchAction action,
        Display display,
        RelationType commandRelation,
        string currentVersion,
        IReadOnlyCollection<DetectedRelatedBundle> relatedBundles,
        bool localDevCompatibilityEnabled)
    {
        if (!ShouldBlockMultipleRelatedBundles(action, relatedBundles.Count))
        {
            return false;
        }

        return !localDevCompatibilityEnabled
            || !IsAllowedLocalDevCompatibilityTransition(
                action,
                display,
                commandRelation,
                currentVersion,
                relatedBundles);
    }

    internal static bool IsAllowedLocalDevCompatibilityTransition(
        LaunchAction action,
        Display display,
        RelationType commandRelation,
        string currentVersion,
        IReadOnlyCollection<DetectedRelatedBundle> relatedBundles)
    {
        if (action != LaunchAction.Install
            || display != Display.None
            || commandRelation != RelationType.None
            || relatedBundles.Count != 2
            || !TryParseCanonicalLocalDevVersion(currentVersion, out Version? current))
        {
            return false;
        }

        if (!Guid.TryParseExact(
                LocalDevCompatibilityStableBundleCode,
                "B",
                out Guid admittedStableCode))
        {
            return false;
        }

        bool stableFound = false;
        bool olderLocalDevFound = false;
        var distinctBundleCodes = new HashSet<Guid>();
        foreach (DetectedRelatedBundle relatedBundle in relatedBundles)
        {
            if (relatedBundle.RelationType != RelationType.Upgrade
                || !relatedBundle.PerMachine
                || relatedBundle.MissingFromCache
                || !Guid.TryParseExact(relatedBundle.ProductCode, "B", out Guid bundleCode)
                || !distinctBundleCodes.Add(bundleCode))
            {
                return false;
            }

            if (bundleCode == admittedStableCode)
            {
                if (stableFound
                    || !string.Equals(
                        relatedBundle.Version,
                        LocalDevCompatibilityStableBundleVersion,
                        StringComparison.Ordinal))
                {
                    return false;
                }

                stableFound = true;
                continue;
            }

            if (olderLocalDevFound
                || !TryParseCanonicalLocalDevVersion(
                    relatedBundle.Version,
                    out Version? relatedLocalDev)
                || relatedLocalDev >= current)
            {
                return false;
            }

            olderLocalDevFound = true;
        }

        // RelationType.Upgrade is emitted only for the bundle's authored OpenBlade
        // UpgradeCode. The fixed Stable bundle code additionally pins the one known
        // legacy provider registration that this LocalDev migration may reconcile.
        return stableFound && olderLocalDevFound;
    }

    private static bool TryParseCanonicalLocalDevVersion(
        string value,
        out Version? parsed)
    {
        if (!Version.TryParse(value, out parsed)
            || parsed.Major != 65535
            || parsed.Minor < 0
            || parsed.Minor > 65534
            || parsed.Build < 0
            || parsed.Build > 65534
            || parsed.Revision < 0
            || parsed.Revision > 65534
            || !string.Equals(value, parsed.ToString(4), StringComparison.Ordinal))
        {
            parsed = null;
            return false;
        }

        return true;
    }

    internal static LaunchAction SelectDetectionGateAction(
        LaunchAction action,
        Display display) =>
        display == Display.Full && action == LaunchAction.Unknown
            ? LaunchAction.Install
            : action;

    internal static bool ShouldRejectNewerVersionAction(
        bool newerVersionDetected,
        LaunchAction action) =>
        newerVersionDetected
        && action is LaunchAction.Install or LaunchAction.Repair;

    internal static bool IsDotNetDesktopRuntimeSufficient(
        string? installedVersion,
        string? minimumVersion)
    {
        Version? installed = string.IsNullOrWhiteSpace(installedVersion)
            ? null
            : ParseBurnComparableVersion(installedVersion!);
        Version? minimum = string.IsNullOrWhiteSpace(minimumVersion)
            ? null
            : ParseBurnComparableVersion(minimumVersion!);
        return installed is not null
            && minimum is not null
            && installed.Major == minimum.Major
            && installed.Minor == minimum.Minor
            && installed >= minimum;
    }

    internal static long DotNetRuntimeSufficientVariableValue(bool runtimeSufficient) =>
        runtimeSufficient ? 1L : 0L;

    internal static bool ShouldInitializeDotNetRuntimeReadiness(string packageId) =>
        string.Equals(
            packageId,
            OpenBladeDotNetRuntimePackage,
            StringComparison.Ordinal);

    internal static bool ShouldPromptForDotNetRuntime(
        LaunchAction action,
        Display display,
        bool runtimeSufficient) =>
        !runtimeSufficient
        && display == Display.Full
        && action is LaunchAction.Install or LaunchAction.Repair;

    internal static bool ShouldProceedAfterDotNetRuntimePrompt(
        bool promptRequired,
        bool consentGranted) =>
        !promptRequired || consentGranted;

    internal static int SelectRelatedBundleConflictStatus(bool multipleRegistrations) =>
        multipleRegistrations ? InstallerConfigurationCorrupt : ProductVersionConflict;

    internal static RequestState SelectPackageRequestState(
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

        return recommendedState;
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

    internal static int SelectApplyCompletionStatus(
        int burnStatus,
        LaunchAction action,
        UninstallOutcome uninstallOutcome) =>
        !Failed(burnStatus)
        && action == LaunchAction.Uninstall
        && uninstallOutcome == UninstallOutcome.Failed
            ? InstallerConfigurationCorrupt
            : burnStatus;

    internal static int NormalizeExitCode(int status) =>
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
