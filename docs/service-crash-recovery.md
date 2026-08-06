# Unexpected service-exit recovery

OpenBlade relies on Windows Service Control Manager (SCM) for process recovery.
The installer configures three bounded restart attempts after 5 seconds, 15
seconds, and 60 seconds. OpenBlade does not run a second watchdog.

## Intentional stop and unexpected exit

The service creates `%ProgramData%\OpenBlade\service-running.json` before its
first device observation. An intentional Windows service stop runs the existing
bounded hardware cleanup and then removes this marker. An in-process background
failure also attempts the same cleanup while the hardware is still reachable,
but retains the marker. A forced termination cannot run cleanup or remove the
marker. In both failure cases, the next same-boot service start enters
unexpected-exit recovery.

If intentional-stop cleanup cannot remove the marker, the next start also uses
the recovery path. This fails safe: it can skip ordinary profile application,
but it cannot mistake an uncertain stop for a normal startup.

Unexpected-exit recovery performs these operations in order:

1. Load profiles and persistent overrides without applying them.
2. Re-admit the exact model, firmware, USB identity, and command capabilities.
3. Read confirmed control state and refresh controller advisories.
4. Compare confirmed state with the active desired state. Differences remain
   latched as external changes.
5. Skip ordinary profile and laptop-behavior writes.
6. Resume only the continuously owned producers that still pass their normal
   write gates.

The run marker is recovery evidence, not hardware admission. Every recovery
write still performs fresh device and safety checks.

`StartedAtUtc` separates a same-boot process failure from a system restart. A
marker older than the current Windows boot is replaced and ordinary startup
reconciliation runs, because firmware state from the prior boot cannot prove
continued OpenBlade ownership. An unreadable marker still fails closed into
unexpected-exit recovery.

## Owned producers

| Producer | Recovery behavior |
| --- | --- |
| Keyboard Driver mode | Maintenance runs only after the first exact-device observation. Unknown status remains fail-closed for function-row input. |
| Manual fan curve | Recovery requires a retained firmware manual state and no fan drift. The service enters the device-admitted maximum target before reading a fresh temperature sample; unavailable CPU telemetry keeps that maximum. A successful target write establishes ownership for the new service process. |
| Keyboard lighting renderer | Only a previously confirmed `LastKnownLighting` value is eligible. External lighting drift blocks restoration unless the user enabled the existing override policy. |
| Persistent overrides | Overrides remain visible as desired intent, but recovery does not use them to bypass external-change latches or perform ordinary profile writes. |
| Session Agent renderers | Service reconnect notifications cancel the old Ambient Awareness or Audio Meter request and dispose its producer before a new confirmed ownership status starts another one. Reactive input and LampArray observation use the same reconnect status feed. |

The service fan and lighting loops retain their existing bounded cadence and
cancellation. A persistent renderer failure receives no unbounded retry loop.
SCM remains the only process-level retry owner.

## Automated coverage

The following tests form the crash-recovery contract:

| Requirement | Coverage |
| --- | --- |
| Durable distinction between graceful, same-boot failure, and system restart | `ServiceRunMarkerStoreTests`, `WorkerTests.UnexpectedBackgroundFailure_IsReportedToCleanup`, `WorkerTests.UnexpectedBackgroundFailure_AttemptsCleanupButRetainsRunMarker`, and `WorkerTests.RunMarker_IsClearedOnlyAfterConfirmedOrDeliberatelySkippedCleanup` |
| Observation before recovery writes | `ServiceControlRuntimeTests.UnexpectedRestart_ObservesBeforeRecoveringOwnedProducers` |
| Thermally safe manual-curve recovery without telemetry at the admitted device maximum | `ServiceControlRuntimeTests.UnexpectedRestart_ObservesBeforeRecoveringOwnedProducers` |
| External changes and persistent overrides | `ServiceControlRuntimeTests.UnexpectedRestart_LatchesExternalChangesWithoutRecoveryWrites` |
| Delayed device arrival after the bounded recovery attempt | `ServiceControlRuntimeTests.UnexpectedRestart_DeviceArrivingAfterRecoveryAttemptDoesNotStartCurve` |
| Startup sequencing and cancellation | `ServiceStartupCoordinatorTests.InitializeAsync_UnclearedRunMarkerUsesOwnedRecoverySequence` |
| Bounded SCM restart policy | `WindowsSetupPlatformServiceTests.InstallService_ConfiguresBoundedRestartRecovery` |
| Stale Ambient Awareness and Audio Meter producers | `ServiceAmbientLightingLoopTests.DisposeWaitsForConcurrentRendererShutdown` and `ServiceAudioMeterLightingLoopTests.DisposeWaitsForConcurrentRendererShutdown` |
| LampArray and reactive-input reconnect | `ServiceLampArrayObservationLoopTests.ReconnectAndExactStatusRestartObservation` and the reconnect cases in `ServiceReactiveLightingLoopTests` |

These tests simulate process loss by leaving the run marker uncleared and
constructing a new service runtime over the retained configuration and device
state. They do not send hardware commands.

## Installed-machine validation

Forced service termination is an interactive hardware test, not part of CI.
Run it only on an admitted laptop and firmware baseline, with no competing
controller active and with the operator available to restore state.

1. Confirm `sc.exe qfailure OpenBlade` reports exactly
   `restart/5000/restart/15000/restart/60000` and the service executable resolves
   under `%ProgramFiles%\OpenBlade`.
2. Save the current profile and confirmed state. Select a manual fan curve and
   one host-rendered lighting effect, then confirm both are active.
3. From an elevated shell, record the service PID and force-terminate only that
   exact `%ProgramFiles%\OpenBlade\OpenBlade.Service.exe` process.
4. Wait up to 30 seconds for SCM to start one replacement service process with
   a different PID. Do not start a second service or watchdog.
5. Use the typed status or firmware-smoke client to confirm exact-device
   verification completed, the manual curve produced a fresh target, the
   lighting renderer reconnected once, and no external-change latch was
   cleared.
6. Confirm the Session Agent reconnected and that only one frame producer is
   active. Export diagnostics and retain the recovery log entry.
7. Stop the service normally once. Confirm Automatic fan handoff completes and
   `service-running.json` is removed, then start the service and verify the
   ordinary startup path is restored.

If any identity, readback, temperature, or controller check is uncertain, stop
the test and use the existing firmware-smoke recovery procedure. Do not weaken
the gate or repeatedly kill the service.
