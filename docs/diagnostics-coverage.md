# Diagnostic coverage

Support exports combine the service's device, capability, configuration, command,
firmware and ownership snapshots with the current user's runtime logs, SnapTap
status, THX observations and a collection manifest.

## Failure records

A caught failure retains its component and operation, discovery or execution
stage, reason, exception type, HRESULT, Win32 error and process exit code when
available. Exception messages, thrown stack locations, all inner exceptions and
aggregate branches accompany the record. Account paths, account/machine names
and credential assignments are redacted. Exception Data, keyboard contents,
screen/audio contents and credentials are not diagnostic payloads.

The shared recorder is `DiagnosticFailure.FromException`.
`DiagnosticTrace.TraceException` writes the detailed record at catch boundaries;
`DiagnosticTrace.FormatException` feeds existing logger and installer sinks.
Existing operation results, admission decisions and recovery behavior remain
authoritative. A diagnostic never grants hardware access or retries.

Each user-mode log line identifies its component version, PID and process-run
identifier. SnapTap terminal status is accepted only for the matching process
and fresh attempt. Its record preserves the failed driver package and check,
including the common driver and exact model's keyboard filter when both fail.

## Recording paths

| Component | Retained evidence |
| --- | --- |
| Service and Protocol | Owned ILogger exceptions and Trace events enter diagnostic history; command, transition and firmware observations remain in their existing typed snapshots. |
| Session Agent | Persistent runtime log plus supervised SnapTap terminal status, display/audio/input-observer/lighting/automation and current-user IPC failures. |
| Tray and Tray.Core | Persistent runtime and crash logs, presentation/IPC/persistence/update/export failures, SnapTap status and optional THX diagnostic capture. |
| Platform and Display | Driver binding, INF/service/driver-store/catalog validation, native access and display mutation failures flow through their hosting process. |
| Setup | The existing installer log retains exception trees and stack locations alongside its step journal. |
| Local-development broker | Its existing retained logger receives structured exception detail and Trace events. This developer component is separate from installed-product runtime exports. |

## Collection and retention

User-mode logs retain the current file and three preceding generations, up to
1 MiB each per component. Normal exports collect up to 128 log files and
64 MiB total. An oversized external/older log contributes its newest 1 MiB
instead of losing the complete file. The manifest records its original and
included sizes and marks it partial.

Failure trees allow 16 levels and 64 nodes, with a total text budget of 128 KiB.
An individual message allows 8 KiB and a stack allows 32 KiB. Truncation is
explicit in `DetailsTruncated`; related branches preserve aggregate failures.
These limits bound damaged or hostile records while retaining ordinary complete
exceptions.

`diagnostic-export.json` records included, missing, failed and partial sections
and per-log collection results. `diagnostic-inventory.json` lists collected
payload entries and sizes, the recorder version and retention limits.
The cross-process log-writer-health.json entry retains logger initialization,
append and truncation failures, their source version and process, and missing
required logging sources. Health survives a healthy helper restart. Its entry
list describes the payload before the inventory and final manifest are added.
An unavailable supplementary collector does not discard healthy evidence.
Exact-identity exports continue to preserve their original bytes.

The service retains up to 2,048 entries and 4 MiB of text, with retention,
eviction and truncation counters in the ZIP's service-history.json. Diagnostic
ZIP responses have a dedicated 16 MiB archive / 24 MiB framed-response limit;
ordinary requests, status and subscriptions retain their 64 KiB limit. This is
IPC protocol 72 and requires a coordinated product update.

## Reviewed boundaries

The audit reviewed 741 catch sites in Service, Protocol, Display, Setup and the
development broker, plus 576 in the user-mode projects and 20 in Platform.
It adds detailed records at approximately 300 previously lossy boundaries.
Service and user-mode native-result checks also retain WMI status, PDH/NVML
status changes and recovery, SCM exit codes, and failed display readback.
Regression coverage verifies the recorder, each hosting sink and ZIP delivery.

## Intentional exclusions

Expected cancellation, normal pipe disconnects, process disappearance during
enumeration, unsupported optional API probes and cleanup-and-rethrow handlers
do not need duplicate warning records when their caller already retains the
result or exception. Diagnostic-sink failures cannot replace the operation's
result or trigger hardware recovery. Input processing records terminal failure
outside the input loop; it does not write an event for every key.

Developer capture tools and physical-validation artifacts remain separate from
ordinary support exports. Source-backed support, setter acknowledgements and
simulated tests do not establish physical effectiveness or successful recovery.

## Regression checks

Tests exercise complete and aggregate exception chains, thrown stack locations,
credential/account redaction, explicit truncation, native causes, package
validation rejection, every reviewed SnapTap profile, helper-status round trips,
service logger-to-export delivery, unreadable-log isolation, oversized-log tails
and ZIP inventory. Coverage is a reviewed boundary inventory, not a claim that
future failure sites cannot require additional instrumentation.
