using OpenBlade.Setup;

#if OPENBLADE_LOCAL_DEV_DEPLOYMENT
LocalDevRelatedBundleCompatibility.PrepareCachedSetupEnvironment();
#endif
SetupPaths paths = SetupPaths.CreateForCurrentIdentity();
string executablePath = Environment.ProcessPath
    ?? throw new InvalidOperationException("The OpenBlade setup executable path is unavailable.");
bool interactiveRequested = SetupCommandParser.IsInteractiveUninstall(args);
bool interactiveRelay = InstalledUninstallBootstrapper.ConsumeInteractiveRelayMarker();
IReadOnlyList<string> effectiveArguments = args;

if (interactiveRequested)
{
    UninstallUserInterface.HideOwnedConsoleWindow();
    if (!UninstallUserInterface.ConfirmPreservingProfiles())
    {
        return 0;
    }

    effectiveArguments = ["uninstall"];
}

if (InstalledUninstallBootstrapper.IsInstalledUninstallInvocation(effectiveArguments, executablePath, paths))
{
    InstalledUninstallBootstrapper.RelaunchFromSecureRelay(
        effectiveArguments,
        executablePath,
        paths,
        interactiveRequested);
    return 0;
}

bool isUninstallRelay = InstalledUninstallBootstrapper.IsRelayExecutable(executablePath, paths);
try
{
    if (isUninstallRelay)
    {
        await InstalledUninstallBootstrapper.WaitForInstalledParentExitAsync(CancellationToken.None);
    }

    _ = SetupCommandParser.TryParseInvocation(
        effectiveArguments,
        out SetupInvocation invocation);
#if OPENBLADE_LOCAL_DEV_DEPLOYMENT
    string enrolledUserSid = LocalDevEnrollmentUser.ReadEnrolledAccountSid();
#else
    string? enrolledUserSid = null;
#endif
    using ISetupProgressReporter progress = SetupProgressReporter.Create(
        invocation.ProgressPipeName);
    int exitCode = await new SetupCommandRunner(
        new WindowsSetupPlatform(new WindowsSetupPlatformOptions(
            Paths: paths,
            ProgressReporter: progress,
            BundleOwnerId: invocation.BundleOwnerId,
            CurrentUserSid: enrolledUserSid)),
        progress)
        .RunAsync(effectiveArguments, CancellationToken.None);
    if ((interactiveRequested || interactiveRelay) && exitCode == 0)
    {
        UninstallUserInterface.ShowSuccess();
    }

    return exitCode;
}
catch (Exception exception)
{
    if (interactiveRequested || interactiveRelay)
    {
        UninstallUserInterface.ShowFailure(exception.Message);
    }
    else
    {
        Console.Error.WriteLine($"OpenBlade setup failed: {exception.Message}");
    }
    return 1;
}
finally
{
    if (isUninstallRelay)
    {
        InstalledUninstallBootstrapper.ScheduleRelayCleanup(executablePath, paths);
    }
}
