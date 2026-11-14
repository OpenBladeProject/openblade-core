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
        IReadOnlyList<ISetupProcess> processes = processOperations.GetProcessesByName("OpenBlade.Service");
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
                if (path is null)
                {
                    if (!process.TryRetainLifetime())
                    {
                        continue;
                    }
                    throw new InvalidOperationException("Cannot establish the service process executable identity; setup stopped before shutdown.");
                }
                if (string.Equals(Path.GetFullPath(path), installedService, StringComparison.OrdinalIgnoreCase))
                {
                    installedProcesses.Add(process);
                }
            }

            // Capture and retain identity BEFORE stop: SCM STOPPED can precede
            // process termination and release of the installed executable tree.
            SetupServiceState? state = await serviceControl.GetStateAsync(OpenBlade.Contracts.OpenBladeWindowsService.Name, deadline.Token)
                .WaitAsync(deadline.Token);
            if (state == SetupServiceState.Running)
            {
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
            throw new TimeoutException($"The installed OpenBlade service process did not exit within {timeout}; setup retained the installed files.", exception);
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
