namespace OpenBlade.Setup;

internal static class SetupServiceQuiescence
{
    internal static async Task StopAsync(
        ISetupServiceControl serviceControl,
        ISetupProcessOperations processOperations,
        string installDirectory,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(timeout, TimeSpan.Zero);
        cancellationToken.ThrowIfCancellationRequested();
        using CancellationTokenSource deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(timeout);
        List<ISetupProcess> processes = [.. processOperations.GetProcessesByName("OpenBlade.Service")];
        try
        {
            string installedService = Path.GetFullPath(Path.Combine(installDirectory, "OpenBlade.Service.exe"));
            List<ISetupProcess> installedProcesses = [];
            foreach (ISetupProcess process in processes)
            {
                deadline.Token.ThrowIfCancellationRequested();
                if (!process.TryRetainLifetime())
                {
                    continue;
                }

                string? path;
                try
                {
                    path = process.ExecutablePath;
                }
                catch (Exception exception) when (
                    exception is InvalidOperationException or System.ComponentModel.Win32Exception
                    && !process.TryRetainLifetime())
                {
                    continue; // Exit raced identity inspection of the retained object.
                }
                if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path))
                {
                    if (!process.TryRetainLifetime())
                    {
                        continue;
                    }
                    throw new InvalidOperationException("Cannot establish the service process executable identity; setup stopped before shutdown.");
                }
                if (TrayProcessIdentity.IsExactInstalledExecutable(path, installedService))
                {
                    installedProcesses.Add(process);
                }
            }

            // Capture and retain identity BEFORE stop: SCM STOPPED can precede
            // process termination and release of the installed executable tree.
            SetupServiceStatus? status = await serviceControl.GetStatusAsync(OpenBlade.Contracts.OpenBladeWindowsService.Name, deadline.Token)
                .WaitAsync(deadline.Token);
            if (status?.State == SetupServiceState.Running)
            {
                if (status.ProcessId is not > 0)
                {
                    throw new InvalidOperationException("Cannot establish the running service process identity; setup stopped before shutdown.");
                }
                ISetupProcess? serviceProcess = processes.FirstOrDefault(process => process.Id == status.ProcessId.Value);
                if (serviceProcess is null)
                {
                    // The service may have started after name enumeration. SCM's
                    // PID must be bound before stop even if that list was empty.
                    serviceProcess = processOperations.GetProcessById(status.ProcessId.Value);
                    processes.Add(serviceProcess);
                }
                if (serviceProcess.Id != status.ProcessId.Value || !serviceProcess.TryRetainLifetime()
                    || !TrayProcessIdentity.IsExactInstalledExecutable(serviceProcess.ExecutablePath, installedService))
                {
                    throw new InvalidOperationException("The running service executable identity could not be confirmed; setup stopped before shutdown.");
                }
                if (!installedProcesses.Contains(serviceProcess)) { installedProcesses.Add(serviceProcess); }

                SetupServiceStatus? current = await serviceControl.GetStatusAsync(OpenBlade.Contracts.OpenBladeWindowsService.Name, deadline.Token)
                    .WaitAsync(deadline.Token);
                if (current != status || !serviceProcess.TryRetainLifetime())
                {
                    throw new InvalidOperationException("The service process identity changed before shutdown; setup retained the installed files.");
                }
                await serviceControl.RunAsync(["stop", OpenBlade.Contracts.OpenBladeWindowsService.Name], [1060, 1062], deadline.Token)
                    .WaitAsync(deadline.Token);
                await serviceControl.WaitForStateAsync(OpenBlade.Contracts.OpenBladeWindowsService.Name,
                    SetupServiceState.Stopped, timeout, deadline.Token).WaitAsync(deadline.Token);
            }

            // Also cover STOPPED or absent registrations with a lingering process.
            foreach (ISetupProcess process in installedProcesses)
            {
                await process.WaitForExitAsync(deadline.Token).WaitAsync(deadline.Token);
            }
            deadline.Token.ThrowIfCancellationRequested();
        }
        catch (OperationCanceledException exception) when (!cancellationToken.IsCancellationRequested && deadline.IsCancellationRequested)
        {
            throw new TimeoutException($"Setup could not confirm service process exit within {timeout}; setup retained the installed files.", exception);
        }
        finally
        {
            foreach (ISetupProcess process in processes)
            {
                process.Dispose();
            }
        }
    }
}
