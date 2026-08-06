using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Windows.Threading;
using WixToolset.BootstrapperApplicationApi;

namespace OpenBlade.Installer.UI;

internal sealed class OpenBladeBootstrapperApplication : BootstrapperApplication
{
    private const string ProgressPipeVariable = "OpenBladeSetupProgressPipe";
    private const string PurgeProfilesVariable = "OpenBladePurgeProfiles";
    private const string InstalledVersionVariable = "OpenBladeInstalledVersion";
    private const string LaunchTargetVariable = "LaunchTarget";
    private const string LaunchWorkingFolderVariable = "LaunchWorkingFolder";
    private const string BundleLogVariable = "WixBundleLog";
    private const string BundleVersionVariable = "WixBundleVersion";
    private const string OpenBladeSetupPackage = "OpenBladeSetup";
    private const int NewerVersionInstalled = unchecked((int)0x80070666);

    private IBootstrapperCommand? command;
    private BootstrapperApplicationData? applicationData;
    private Dispatcher? dispatcher;
    private InstallerWindow? window;
    private InstallerProgressPipe? progressPipe;
    private LaunchAction plannedAction = LaunchAction.Unknown;
    private bool installed;
    private bool applying;
    private bool cancellationRequested;
    private bool allowNewerVersionOverwrite;
    private int phaseCount = 2;
    private int cacheProgress;
    private int executeProgress;
    private int result;
    private string? lastError;
    private string bundleVersion = "unknown";
    private string? newerRelatedBundleVersion;
    private readonly HashSet<string> newerRelatedBundleCodes =
        new(StringComparer.OrdinalIgnoreCase);

    private IEngine Engine => engine;

    public OpenBladeBootstrapperApplication()
    {
        DetectBegin += OnDetectBegin;
        DetectRelatedBundle += OnDetectRelatedBundle;
        DetectComplete += OnDetectComplete;
        PlanBegin += OnPlanBegin;
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

        if (newerRelatedBundleVersion is not null
            && action is LaunchAction.Install or LaunchAction.Repair
            && (!overwriteNewerVersion || command.Display != Display.Full))
        {
            RequireNewerVersionOverwriteConfirmation();
            return;
        }

        plannedAction = action;
        allowNewerVersionOverwrite = overwriteNewerVersion
            && newerRelatedBundleVersion is not null
            && command.Display == Display.Full;
        cancellationRequested = false;
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
        Engine.Plan(action, BundleScope.Default);
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
            string target = Engine.GetVariableString(LaunchTargetVariable);
            string workingFolder = Engine.GetVariableString(LaunchWorkingFolderVariable);
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
        progressPipe?.Dispose();
        Engine.Quit(NormalizeExitCode(result));
    }

    private void OnDetectBegin(object? sender, DetectBeginEventArgs e)
    {
        installed = e.RegistrationType == RegistrationType.Full;
        allowNewerVersionOverwrite = false;
        newerRelatedBundleCodes.Clear();
        newerRelatedBundleVersion = null;
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
            && IsNewerVersion(bundleVersion, e.Version))
        {
            newerRelatedBundleCodes.Add(e.ProductCode);
            if (newerRelatedBundleVersion is null
                || IsNewerVersion(newerRelatedBundleVersion, e.Version))
            {
                newerRelatedBundleVersion = e.Version;
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

        if (newerRelatedBundleVersion is not null
            && command.Action is LaunchAction.Install or LaunchAction.Repair)
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

    private void OnPlanRelatedBundleType(object? sender, PlanRelatedBundleTypeEventArgs e)
    {
        bool detectedNewerBundle = newerRelatedBundleCodes.Contains(e.BundleCode);
        RelatedBundlePlanType selected = SelectRelatedBundlePlanType(
            allowNewerVersionOverwrite,
            detectedNewerBundle,
            e.RecommendedType);
        if (selected != e.Type)
        {
            e.Type = selected;
            AddDetail($"Replacing newer OpenBlade bundle {e.BundleCode} after user confirmation.");
        }

        e.Cancel = cancellationRequested;
    }

    private void OnPlanPackageBegin(object? sender, PlanPackageBeginEventArgs e)
    {
        RequestState selected = SelectPackageRequestState(
            allowNewerVersionOverwrite,
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
        IntPtr parent = window?.WindowHandle ?? IntPtr.Zero;
        AddDetail("Applying the requested changes.");
        Engine.Apply(parent);
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
        AddDetail($"Running {package}.");
        Ui(() => window?.SetProgress(CalculatedProgress(), package));
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
            Ui(() => window?.ShowSuccess(plannedAction));
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
        if (command?.Display == Display.Full)
        {
            Ui(() => window?.ShowNewerVersionConfirmation(installedVersion, bundleVersion));
        }
        else
        {
            CompleteWithFailure(
                NewerVersionInstalled,
                $"OpenBlade {installedVersion} is already installed. "
                + "Run this installer interactively to confirm overwriting it.");
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
        if (applicationData?.Bundle.Packages.TryGetValue(packageId, out IPackageInfo? package) == true)
        {
            return string.IsNullOrWhiteSpace(package.DisplayName) ? packageId : package.DisplayName;
        }

        return packageId;
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

    private static bool Failed(int status) => status < 0;

    internal static bool IsNewerVersion(string currentVersion, string relatedVersion)
    {
        Version? current = ParseBurnComparableVersion(currentVersion);
        Version? related = ParseBurnComparableVersion(relatedVersion);
        return current is not null && related is not null && related > current;
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
        bool allowOverwrite,
        bool detectedNewerBundle,
        RelatedBundlePlanType recommendedType) =>
        allowOverwrite
            && detectedNewerBundle
            && recommendedType == RelatedBundlePlanType.Downgrade
                ? RelatedBundlePlanType.Upgrade
                : recommendedType;

    internal static RequestState SelectPackageRequestState(
        bool allowOverwrite,
        LaunchAction action,
        string packageId,
        RequestState recommendedState) =>
        allowOverwrite
            && action == LaunchAction.Install
            && string.Equals(packageId, OpenBladeSetupPackage, StringComparison.Ordinal)
                ? RequestState.ForcePresent
                : recommendedState;

    internal static int SelectApplyDowngradeStatus(bool allowOverwrite, int status) =>
        allowOverwrite ? 0 : status;

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
