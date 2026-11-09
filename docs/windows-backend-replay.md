# Windows backend replay baseline

The compatibility baseline was taken from `df4cbe096e8cace43ab527f6335afd9d705528ed`. It covers the Windows service, current-user Session Agent, and first-party helper boundaries. The current C# hosts establish the oracle. The replay runs codecs on memory streams, dispatch against synthetic clients and fake state, the v51 one-shot connection loop on a memory stream, and persistence against test-owned temporary files. The existing Rust codec and decoded policy fixtures remain the source for their respective FFI contracts.

## Running the managed oracle

From the repository root with the usual Windows .NET, MSVC and pinned Rust build prerequisites:

```powershell
dotnet test tests/OpenBlade.Contracts.Tests/OpenBlade.Contracts.Tests.csproj --filter 'FullyQualifiedName~WindowsReplay|FullyQualifiedName~RustStatusCli' -p:NuGetAudit=false
dotnet test tests/OpenBlade.Service.Tests/OpenBlade.Service.Tests.csproj --filter 'FullyQualifiedName~WindowsReplay' -p:NuGetAudit=false
dotnet test tests/OpenBlade.Protocol.Tests/OpenBlade.Protocol.Tests.csproj --filter FullyQualifiedName~WindowsRustModelEvidenceInventory -p:NuGetAudit=false
dotnet test tests/OpenBlade.Session.Tests/OpenBlade.Session.Tests.csproj --filter 'FullyQualifiedName~WindowsReplay' -p:NuGetAudit=false
pwsh -NoProfile -File tests/Build.Tests/WindowsReplayPublishInventory.Tests.ps1
```

The existing affected-project CI runs the test projects and the publish contract. The inventory contract parses the seven explicit project and staging pairs in Publish.ps1. It checks the listed helper switches and persistence readers against their source anchors. New helper modes and stores require inventory review. This check requires no restored or built assemblies.

## Consuming the fixtures from Rust

| File | Contract |
| --- | --- |
| [service.json](../tests/OpenBlade.Contracts.Tests/WindowsReplay/v1/service.json) | Service v79 envelope and limits, all 51 closed request discriminators grouped by family, both server frame types, request normalization and failure vectors, typed result numbers and status projection. |
| [service-status-event.json](../tests/OpenBlade.Contracts.Tests/WindowsReplay/v1/service-status-event.json) | Full synthetic v79 `statusEvent` produced and read by the real managed codec. For a `getStatus` or initial `subscribeStatus` response, use its `status` body and the request ID with the successful response envelope. |
| [v51 status fixtures](../rust/crates/openblade-status-cli/fixtures/) | Frozen compatibility request and a synthetic response used for the Rust status CLI's whitelist. The managed writer is checked on the whitelist fields. The deliberately synthetic private and unknown fields exercise the Rust reader. |
| [service-runtime.json](../tests/OpenBlade.Service.Tests/WindowsReplay/v1/service-runtime.json) | Synthetic session and dispatcher scenarios, override migration and quarantine, pending-journal restart, and typed thermal completion and no-retry outcomes. |
| [session.json](../tests/OpenBlade.Session.Tests/WindowsReplay/v1/session.json) | Every display, GPU apps, THX audio, and battery report request, with numeric fields, version and frame rejection, and an audio response with the older optional-field shape. |
| [Model evidence](../tests/OpenBlade.Protocol.Tests/Fixtures/windows-rust-model-evidence-inventory.json) | All 19 catalog profiles, production feature-evidence rows for the 17 source-reviewed profiles, and separate physical-evidence pointers for the two physically validated models. |
| [Inventory](windows-backend-replay-inventory.json) | Windows publish entry points, helper modes, persistence owners, and the phase responsible for remaining parity. |

The JSON fixtures are independent of .NET. Preserve integer precision and the distinction between absent and null. In a wire vector, serialize `input` to UTF-8 JSON with the discriminator first where present. `json` is a literal body used for duplicate keys. Prepend the signed 32-bit little-endian payload length in bytes. `declaredLength` supplies a prefix with no payload. Compare successful re-encodings as JSON objects, preserving array order and numeric values. `output` is the expected canonical body when the input is a request. Failure categories describe the point of rejection and do not freeze localized or exception text. Session request vectors without `failure` use the same object for input and output. The persistence vectors use the raw JSON object on disk and assert state after reconstruction of the store.

