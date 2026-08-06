using System;
using System.Diagnostics;
using System.Windows.Interop;
using System.Windows.Threading;
using WixToolset.BootstrapperApplicationApi;

namespace OpenBlade.Installer.UI;

internal sealed class OpenBladeBootstrapperApplication : BootstrapperApplication
{
    private const string ProgressPipeVariable = "OpenBladeSetupProgressPipe";
    private const string PurgeProfilesVariable = "OpenBladePurgeProfiles";
    private const string LaunchTargetVariable = "LaunchTarget";
    private const string LaunchWorkingFolderVariable = "LaunchWorkingFolder";
    private const string BundleLogVariable = "WixBundleLog";
    private const string BundleVersionVariable = "WixBundleVersion";

    private IBootstrapperCommand? command;
    private BootstrapperApplicationData? applicationData;
    private Dispatcher? dispatcher;
    private InstallerWindow? window;
    private InstallerProgressPipe? progressPipe;
    private LaunchAction plannedAction = LaunchAction.Unknown;
    private bool installed;
    private bool applying;
    private bool cancellationRequested;
    private int phaseCount = 2;
    private int cacheProgress;
    private int executeProgress;
    private int result;
    private string? lastError;

    private IEngine Engine => engine;

    public OpenBladeBootstrapperApplication()
    {
        DetectBegin += OnDetectBegin;
        DetectRelatedBundle += OnDetectRelatedBundle;
        DetectComplete += OnDetectComplete;
        PlanBegin += OnPlanBegin;
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
        ApplyComplete += OnApplyComplete;
    }

    public void StartAction(LaunchAction action, bool purgeProfiles)
    {
        if (applying || command is null)
        {
            return;
        }

        plannedAction = action;
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
        if (command is null)
        {
            Engine.Quit(unchecked((int)0x80004005));
            return;
        }

        dispatcher = Dispatcher.CurrentDispatcher;
        string version = TryGetVariable(BundleVersionVariable) ?? "unknown";
        if (command.Display is Display.Full or Display.Passive)
        {
            window = new InstallerWindow(this, version);
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
                Ui(() => window?.ShowFailure(
                    "Run OpenBlade-Setup.exe normally to install, repair, or remove OpenBlade.",
                    TryGetVariable(BundleLogVariable)));
            }
            else
            {
                dispatcher?.BeginInvokeShutdown(DispatcherPriority.Normal);
            }

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

    private void OnPlanPackageBegin(object? sender, PlanPackageBeginEventArgs e)
    {
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
        IntPtr parent = window is null ? IntPtr.Zero : new WindowInteropHelper(window).Handle;
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
        Ui(() => window?.SetProgress(e.OverallPercentage));
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
