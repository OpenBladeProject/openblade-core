# CPU validation tools

These developer commands extend the [Voltage Optimizer product](cpu-tuning.md).

The developer `--cpu-tuning-layout-smoke` harness renders seven synthetic states
at two widths without connecting to the service. It checks editor bounds,
saved/live separation and recovery presentation; screenshots are not hardware
admission or installed-application proof.

FirmwareSmoke's `cpu-voltage-optimizer` command requires
`--confirm-target RZ09-0528-CPU-VOLTAGE-OPTIMIZER-SMOKE`, an exact installed
`--expected-service-version` with source suffix, and a fresh private `--output`.
It durably captures a uniform twelve-core −1 or zero baseline, observes it again,
applies the opposite On/Off state, independently checks all offsets, restores
through Service ownership and verifies the exact original and resolved recovery.
Uncertainty stops without retry or guessed restoration. Existing evidence is
preserved. Installation, provisioning, restart and sleep remain separately
authorized actions.

### Bounded live voltage validation build

Issue [#659](https://github.com/OpenBladeProject/openblade-core/issues/659) adds
`-p:OpenBladeCpuVoltageValidation=true`, default off. This developer profile
advertises only enabled −1 and disabled zero on the exact RZ09-0528 target.
The Service rejects live PPT, mixed CPU requests and other live offsets. The
installed native provider independently bounds every forward, reverse and restore
command, and every value in the exact native before-vector, to −1 or zero.
Fresh firmware range metadata must still cover the operation and exact reverse;
identity, active-session, full-power, continuity, ownership and readback checks
remain required. The profile uses the existing transaction and FirmwareSmoke
command. It neither starts nor provisions the optional AMD driver.

Normal builds admit the captured CO range and retain the existing saved path;
live PPT remains `ObservedOnly`.
The validation profile does not add saved write authority; its shared advertised
−1-only capability also narrows saved requests that consume that range. The
attended cycle uses `LiveNow` only and submits no saved or PPT command.

Unit builds require `-p:SourceRevision=<40-character-commit>` and should use a
separate `--artifacts-path`. Their product version is
`0.24.1+voltage-validation.<first-eight-commit-characters>`. For a coordinated local candidate, the
existing `build/Publish.ps1` accepts `-CpuVoltageValidation` only with
`-BuildChannel LocalDev`, a reserved `-LocalDevVersion` and `-SourceRevision`
matching a clean checkout, including nonignored untracked files. Every published project receives the same
profile; the version is `<LocalDevVersion>+voltage-validation.<first-eight-commit-characters>`.
Stable and Nightly publication reject opt-in, and normal publication explicitly
disables it. The runner accepts this exact marker with an 8- or 40-digit hex
revision, and still requires an exact match to the installed Service version.
These profile builds do not establish physical validation or broader production
admission; the CPU/cooling replacement must retain the combined IPC 61 sources.


### Attended temporary-driver owner

Capture's `cpu-voltage-owned-smoke` reuses `CpuPmOwnedDriverLifecycle`: exact
signed V31 registration, durable ownership journal, independent absence checks
and retained child rules. It launches only packaged FirmwareSmoke; the owner
performs no CPU-PM getter or setter. Maximum twenty-four CO setters cover one
all-core −1/zero transition and exact original restoration.

Approval must cover installation, temporary owned-driver create/start/stop/delete,
the cycle and normal OpenBlade shutdown cleanup. Driver absence and the exact
reviewed Service process are established first. Cleanup requires a terminal
zero-exit Smoke child, fresh pinned baseline/results, complete forward/original
readbacks and `OriginalVerified`. A typed shutdown acknowledgement is insufficient:
the exact Service must terminate without replacement before owned-driver cleanup.

The single 600-second deadline covers admission through shutdown and cleanup;
ordinary PM owner continuity remains 120 seconds. The client connection remains
two seconds; requests and native setter boundaries use the remaining original
deadline. Deadline, Ctrl+C, unknown completion, Service replacement or lost
file/power/session/ownership evidence parks the owner and retains resources.
It does not kill a child, guess restoration, retry shutdown or delete the driver
on uncertainty. Retained failures require reconciliation.

Use one committed combined IPC 61 package and independently reviewed protected
payload. Strict manifest version 1 binds full source and `name`, `length`,
`sha256` for the complete Capture root, `firmware-smoke/` and `service/` trees.
Cycle assemblies share the full source; Service has matching
`+voltage-validation.<8hex>`. Required arguments are `--payload`, `--source`,
`--manifest-sha256`, `--expected-service-version` and
`--confirm-target RZ09-0528-OWNED-V31-CO-AND-OPENBLADE-SHUTDOWN`.
Pins cover package and installed files; result handles deny writes/deletion
through cleanup. The fresh run tree is administrator/SYSTEM-only. JSON cannot
grant authority, and building source authorizes no installation or physical test.

Ordinary builds support standalone `--observe-only` with its exact confirmation
and separate `--owner-source`. The manifest independently binds helper and
Service/Smoke source. Cycles and session jobs reject ordinary profiles before
provisioning. Observation sends no CO/PPT setter and keeps owned cleanup and
ambiguity rules. Session/broker enrollment and job details are in
[cpu-voltage-session.md](cpu-voltage-session.md) and
[separate deployment broker guide](https://github.com/OpenBladeProject/openblade-core/blob/feat/cpu-deployment-tools/docs/unattended-deployment-broker.md).

Voltage packages retain bounds of 1,536 files, 512 KiB manifest, 1 GiB payload,
4,096 pins and 2,048 inventory entries. Ordinary PM retains 256 files, 64 KiB
manifest, 512 MiB payload, 800 pins and 768 entries. Both retain 128 MiB per-file
and three-component relative paths. These bounds accommodate complete runtime
trees without trimming dependencies.

Visible validation versions use exactly eight lowercase source characters to
fit the existing 64-character IPC limit. Full 40-character SourceRevision is
retained in Service metadata `OpenBladeValidationSourceRevision`, publication
checks and manifest. The owner verifies prefix and complete file hashes.

Historical captures and intermediate implementation evidence remain in Git
history and private validation archives. Current work is tracked in
[issue #622](https://github.com/OpenBladeProject/openblade-core/issues/622),
[simplification #698](https://github.com/OpenBladeProject/openblade-core/issues/698)
and [draft PR #623](https://github.com/OpenBladeProject/openblade-core/pull/623).