For example, emit a binary test frame for the read-only host or inspect the operation inventory from a clean checkout using Python's standard library:

```powershell
python -c 'import pathlib, struct; p=pathlib.Path("tests/OpenBlade.Contracts.Tests/WindowsReplay/v1/service-status-event.json").read_bytes(); pathlib.Path("status-event.frame").write_bytes(struct.pack("<i", len(p)) + p)'
python -m json.tool tests/OpenBlade.Contracts.Tests/WindowsReplay/v1/service.json
```

Phase 2 should load these files directly in Rust tests. Replay the v79 cached read and subscription with the full v79 status body and replay the v51 compatibility read with the existing CLI fixtures. Check the active/background/cancelled scenarios and malformed envelope cases against the separate development endpoint. Existing service named-pipe tests retain authority for Windows ACL, Identification QoS, persistent slots, bounded subscriptions and authentication order. The v51 one-shot vectors pass through the production connection loop. Windows security tokens, service lifecycle timing and subscriber reconnect require separate validation. Implement and verify those on the development pipe in Phase 2.

## Remaining parity by phase

| Phase | Owning behavior and evidence |
| --- | --- |
| 2 · Read-only service | Cached status, subscription initial/event frames, request correlation, no activation for v51 reads, active-session projection, capacity, authorization, readiness and shutdown. Owners are `IpcMessageCodec`, `NamedPipeControlServer`, `ServiceRequestAccessPolicy`, `ControlRequestHandler`, `StatusSubscriptionHub`, `Worker` and the C# service client. Diagnostics archive generation and hardware observation stay with the service parity work. |
| 3 · Windows adapter | Catalog and exact identity, serialized typed report transport, command and transaction matching, per-write admission, cancellation, handle retirement and uncertain submission. `RazerBladeModelCatalog`, `ExperimentalBladeModels.FeatureEvidenceFor`, the exact backend admission manifests and [source-reviewed support](experimental-blade-support.md) own the capability evidence. Use the existing [shared parity fixtures](../tests/Fixtures/Parity/v1/) for report encoding and the protocol tests for model-specific command semantics. The thermal replay covers executor results from fake outcomes. Native cancel/completion races and independent readback belong to the adapter. |
| 4 · Runtime and recovery | Profile selection and merge, application context, power/session and controller transitions, durable configuration, thermal convergence and one-shot/CPU/HyperBoost ownership. Use the inventory's store owners and existing `ProfileStoreTests`, `BatteryConfigurationStoreTests`, `PersistentOverrideStoreTests`, `ThermalTransitionExecutorTests` and `*OwnershipJournalStoreTests`. Aggregate commit/recovery, profile migration, boot epochs and ownership journal variants need portable replay before replacing their readers. |
| 5 · Service feature parity | Performance, GPU clock/CPU tuning, HyperBoost, fan curves/stop/Guardian, battery protection and full-charge override, lighting ownership and frame modes, telemetry and CPU worker, diagnostics, one-shot controls, brightness HID usage, cooling-pad settings/leases and recovery. The service registry accounts for every IPC operation. Synthetic wire samples cover representative operations. Per-feature safety behavior still relies on existing focused tests and exact-device evidence. RC21-0230 is a separate source-backed integration. |
| 6 · Session Agent and helpers | Display mode/HDR/ICC/brightness/power mode, GPU scan and safe termination, THX and optional provisioning, battery report, foreground context, input/Fn/SnapTap, interactive lighting/LampArray and worker isolation. The four codecs have frozen request vectors. Dispatch outcomes, protected worker pipes, restart behavior, user-side stores and platform trust still use their focused managed tests. WinUI and deployment tooling remain C#. |
| 7 · Cutover | Package signing, helper composition, upgrade, rollback, uninstall and the single hardware-owner handoff. Use the package role inventory and existing setup/publish gates. |

## Phase 3a Windows metadata discovery

From the repository root, build the developer executable and run the synthetic tests with the pinned Rust toolchain. The tests do not invoke the Windows metadata entry point.

