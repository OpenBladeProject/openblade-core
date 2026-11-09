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
