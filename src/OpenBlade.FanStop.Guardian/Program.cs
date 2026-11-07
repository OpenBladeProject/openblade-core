using System.Diagnostics;
using System.Globalization;
using System.IO.Pipes;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Channels;
using Microsoft.Win32.SafeHandles;
using OpenBlade.Contracts;
using OpenBlade.FanStop;
using OpenBlade.Service;

namespace OpenBlade.FanStop.Guardian;

internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        try
        {
            FanStopGuardianTransport.RequireSystem();
            string executable = Environment.ProcessPath ?? throw new IOException("Missing guardian apphost.");
            if (!Path.GetFileName(executable).Equals("OpenBlade.FanStop.Guardian.exe", StringComparison.OrdinalIgnoreCase))
            { return 2; }
            FanStopGuardianTransport.VerifyProtectedPath(executable, directory: false);
            FanStopGuardianTransport.VerifyProtectedPath(AppContext.BaseDirectory, directory: true);
            if (args.Length is not (4 or 10) || !Guid.TryParseExact(args[2], "N", out Guid operation)
                || operation == Guid.Empty || !args[1].StartsWith("OpenBlade.FanStop.", StringComparison.Ordinal)
                || args[1].Length > 128 || !Path.IsPathFullyQualified(args[3])) { return 2; }
            var store = new FanStopGuardianStore(Path.GetDirectoryName(args[3])!);
            if (!string.Equals(store.DirectoryPath, args[3], StringComparison.OrdinalIgnoreCase)) { return 2; }
            var reservation = store.ReadReservation(operation);
            using Process service = FanStopGuardianTransport.RetainPeer(reservation.ServicePid,
                Path.Combine(AppContext.BaseDirectory, "OpenBlade.Service.exe"), reservation.ServiceStartUtcTicks);
            if (args is ["--proxy", _, _, _])
            { return await RunProxyAsync(args, executable, service); }
            if (args is ["--owner", _, _, _, "--control", _, "--job", _, "--caller", _]
                && long.TryParse(args[7], NumberStyles.None, CultureInfo.InvariantCulture, out long job) && job > 0
                && long.TryParse(args[9], NumberStyles.None, CultureInfo.InvariantCulture, out long caller) && caller > 0)
            {
                using var jobHandle = new SafeFileHandle((nint)job, ownsHandle: true);
                using var callerHandle = new SafeProcessHandle((nint)caller, ownsHandle: true);
                _ = So6120GuardianProcess.VerifyOwnerIsolation(jobHandle, callerHandle);
                jobHandle.Dispose(); callerHandle.Dispose();
                using var control = new AnonymousPipeClientStream(PipeDirection.In, args[5]);
                return await RunOwnerAsync(args[1], operation, store, reservation, service, control);
            }
            return 2;
        }
        catch { return 3; } // Durable service reservation still fences startup.
    }

    private static async Task<int> RunProxyAsync(string[] args, string executable, Process service)
    {
        using var control = new AnonymousPipeServerStream(PipeDirection.Out, HandleInheritability.Inheritable);
        using var lifetime = So6120GuardianCallerLifetime.CreateForCurrentProcess();
        So6120GuardianChild launched;
        try
        {
            launched = So6120GuardianProcess.Start(executable,
                ["--owner", args[1], args[2], args[3], "--control", control.GetClientHandleAsString(),
                    "--job", lifetime.JobQueryHandle.DangerousGetHandle().ToString(CultureInfo.InvariantCulture),
                    "--caller", lifetime.CallerQueryHandle.DangerousGetHandle().ToString(CultureInfo.InvariantCulture)],
                control.ClientSafePipeHandle, showConsole: false, lifetime);
        }
        finally { control.DisposeLocalCopyOfClientHandle(); lifetime.Dispose(); }
        using var owner = launched;
        using var stop = new CancellationTokenSource();
        var pump = Task.Run(async () =>
        {
            try
            {
                while (!stop.IsCancellationRequested)
                {
                    await control.WriteAsync(new byte[] { service.HasExited ? (byte)2 : (byte)1 }, stop.Token);
                    await control.FlushAsync(stop.Token);
                    await Task.Delay(500, stop.Token);
                }
            }
            catch (Exception) { control.Dispose(); }
        });
        try
        {
            using var original = Process.GetProcessById(owner.Id);
            using var report = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await FanStopGuardianTransport.WriteAsync(Console.OpenStandardOutput(),
                new FanStopGuardianOwnerIdentity(owner.Id, original.StartTime.ToUniversalTime().Ticks), report.Token);
            return await owner.WaitForExitAsync();
        }
        finally
        {
            await stop.CancelAsync();
            control.Dispose();
            try { await pump; } catch (Exception) { }
        }
    }

    private static async Task<int> RunOwnerAsync(string pipeName, Guid operation, FanStopGuardianStore store,
        FanStopGuardianReservation reservation, Process service, Stream proxyControl)
    {
        await using var serviceSignals = new So6120GuardianSignals(startupLease: TimeSpan.FromSeconds(10));
        await using var proxySignals = new So6120GuardianSignals(startupLease: TimeSpan.FromSeconds(10));
        using var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        using var startup = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        try
        {
            await pipe.ConnectAsync(startup.Token);
            if (FanStopGuardianTransport.PeerPid(pipe, server: false) != reservation.ServicePid || service.HasExited)
            { throw new InvalidDataException("Guardian startup peer did not match the reserved service."); }
        }
        catch (Exception error)
        {
            var refusal = new FanStopGuardianSnapshot(operation, 1, FanStopGuardianPhase.Refused,
                NativeReleased: true, ConfirmedState: FanStopObservedState.Ordinary,
                Reason: $"Guardian startup refused before acquisition ({error.GetType().Name}).")
            { KnownNotSubmitted = true };
            store.RecordOwnerResult(refusal);
            return 0;
        }
        using var pumps = new CancellationTokenSource();
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(serviceSignals.StopToken, proxySignals.StopToken);
        var statuses = Channel.CreateBounded<FanStopGuardianSnapshot>(new BoundedChannelOptions(1)
        { SingleReader = true, SingleWriter = true, FullMode = BoundedChannelFullMode.DropOldest });
        Task writer = Task.Run(async () =>
        {
            try
            {
                await foreach (var status in statuses.Reader.ReadAllAsync(pumps.Token))
                {
                    using var deadline = CancellationTokenSource.CreateLinkedTokenSource(pumps.Token);
                    deadline.CancelAfter(TimeSpan.FromSeconds(2));
                    await FanStopGuardianTransport.WriteAsync(pipe, status, deadline.Token);
                }
            }
            catch (Exception) { serviceSignals.CallerDisconnected(); }
        });
        Task serviceReader = Task.Run(async () =>
        {
            try
            {
                while (!pumps.IsCancellationRequested)
                {
                    var command = await FanStopGuardianTransport.ReadAsync<FanStopGuardianCommand>(pipe, pumps.Token);
                    if (command.OperationId != operation || !Enum.IsDefined(command.Kind)) { break; }
                    if (!serviceSignals.Receive(command.Kind == FanStopGuardianCommandKind.Heartbeat && command.ContinueStop
                            ? (byte)1 : (byte)2)) { return; }
                }
            }
            catch (Exception) { }
            serviceSignals.CallerDisconnected();
        });
        Task proxyReader = Task.Run(async () =>
        {
            try
            {
                byte[] value = new byte[1];
                while (await proxyControl.ReadAsync(value, pumps.Token) == 1)
                { if (!proxySignals.Receive(value[0])) { return; } }
            }
            catch (Exception) { }
            proxySignals.CallerDisconnected();
        });
        try
        {
            using var handshake = CancellationTokenSource.CreateLinkedTokenSource(startup.Token, stop.Token);
            try { await Task.WhenAll(serviceSignals.FirstHeartbeat, proxySignals.FirstHeartbeat).WaitAsync(handshake.Token); }
            catch (OperationCanceledException) { await stop.CancelAsync(); }
            // All hardware stays on this single worker. Control readers and the
            // lease clocks remain independent of synchronous native calls.
            int result = await Task.Run(() => ExecuteOwner(operation, store, reservation, service, stop.Token,
                status => statuses.Writer.TryWrite(status)));
            statuses.Writer.TryComplete();
            await writer; // Terminal status must be sent before owner exit.
            return result;
        }
        finally
        {
            await pumps.CancelAsync();
            pipe.Dispose(); proxyControl.Dispose();
            try { await Task.WhenAll(serviceReader, proxyReader, writer); } catch (Exception) { }
        }
    }

    private static int ExecuteOwner(Guid operation, FanStopGuardianStore store, FanStopGuardianReservation reservation,
        Process service, CancellationToken stop, Action<FanStopGuardianSnapshot> publish)
    {
        long revision = 0;
        bool stopped = false;
        bool stoppedObserved = false;
        FanStopEndReason endReason = FanStopEndReason.None;
        So6120DirectDutySession? session = null;
        So6120SessionCommandEvidence? retainedCommandEvidence = null;
        So6120ServiceDutyPlatform? acquisition = null;
        FanStopGuardianSnapshot Status(FanStopGuardianPhase phase, string? reason = null) =>
            new(operation, ++revision, phase, StopConfirmed: stopped, Reason: reason) { EndReason = endReason };
        void PublishTerminal(FanStopGuardianSnapshot result)
        {
            store.RecordOwnerResult(result);
            publish(result);
        }
        try
        {
            stop.ThrowIfCancellationRequested();
            if (!FanStopSettings.IsWellFormed(reservation.Settings))
            { throw new InvalidDataException("Guardian reservation has an invalid Stop policy."); }
            publish(Status(FanStopGuardianPhase.Starting) with { CommandPending = true });
            string hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
                "SO6120 EC1.04 RZ09-0581 reviewed direct zero/Auto contract")));
            const string firmwareHash = "8537E9FD55B7B35504D8D87B0191ACC4391B67365B3FE72F6E1029A424C9E707";
            var identity = new So6120JournalIdentity(new(operation, "RZ09-0581/1532:02E0", "SO6120-EC1.04", Guid.NewGuid()),
                PerformancePreset.Balanced, 0, 0, hash, firmwareHash, operation, hash,
                $"service:{reservation.ServicePid}:{reservation.ServiceStartUtcTicks}");
            acquisition = So6120ServiceDutyPlatform.Acquire(identity, reservation.ServicePid,
                reservation.ServiceStartUtcTicks, () => store.ReadReservation(operation) == reservation,
                So6120ServiceDutyPlatform.CreateTemperatureSource(), reservation.Settings);
            stop.ThrowIfCancellationRequested();
            PowerSource power = acquisition.ReadPowerSource();
            if (power != PowerSource.UsbC)
            { throw new InvalidDataException("Stop entry requires a resolved USB-C power source."); }
            session = So6120DirectDutySession.CreateForOwnedStop(store.RunRoot(operation), acquisition.Binding,
                acquisition.Port, acquisition.EvidenceSource, TimeProvider.System, acquisition.AdmissionPolicy,
                new(() => !stop.IsCancellationRequested && acquisition.RefreshEntryAdmission(),
                    () => !stop.IsCancellationRequested && acquisition.CachedEntryAdmission(),
                    () => !stop.IsCancellationRequested && acquisition.ContinueStopAdmission()));
            So6120DutyResult entry = session.EnterStopped(stop);
            stopped = entry.Success && entry.DirectEntryConfirmed;
            if (entry.CommandsFenced || entry.CleanupCheckpoint is null)
            { throw new InvalidDataException($"Stop entry did not establish an owned cleanup checkpoint ({entry.Failure})."); }
            if (stopped)
            {
                acquisition.BeginOwnedStop();
                So6120StopCoastResult coast = session.ObserveStoppedCoast(stop);
                if (coast == So6120StopCoastResult.Unresolved)
                { throw new InvalidDataException("Stopped-fan coast observation is unresolved."); }
                if (coast == So6120StopCoastResult.Stopped)
                {
                    stoppedObserved = true;
                    publish(Status(FanStopGuardianPhase.Stopped) with
                    {
                        ConfirmedState = FanStopObservedState.Stopped,
                        ObservedPowerSource = power,
                    });
                    long lastPowerRead = TimeProvider.System.GetTimestamp();
                    while (!stop.IsCancellationRequested && !service.HasExited && session.ContinueStopAdmitted())
                    {
                        if (TimeProvider.System.GetElapsedTime(lastPowerRead) >= TimeSpan.FromSeconds(2))
                        {
                            PowerSource next = acquisition.ReadPowerSource();
                            lastPowerRead = TimeProvider.System.GetTimestamp();
                            FanStopPowerTransition transition = FanStopPowerTransitionPolicy.Evaluate(power, next);
                            if (transition == FanStopPowerTransition.Automatic)
                            { endReason = FanStopEndReason.PowerChanged; break; }
                            if (transition == FanStopPowerTransition.VerifyOwnedStop)
                            {
                                // A lower-power transition retains the existing
                                // override only after a complete fresh control read.
                                var verification = session.VerifyOwnedStop();
                                if (verification.Disposition != So6120DutyIoDisposition.Confirmed)
                                { throw new InvalidDataException("Power change altered owned fan state."); }
                                if (verification.Observation?.Snapshot is not { Tach1: 0, Tach2: 0 })
                                { stoppedObserved = false; break; }
                                power = next;
                                publish(Status(FanStopGuardianPhase.Stopped) with
                                {
                                    ConfirmedState = FanStopObservedState.Stopped,
                                    ObservedPowerSource = power,
                                });
                            }
                        }
                        stop.WaitHandle.WaitOne(TimeSpan.FromMilliseconds(500));
                    }
                }
            }
            if (stop.IsCancellationRequested || service.HasExited) { endReason = FanStopEndReason.CallerRequest; }
            else if (endReason == FanStopEndReason.None)
            { endReason = acquisition.EndReason == FanStopEndReason.None ? FanStopEndReason.SafetyCondition : acquisition.EndReason; }
            publish(Status(FanStopGuardianPhase.ReturningToAutomatic, "Auto requested or continuation admission ended.")
                with
            { CommandPending = true, ConfirmedState = stoppedObserved ? FanStopObservedState.Stopped : FanStopObservedState.Unknown });
            So6120DutyResult automatic = session.ReturnToAutomatic();
            if (!automatic.Success || !automatic.NormalModeObserved || !automatic.CleanupConfirmed || automatic.CommandsFenced)
            { throw new InvalidDataException($"Auto handback is unresolved ({automatic.Failure})."); }
            retainedCommandEvidence = session.CommandEvidence;
            session.Dispose(); session = null;
            acquisition.Dispose();
            if (acquisition.CloseEvidence.State != So6120NativeCloseState.Closed)
            { throw new IOException("Native guardian release is unconfirmed."); }
            // Native close and journal completion are independent proofs.
            var inspection = So6120DirectDutyJournal.Inspect(store.RunRoot(operation));
            if (!inspection.Complete || inspection.Corrupt || inspection.Fenced || inspection.UnresolvedWork)
            { throw new InvalidDataException("Guardian journal is not complete."); }
            PublishTerminal(Status(FanStopGuardianPhase.Completed) with
            {
                ConfirmedState = FanStopObservedState.Automatic,
                AutoConfirmed = true,
                CleanupConfirmed = true,
                JournalComplete = true,
                NativeReleased = true
            });
            return 0;
        }
        catch (Exception error)
        {
            So6120SessionCommandEvidence? commands = session?.CommandEvidence ?? retainedCommandEvidence;
            if (error is not So6120ServiceNativeReleaseException
                && acquisition?.NativeOperationUnresolved != true
                && (commands is null || commands.CleanNoMutation))
            {
                try
                {
                    session?.Dispose(); session = null;
                    acquisition?.Dispose();
                    if (acquisition is null || acquisition.CloseEvidence.State == So6120NativeCloseState.Closed)
                    {
                        string detail = $"Stop admission refused; no EC mutation and no pending native command. "
                            + $"Resolved EC commands: {commands?.CommandsCompleted ?? 0}. ({error.GetType().Name}): {error.Message}";
                        PublishTerminal(Status(FanStopGuardianPhase.Refused, detail[..Math.Min(detail.Length, 1024)]) with
                        {
                            KnownNotSubmitted = true,
                            ConfirmedState = FanStopObservedState.Ordinary,
                            NativeReleased = true,
                        });
                        return 0;
                    }
                }
                catch (Exception) { }
            }
            string failure = $"Owned fan session needs read-only reconciliation ({error.GetType().Name}): {error.Message}";
            publish(Status(FanStopGuardianPhase.RecoveryRequired) with
            {
                Unresolved = true,
                Failure = failure[..Math.Min(failure.Length, 1024)]
            });
            // No alternate writer, replay or speculative Auto. Retain the native
            // owner and all acquisition resources when resolution is unknown.
            Thread.Sleep(Timeout.Infinite);
            GC.KeepAlive(session); GC.KeepAlive(acquisition);
            return 3;
        }
    }

}