```powershell
cargo +1.99.1 test --locked --manifest-path tools/openblade-windows-host/Cargo.toml
cargo +1.99.1 build --locked --manifest-path tools/openblade-windows-host/Cargo.toml --bin openblade-windows-discovery
dotnet test tests/OpenBlade.Protocol.Tests/OpenBlade.Protocol.Tests.csproj --filter FullyQualifiedName~WindowsRustModelEvidenceInventory -p:NuGetAudit=false
```

The separate `openblade-windows-discovery.exe` accepts no arguments and emits one JSON snapshot. It is outside the product workspace and publish inventory. It has no service or IPC entry point. The Rust workflow invokes it only for a negative metadata smoke on a GitHub-hosted Windows Server VM.

- `platform` applies the managed Windows 11 client, native x64 and build-floor checks. `registryMatch` independently compares the two 64-bit HKLM sources, `HARDWARE\DESCRIPTION\System\BIOS` and `SYSTEM\CurrentControlSet\Control\SystemInformation`. Manufacturer and product name must agree ordinally across sources and match the managed projection. SKU and BIOS version are diagnostics on a registry match. The projection test compares all rows with the managed catalog, exact host factories, and candidate selectors.
- `hid.inventory` describes the SetupAPI scan of present HID interfaces. Interface names are opaque and internal to the scan. The associated devnode supplies hardware IDs. Conflicting VID/PID, MI, collection, or usage aliases make a row incomplete. An alternate HID device ID family can be readable without supplying a VID/PID hint. Missing or unreadable metadata and duplicate interface names make the scan incomplete. Bounds are 256 UTF-16 code units per registry value, 2,048 units per interface buffer, 4,096 units per hardware-ID buffer, and 4,096 interfaces. Ctrl+C and Ctrl+Break request cooperative cancellation between synchronous calls. Cancellation observed before output discards the snapshot. A second interrupt goes to the Windows default handler.
- `hid.candidates` evaluates hints only after an exact registry match on a supported platform. It uses the exact model VID/PID. The two physically validated models additionally use the HIDClass top-level collection usage hint (page 1, usage 2). The source-reviewed factory has no fixed usage selector. Candidate counts describe distinct interfaces, including multiple collections of a single device. A vendor-qualified usage alias can supplement usage evidence when its vendor agrees. It cannot supply a product ID. `singleUnresolved` still needs handle attributes and the 91-byte feature-report capability. On source-reviewed models it also needs the existing per-model functional interface selection. The generic managed selector has no MI, parent, or container gate. Feature-specific topology requirements stay with their respective backends.
- The executable makes no device opens or HID report calls. It issues no commands and establishes no controller ownership, firmware compatibility, feature admission, or physical validation. The C# service remains the sole control HID owner.

