# CPU voltage validation session

The developer-only session host accepts at most three explicit CPU observations and, only after a
Stored response with a writable typed live backend, one reversible -1/0 cycle.
It runs in the enrolled user's active local Windows session. Each job uses a
fresh instance of the existing owned-driver supervisor, with its 600-second
budget, fresh hardware admission, exact restoration, Service shutdown and owned
driver cleanup. The cycle has at most twelve forward and twelve reverse setters.

Prepare the matching committed validation package offline, install its matching
Service through the existing authorized deployment path, and review its source,
manifest hash and version. A newly published package is user-writable and cannot
be launched directly as the host. During one attended administrative setup, copy
and verify that exact package into an administrator/System-owned private closure
with protected ACLs, as required by the existing owned-driver launcher. This
host does not stage packages, install software, request elevation, or accept
commands to replace its enrolled package. Existing older packages lack this host.

When present, start the host from that protected package in an elevated local
console, using the reviewed values:

```powershell
& '<protected closure>\OpenBlade.Capture.exe' cpu-voltage-session-host `
  --payload '<protected closure>' --source '<full revision>' `
  --manifest-sha256 '<uppercase SHA-256>' `
  --expected-service-version '<version+voltage-validation.8hex>' `
  --confirm-target RZ09-0528-CPU-SESSION-THREE-OBSERVATIONS-ONE-CYCLE-READONLY-CLEANUP
```

The host prints `CpuVoltageSession=<32-character GUID>`. Enrollment lasts at
most eight hours and is bound to this package, user and Windows session. Keep
the laptop awake, connected to AC and the same console unlocked. Lock, sleep,
session changes or power loss close admission. No unattended UAC is attempted.
A changed package requires a new attended host launch.
Only one host can hold the fixed protected lease. A prior session without a
matching resolved closure blocks enrollment even if its process has exited;
that situation requires explicit reconciliation.

Use a normal-user copy of the matching Capture client, such as the build output;
the client does not need access to the protected payload. The administrator-owned
receipt under `C:\ProgramData\OpenBladeCpuVoltageSessions` grants the enrolled
user read access and binds the pipe to the host process identity.

```powershell
& '<normal-user client>\OpenBlade.Capture.exe' cpu-voltage-session-client `
  --session '<session GUID>' --action ObserveOnly
& '<normal-user client>\OpenBlade.Capture.exe' cpu-voltage-session-client `
  --session '<session GUID>' --action Status
```

Requests return promptly; `Status` returns the current job and fixed evidence
records when available, including the fixed owner context, child identity and a
bounded coherent snapshot of its live lifecycle journal. Redirect client output to a user-owned file to retain a
copy. The original private evidence is unchanged. After observation completes,
`observationAdmitted: true` permits the `VoltageCycle` action. A refused
observation blocks the cycle. A resolved readonly refusal permits a fresh explicitly
requested observation within the three-observation limit. The normal cycle runner still repeats all fresh
admission checks. Use `Close` when the host is idle and resolved.

The host records request acceptance durably before launching. An optional
`--request-id <32-character GUID>` identifies a request. Never resubmit a job
after a lost response; inspect `Status`. There are no automatic retries or a job
queue. Between resolved jobs, the host can start only the exact pinned installed
OpenBlade Service, after checking its configuration, protection and driver
absence. It does not reinstall to start Service.

An ambiguous operation stops new jobs and retains the owner, process handles and
file pins. For a failed `ObserveOnly` job, request the fixed `CleanupReadOnly`
action once. It verifies the pinned no-Set/no-Restore result, enrolled Service
identity, child provenance, complete ChildBound lifecycle and signed owned
driver configuration. It refuses a live smoke child, missing or changing proof,
known pending CPU recovery, and any failed voltage cycle.

Cleanup follows the reviewed resource order: typed shutdown of the exact
Service (including normal non-CPU shutdown cleanup), verified Service terminal,
termination of only the exact retained parked owner, then owned driver stop,
delete and independent full absence. It never issues CPU Observe/Set/Restore,
terminates a smoke child, or guesses that CPU restoration occurred. A running
owner must have emitted its final park reason; a timeout alone is insufficient.
The existing shutdown path performs no CPU recovery; cached pending CPU recovery
when present still causes refusal. Cleanup is not a claim about unrelated CPU state.

```powershell
& '<normal-user client>\OpenBlade.Capture.exe' cpu-voltage-session-client `
  --session '<session GUID>' --action CleanupReadOnly
```

After a durable verified cleanup receipt, `Status` includes that receipt and a
fresh `ObserveOnly` request may run under the same package and elevation. Cleanup
does not clear a lock, sleep, power or session block. No retries are automatic;
each observation and cleanup has a new durable request ID. Cleanup failure
retains resources and refuses another cleanup attempt. Its 90-second cooperative
budget never abandons a pending native call or starts a replacement worker.
Evidence older than a complete ChildBound lifecycle or beyond its first cleanup
intent requires explicit attended reconciliation. A package revision still
requires new enrollment.

`Close` cannot discard an unresolved owner. A fatal server failure during an
unresolved job also retains the host. There is no generic recovery, shell,
arbitrary path or process-selection command. This source-tested cleanup path
still requires attended installed validation before relying on it unattended.