The metadata adapter follows Microsoft’s [SetupAPI enumeration](https://learn.microsoft.com/windows-hardware/drivers/install/enumerating-installed-device-interfaces), [interface detail](https://learn.microsoft.com/windows/win32/api/setupapi/nf-setupapi-setupdigetdeviceinterfacedetailw), and [HIDClass hardware IDs](https://learn.microsoft.com/windows-hardware/drivers/hid/hidclass-hardware-ids-for-top-level-collections) contracts.

Phase 3b needs a typed Windows session with independently observed handle identity and model-specific command selection. Its operation owner must serialize request, completion and response validation, including transaction and command matching. A cancelled or timed-out overlapped operation must retain its buffer and handle until terminal completion is observed. Retire the session when completion or submission is uncertain, reconcile read-only, and avoid replaying that request. The session must preserve the managed controller checks and each feature’s separate fresh write admission. Phase 3a’s metadata counts cannot feed those gates as handle proof.

Updating a frozen result requires review of the owning managed boundary and the impact on Rust consumers. macOS, Linux and SwiftUI are outside this Windows baseline.

## Phase 3b standard Windows feature exchange

Run the isolated transport target on Windows x64 with the pinned Rust toolchain and MSVC.

```powershell
cargo +1.99.1 test --locked --manifest-path tools/openblade-windows-host/Cargo.toml --test standard_transport
```

The integration test target is the only entry point to the transport. It has no device opener. It imports the shared Rust codec and types as test dependencies. Neither developer binary nor any shipped component links the transport. The typed entry point exercises the RZ09-0581 adapter-power query from `AdapterPowerCommandClient`. It returns the two validated level codes. Wattage classification and per-model state admission remain with C#. The [sanitized adapter-power evidence](../evidence/legacy-captures/decoded/rz09-0581-pid-02e0-bios-3.01-adapter-power-readback-fixture.json) provides the observed command and payloads. The replay reconstructs the enclosing reports with synthetic transaction IDs and native outcomes. `WindowsHidNative.Submit`, `WindowsRazerHidSession` and `WindowsRazerHidSessionTests` supply the Windows buffer direction, count rules and failure semantics. `ProductionBatteryFactory` supplies the 30 ms SET-to-GET delay.

- The shared Rust codec encodes the 90-byte request and validates the checksum, transaction, command, success status and response size. The Windows buffer has a separate report ID byte at offset zero. The SET IOCTL receives the buffer as input. The GET IOCTL receives a separately initialized buffer as output. For this zero-ID route the managed count rule accepts 90 or 91 transferred bytes when the prefix is correct.
- A session admits one active exchange and one waiter. The caller supplies a monotonic deadline covering the queue and exchange. It consumes transaction IDs 1 through 255 without reuse. Cancellation before SET submission can leave the transport available. A native SET rejection records its error separately and retires the handle. Once SET may have reached the driver, a missing or invalid reply retires the handle and requires independent read-only reconciliation before any later session. There is no replay or fallback.
- The Windows prototype uses a per-operation manual-reset event with `DeviceIoControl`, `CancelIoEx`, `WaitForSingleObject` and `GetOverlappedResult`. The managed route uses IOCP for the same IOCTLs. The Rust operation retains the handle, event, buffer and OVERLAPPED through terminal completion. It records the targeted cancellation result separately from completion, including a completion that wins the race. Cancellation and deadlines bound admission and trigger cancellation. An active caller remains in the drain if a driver delays completion, and the single wait slot stays bounded. See [DeviceIoControl](https://learn.microsoft.com/en-us/windows/win32/api/ioapiset/nf-ioapiset-deviceiocontrol), [CancelIoEx](https://learn.microsoft.com/en-us/windows/win32/api/ioapiset/nf-ioapiset-cancelioex) [WaitForSingleObject](https://learn.microsoft.com/en-us/windows/win32/api/synchapi/nf-synchapi-waitforsingleobject) and [GetOverlappedResult](https://learn.microsoft.com/en-us/windows/win32/api/ioapiset/nf-ioapiset-getoverlappedresult).
- Windows tests link the real APIs. A blank GET is rejected on a fresh private named pipe. A pending connect on a second fresh pipe exercises targeted cancellation and observes terminal `ERROR_OPERATION_ABORTED`. These kernel tests and the scripted races establish software behavior. Physical HID timing and device replies require separate evidence.

The next Phase 3 gate is a reviewed, read-only activation slice. It needs an exact-model lease with fresh platform, host, collection, HID attributes and 91-byte feature geometry checks on the **opened** handle, the existing exact-command and competing-controller policy, and separately authorized single-owner verification of the query on Windows hardware. The metadata snapshot cannot supply this lease. Remaining transport work includes model-specific transaction and pacing policies, other typed commands, SET-only and batch feature operations, and separate matrix or accessory routes where the production backend uses them. Any mutating path also requires fresh per-write admission, independent readback and durable restoration.

## Phase 3c RZ09-0581 opened-handle fixture

The integration target above now imports the adapter from
[`src/adapter_power`](../tools/openblade-windows-host/src/adapter_power/).
The fixture consumes one simulated owner lease for one `07/8C` adapter-power
query. Neither developer executable links the adapter. No native HID opener,
controller authority provider, or production service handoff is wired.

- The supported Windows platform and both registry identities must agree on
  `Razer` and `Blade 16 - RZ09-0581`. The fixture shares the ordinal
  registry comparison with Phase 3a. SKU and BIOS are diagnostic. The
  discovery snapshot and path text do not establish handle identity.
- The fixed standard route selects exactly one opened `1532:02E0` collection
  with usage page `0001`, usage `0002`, and a 91-byte feature report. This is
  the RZ09-0581 selector from `WindowsRazerHidEvidenceEvaluator` and
  `WindowsRazerHidSessionOpener`. The Win32 implementation queries
  [attributes](https://learn.microsoft.com/windows-hardware/drivers/ddi/hidsdi/nf-hidsdi-hidd_getattributes)
  and [preparsed collection data](https://learn.microsoft.com/windows-hardware/drivers/ddi/hidsdi/nf-hidsdi-hidd_getpreparseddata)
  from the same owned handle, then reads the collection caps with
  `HidP_GetCaps`. The fixture requires a complete candidate set. It fails
  closed on unreadable identity, ambiguity, or a changed selected handle.
  Cardinality comes from the completed scan. Re-reading the selected handle
  does not refresh that scan.
- The fixture repeats host and strict authority checks after the candidate
  scan, then reads the selected handle again before submission. The simulated
  lease stays held through native drain and handle closure. The fixture uses a
  new managed transaction context beginning at `01`, report ID `00`, a 30 ms
  SET-to-GET delay, and a three-second exchange cap. The existing codec independently matches the response. The
  two returned level bytes preserve firmware values, including levels the
  managed classifier maps to Unknown.
- Fake handles exercise selection, refusal, cancellation, response matching
  and disposal. Windows also links the real HID and overlapped I/O APIs and
  exercises rejection on private non-HID pipes. These tests establish software
  behavior. The device has not been exercised.

### Physical verification runbook for a later authorized session

1. Review a pinned activation build that implements a service-coordinated,
   single-use handoff. The C# owner must quiesce and drain the control and
   matrix lanes covered by `Rz090581FeatureCommandGate`, close their handles,
   and prevent reacquisition while the Rust owner holds the lease. The process
   that supervises the lease must verify Rust's terminal drain and handle
   closure before returning ownership. Closing the tray, acquiring a
   process-local semaphore, or successfully opening a shareable HID handle
   does not prove exclusivity. Until this handoff and a strict
   `WindowsHardwareAccessGuard`-equivalent authority provider exist, the
   activation gate is blocked.
2. Obtain separate attended authorization for the exact build, RZ09-0581
   `1532:02E0`, one `07/8C` exchange and the ownership pause/recovery. The
   query reads firmware state through a SET feature request followed by GET.
   The handoff can briefly suspend OpenBlade controls and automation. If
   coordinated service shutdown is part of the separately reviewed handoff,
   its normal cleanup can return keyboard mode and OpenBlade-owned manual fans
   to firmware control. Record this impact before execution. Do not stop
   competing Razer software.
3. On stable AC with the machine fully awake, record the pinned C# and Rust
   builds, exact host and HID evidence, power source, a fresh typed C#
   adapter-power baseline, confirmed keyboard/fan state and saved intent,
   and service health. Abort if any required evidence is missing or the C# baseline is
   Unknown. After the handoff, rescan and verify the selected handle, fresh
   host identity, stable power and session, strict controller absence, and
   the live exclusive lease before the first SET. Invalidate admission if a
   topology change is reported after the scan. A change, ambiguity, or refused
   check ends the run before submission.
4. Execute exactly one typed adapter-power exchange. Record sanitized
   request/response correlation, transferred lengths, actual and recommended
   levels, pacing, and terminal outcome. Drain outstanding I/O and close the
   handle before relinquishing ownership. Compare the result with the
   same-source C# baseline and a fresh typed C# read after the handback.
   Cached status can provide service health and ownership context.
   Confirm service health, restored ownership, and keyboard/fan readback
   against the baseline and recorded cleanup expectations.
5. If submission or readback is uncertain, retain the failure and native drain
   evidence, close the lane, and prohibit replay. After terminal closure is
   verified, reconcile with the coordinated C# owner's read-only checks before
   resuming automation. If closure, ownership, or restoration cannot be
   confirmed, leave further submissions blocked and report the unresolved
   state.

Further parity work covers RZ09-0528 and the source-reviewed models'
per-model functional selectors (`ExperimentalBladeFactory` and
`ValidateControlInterfaceAsync`), transport and transaction differences,
SET-only and batch operations, the PID 02E0 matrix route, and accessory
routes such as the cooling pad. Each mutating route also needs its own fresh
admission, independent readback and durable restoration. Product packaging
and the hardware-owner cutover belong to later phases.

## Phase 3d Windows ownership rehearsal

Run the process tests with the pinned Rust toolchain on Windows x64.

```powershell
cargo +1.99.1 test --locked --manifest-path tools/openblade-windows-host/Cargo.toml --test standard_transport ownership::
```

The integration test launches its own executable as the fake incumbent and
Rust worker. The roles are reachable only through the test target. Each case
creates a new temporary directory with fixed control, matrix and owner-fence
files. The incumbent keeps an exclusive Windows open on the fence while
both lanes have work in flight. It writes and flushes a restart block before
retiring either lane, stops admission, joins both lane operations, closes
their handles and checks closure through fresh exclusive opens.

The incumbent creates an unnamed, non-inherited job with kill-on-close and
one active process. Windows puts the worker in the job at creation. An
explicit handle list passes its anonymous request and reply pipes and a
wait-only handle to the exact incumbent. Test output has separate handles.
The worker supplies a fresh attempt nonce, accepts a matching grant once and
verifies the volume and full 128-bit file ID from its own open handle. It
checks the incumbent handle during admission and before fake I/O. The
fixture assumes trusted executables running as the same user in the
test-owned directory. A production service also needs a restricted launch
and service-protected state. [Windows process creation attributes](https://learn.microsoft.com/windows/win32/api/processthreadsapi/nf-processthreadsapi-updateprocthreadattribute) · [Windows file identity](https://learn.microsoft.com/windows/win32/api/winbase/ns-winbase-file_id_info)

The worker consumes the Phase 3c lease once through `query_once`. The fake
SET stays in flight while the tests observe the worker's exclusive control
handle and the released matrix handle. A separate incumbent is refused while
the lease is live, before the timed query starts. The query returns the two
validated levels. The incumbent requires the expected
terminal receipt, successful exit of its exact child and independent file
reopens and readback before clearing the restart block and returning to
active. The Phase 3c fake host and collection data and scripted controller,
power and session decisions remain separate from process ownership.
File identity, share denial, child pipes, process exit, job teardown and
persistence across replacement processes are Windows behavior exercised by
the test. The fake operations do not exercise physical driver drain.

A stale attempt or file identity and a changed controller or execution
decision refuse before SET. Cancellation after SET, abrupt worker exit, a
lost or malformed receipt and failed readback preserve the restart block. A hung worker
ignores the channel when the test kills the incumbent. The job releases
the worker's file, while a new incumbent still refuses the marked directory.
The marker models fail-closed recovery across process death on a running
system. The fixture has no marker repair command and tests neither
power-loss durability nor reconciliation against real firmware.

### Phase 3e managed ownership boundary

The private Windows service fixture composes the real serialized device
provider, battery and lighting factories, retained lighting session, and PID
02E0 matrix writer. Windows exclusive test files stand in for HID handles.
The child waits on redirected standard input and the owner observes exit through
its retained Windows process handle. The Rust tests above separately exercise
the private pipe protocol, job teardown and terminal receipts.

Run the managed fixture on Windows with
`dotnet test tests/OpenBlade.Service.Tests -p:NuGetAudit=false --filter FullyQualifiedName~ManagedOwnerHandoffTests`.
It checks active and queued control work, a submitted frame from the retained
Ambient Awareness renderer, matrix close ordering, blocked reopen, stale and
duplicate handback, a mismatched adapter-power read, cancellation, a failed
close and a fresh provider over a pending marker.

The private scope requires the same native retirement gate and disposal
registry across the battery, fan-RPM and lighting factories. Both preflight and
handback require an exact tested or model-scoped firmware probe. An ordinary
untested firmware override cannot authorize this handoff. The scope closes
ordinary provider and renderer admission before draining. A successful drain requires lighting control and matrix closure,
empty retained-disposal work, and release of the shared native command gate.
The temporary close leaves firmware mode and saved settings alone. Handback
uses the same typed adapter-power read through a fresh control port after
exact child exit, waits for terminal cleanup and compares the observed value
with the recorded baseline. Exit and readback establish distinct facts.
A failed or canceled transfer after journaling leaves the marker. Partial
records also block startup. The private supervisor must provision its storage
directory before composing the owner. A new owner cannot reconstruct a
lease from a pending record.

Installed service composition does not select the scope. The lease proves
managed drain in the fixture. A separate grant still needs the fixed Rust
query, pinned child/job/pipe binding and strict native authority. The managed
scope leaves the laptop-control recovery store untouched.

### Remaining work before the physical runbook

1. Put an owner-pause latch at the C# service device admission boundary
   before waiting for operations. Cover `SerializedBladeDevice.EnterHardwareOperationAsync`,
   provider acquisition and independent adapter-power opens such as
   `FanStopAdapterPowerReader`. Hold new service automation, interactive
   lighting and command admissions before draining. Read-only cached status
   can continue. The `Rz090581FeatureCommandGate` orders individual protocol
   commands. It cannot fence the lifetime of all openers or stop a later
   admission. [Device provider](../src/OpenBlade.Service/WindowsVerifiedBladeDeviceProvider.cs) · [Independent adapter-power reader](../src/OpenBlade.Protocol/FanStopAdapterPowerReader.cs) · [Shared gate](../src/OpenBlade.Protocol/Rz090581FeatureCommandGate.cs)
2. With admissions closed, drain the service hardware gate, cancel queued
   work that has not reached native submission and await terminal retirement
   of every in-flight command. Fence lighting reopen and retire the retained
   port via `ProductionLightingSession`. Drain and dispose the PID 02E0
   matrix writer and its queue separately. Account for native work retained
   by the command gate and bounded disposal registry after a caller returns.
   Treat a drain deadline or pending disposal as unresolved. Verify terminal
   completion and disposal of both lanes before granting. The service shutdown
   path restores owned HyperBoost, keyboard mode and fan state. A temporary
   handoff needs a dedicated pause and retirement path. [Lighting session](../src/OpenBlade.Service/ProductionLightingSession.cs) · [Matrix writer](../src/OpenBlade.Protocol/WindowsPid02E0MatrixFeatureWriter.cs) · [Command gate](../src/OpenBlade.Protocol/RazerCommandSessionGate.cs) · [Pending disposals](../src/OpenBlade.Protocol/PendingDisposalRegistry.cs)
3. Persist a service-protected pending handoff before releasing either lane.
   Bind it to the admitted exact model, build, boot and attempt. Gate
   service startup and all reacquisition paths on that record. Launch a
   pinned worker under an explicit handle allowlist and retained process/job
   supervision. Grant only the fixed query, with fresh Windows platform,
   both registry identities, strict controller absence, power and session
   admission, and independent opened HID identity and report geometry.
   Revoke on topology, session, power or controller changes. Link the
   adapter only after these production gates and their refusal paths have
   been implemented and reviewed. [Strict controller guard](../src/OpenBlade.Protocol/WindowsHardwareAccessGuard.cs)
4. Keep the pause latched through Rust terminal native drain and disposal.
   Process exit is useful evidence about handle lifetime. For an unexpected
   exit it cannot establish that the firmware received or completed an
   exchange. Keep unresolved intents and reconcile through a separately
   admitted C# read-only path after closure. Preserve pending laptop-control
   journal meanings, saved fan and keyboard intent and stop-on-ambiguity.
   Release normal reacquisition and automation only after successful
   reconciliation. A service restart, lost lease receipt or unresolved native
   completion must stay fenced. [Pending control store](../src/OpenBlade.Service/LaptopControlPendingStore.cs) · [Control executor](../src/OpenBlade.Service/LaptopControlExecutor.cs) · [Service recovery](../src/OpenBlade.Service/ServiceControlRuntime.cs)

For a controlled developer build, first refresh the ancestor stack from
`main`, including the service-data ACL hardening, and rerun the fixture on the
rebased exact head. Connect the private supervisor before any real service
acquisition, bind the marker to verified build and boot identities in protected
storage, and define supervised restart recovery, keyboard-mode ownership
and renderer reapplication across handback. Connect the pinned worker and strict opened-handle authority
for the fixed query, then rehearse that assembled artifact on fake devices.
The separate attended RZ09-0581 run also needs explicit authority for the
service lifecycle and the single device query in the Phase 3c runbook.
