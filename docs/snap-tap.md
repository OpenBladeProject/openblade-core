# SnapTap

When A and D are held, the last pressed key takes priority. Releasing it restores the other key if it is still held.

Open **Laptop**, find **SnapTap (A/D)** under **Keyboard**, and turn it on. Release keys and mouse buttons to activate it. SnapTap is off by default and stays running when you close the visible Tray.

On every reviewed model below, connect AC power. If the driver is missing or its setup is incomplete, choose **Install driver** and approve the Windows prompt. OpenBlade checks Windows Update first, including optional driver updates, and installs only the reviewed Razer components matching this laptop. If Windows Update offers no suitable component before any installation, OpenBlade uses the model's pinned driver package. If a restart is requested, restart Windows and follow the setup guide to complete any remaining steps. After a successful installation, turn SnapTap off and on. **Install driver** and **Setup guide** stay visible when a supported stack is unavailable. If setup is blocked, the result explains the next action and diagnostics retain the exact failed check. **Setup guide** opens the catalog instructions.

Choose **Windows hook** or **Razer driver** under **SnapTap input method**. Existing preferences keep the Razer driver selected. The Windows helper uses A/D across keyboards and needs no Razer driver; some games use input paths it cannot handle. The Razer backend uses the laptop keyboard through its exact model-specific filter and shared control driver. The information button includes game compatibility details.

The function row has its own input-method choice. See [keyboard input methods](keyboard-input-backends.md) for defaults, switching and driver support.

## Supported driver profiles

| Model | PID | Reviewed source build | SnapTap validation |
| --- | --- | --- | --- |
| RZ09-0581 | 02E0 | 1.0.0.78 | Physically validated |
| RZ09-0528 | 02C6 | 1.0.0.78 | Source-reviewed experimental |
| RZ09-0483 | 029F | 1.0.0.73 | Source-reviewed experimental |
| RZ09-0482 | 029D | 1.0.0.73 | Source-reviewed experimental |
| RZ09-0530 | 02C5 | 1.0.0.78 | Source-reviewed experimental |
| RZ09-0328 | 0255 | 1.0.0.66 / 1.0.0.71 | Source-reviewed experimental |
| RZ09-0484 | 02A0 | 1.0.0.73 | Source-reviewed experimental |

The builds and hashes in the [driver review](snap-tap-driver-review.md) identify the inspected API sources. Runtime admission requires trusted installed packages for the shared `RzCommon` service and the model-specific filter, with the reviewed typed API and topology. Compatible installed driver updates do not require a new build allowlist. Discovery selects the service and keyboard collection from the exact model profile. The experimental label describes pending physical SnapTap validation, independently of the laptop's other supported features.

Setup uses a separately pinned recipe for each model. A missing file, untrusted package, incomplete topology and unsupported model are distinct admission failures; the exported `OpenBlade.SnapTap.log` records the rejected check. Only a positive setup inspection exposes **Install driver**. Downloading Synapse alone does not establish that the required driver stack is installed.

## Driver implementation

For a failure report, export diagnostics while the failure is present. The
`snap-tap.json` entry retains the runtime state and terminal failure stage,
reason, numeric errors, redacted exception messages and stack locations, inner
and aggregate causes, and possible submission. `OpenBlade.SnapTap.log` records
supervisor failures. A driver-admitted entry establishes discovery and admission;
it does not confirm hook setup or active input handling. `diagnostic-export.json`
identifies missing, partial or failed log collection, and `diagnostic-inventory.json`
lists collected payload entries and limits. These records contain no keystrokes.

The Session Agent starts an authenticated current-user supervisor, which starts a separate input worker. Parent image, user, session, process lifetime, preference and coordinated shutdown checks apply to both helpers. A per-user/session mutex prevents concurrent supervisors. A failed helper stays stopped until an explicit off/on transition.

Admission requires both Windows host-identity sources to match one exact reviewed model, its PID-specific keyboard collection and control endpoint in the same device container, its sole reviewed keyboard upper filter, and trusted installed packages for the model filter and shared `RzCommon` driver. OpenBlade checks actual service images, signed INF hardware/service declarations and Windows catalog membership. Observed content fingerprints invalidate stale sessions; they are not a build allowlist. The active physical console session must own the worker. A competing mapping controller, non-console session or incomplete discovery rejects admission. New models and materially different API contracts need their own driver review. SKU is diagnostic metadata.

A single supervised driver worker owns the complete set requested by both features: A/D for SnapTap, the admitted function-row keys, or their union. Existing conflicting records prevent activation. It subscribes only after all selected canonical records match independent readback. The driver consumes those physical events, so the worker forwards each accepted original event or emits the typed action exactly once. Changing either driver selection stops and restores the complete old set before starting the new set. Windows-hook selection is explicit; failures never choose another backend automatically.

The supervisor opens its own unsubscribed driver file; it never duplicates the worker's subscribed file object. A non-inherited kill-on-close Windows job contains the worker before its startup gate opens. The worker also holds and checks its authenticated parent process handle. Heartbeats come from the input loop. A missed heartbeat triggers bounded cooperative stop, then termination of that exact child if needed. Driver I/O uses overlapped requests with bounded cancellation; buffers remain alive until completion or process termination.

Normal stop releases a confirmed owned winner in the same foreground, then closes the subscription. The supervisor proves child exit, enumerates the device-specific shared hook table, removes only canonical owned records, and verifies each removal while preserving unrelated records. It avoids the aggregate disable and global reset commands because those can release unrelated held output. First subscription rechecks keys and mouse buttons immediately before submission for the same reason.

Uncertain output stops further input writes. Closing the last subscribed file allows subsequent physical events through; cleanup does not prove that an earlier output key was released. Recovery reconciles hook configuration only and does not guess missing key releases. A durable ownership journal blocks restart after uncertain output or interrupted supervisor cleanup. Restarting Windows clears that recovery gate once fresh enumeration confirms no A/D hooks remain. Foreground/layout loss while a key is owned also stops the worker. A missing foreground window or layout while idle closes the subscription without consuming another packet. After confirmed child exit and verified hook cleanup, a fresh worker waits for a valid foreground and all keys released before subscribing again. Held or uncertain output, failed closure, changed ownership and failed cleanup never take this automatic recovery path.

## Source and verification

The API contract was established from `mapping_engine.dll` 1.3.16.5, SHA-256 `6EABDFDEDF797E042738B630D827C06F7A45DBE560EBAEC96C66698F88F3320A`, `RzCommon.sys` 1.0.0.76, SHA-256 `64B72EF9CE409BB17D31096C119B68C4A6A02AB35B7535D763157A4E1A587E59`, and `RzDev_02e0.sys` 1.0.0.78, SHA-256 `BC1666048D81A8DBA6BBFFDBDC86C2119632FD68EF2BB6D5D417FE3B84744787`. These versions and hashes identify inspected sources rather than a runtime build allowlist. Other filter source hashes, signed package provenance, Synapse mappings and callback ABI comparisons are recorded in the [driver review](snap-tap-driver-review.md). No vendor binary is bundled or loaded into OpenBlade.

The codecs include literal known-answer packets, exact response sizes, ignored undefined padding, fixed-pair validation and bounded enumeration. Hook-session tests exercise ordering, ownership, failure latching and subscription closure. Priority tests compare all 1,024 five-event sequences with an independent press-order oracle.

Attended validation on 2026-10-17 used version 0.34.0 at commit `53d044e6d67dc4308e5fbc0b3d9b4ffd16e09b51` on RZ09-0581. A normal-integrity validation launcher exercised the production driver loop and supervisor without installing a new build or changing the saved SnapTap preference. An initial A-hook configuration round trip matched readback and restored the empty hook table before input testing.

Both laptop-keyboard press orders passed: hold A, press and release D, release A; then hold D, press and release A, release D. The window and independent Raw Input observer each recorded 129 events, including repeats, with matching priority transitions, no delivered A+D overlap and no held key at the end. Raw Input attributed every event to the laptop keyboard collection. Normal stop exited successfully and restored the empty hook table; a separate read-only query confirmed it. Installed binaries, preferences and saved status were unchanged after restoration.

The pinned driver keeps hooks per device instance. Its IOCTL wrapper at RVA `0x1A10` obtains the queue's WDF device and typed context, then passes it to dispatch at `0x2E6C`; dispatch and input matching use that context's hook table at `+0x570`. Together with the exact keyboard-filter and container checks, this establishes laptop-only scope from source. No external keyboard was physically exercised.

Additional native checks confirmed that the production kill-on-close job terminates an idle child when its owner dies, and both real helper entrypoints exit cleanly with SnapTap off. These checks used no driver writes. Existing failure tests cover hook ownership, ambiguous submission, restart latching and the reboot-required recovery journal.

The observed laptop Raw Input attribution reaches the unchanged Reactive/Ripple input path; rendered lighting was not separately inspected. Input latency remains unmeasured. The complete enabled installed-agent startup and a live held-output or pending-I/O crash were not exercised.

## Driver installation

A separate elevated helper runs from the installed OpenBlade directory. It accepts one fixed action, with no package path or URL arguments. The input worker never installs drivers.

The helper requires an exact reviewed host and its keyboard/mouse topology, AC power, an active console session and no competing Razer controller. It checks that model's pinned archive and file hashes, Windows catalog signatures, and INF/SYS membership. The complete recipe includes the keyboard filter, mouse/control filter, USB lower filter, virtual consumer device and shared RzCommon driver. Four additional models download their pinned original Razer installers as data. Setup verifies each carrier and extracts only fixed, bounded driver blocks without running the installer. Their shared RzCommon files and notice come from the separately verified catalog archive; no other model's filter is extracted. RZ09-0328 instead uses a complete exact-model Microsoft Update Catalog CAB with its own reviewed 1.0.0.66 common/filter pair. Its signed U INFs pass current Windows policy; the older Razer-carrier legacy catalogs and uncovered U INFs are excluded. See the [0255 API review](snap-tap-0255-catalog-api-review.md). The mouse filter creates the control child; installing only the keyboard INF is insufficient.

Acquisition, each staged/bound INF and final readback stay tied to the same immutable model recipe. A changed recipe or uncertain operation stops installation. Readback confirms all five package bindings, expected services and filters, healthy device nodes, and the control/virtual children in their exact common container and ancestry. Existing unrecognized driver images and unrelated filters are preserved. Setup also refuses another device matching the shared control INF, because [PnPUtil applies /install to every matching device](https://learn.microsoft.com/en-us/windows-hardware/drivers/devtest/pnputil-command-syntax#add-driver). Disconnect other Razer control devices before setup.

An **Install driver** action requires a positive read-only setup inspection. Known missing bindings can qualify even when old driver files remain after Synapse removal. A catalog/signature rejection, unknown package failure or unrelated stack does not become installation authority. The original SnapTap failure remains in diagnostics.

An administrator-only journal records intent before each driver operation. Interrupted or uncertain operations block automatic replay, including after a restart; recovery follows the manual catalog procedure. The helper never forces removal or restarts Windows. See the [driver package procedure](razer-brightness-driver-package.md) for recovery and release validation.

Read-only checks verified the catalog inputs; automated tests cover recipe isolation, archive/catalog verification, independent readback and installer failure handling. Setup on every model except the previously reviewed RZ09-0581 remains source-backed experimental. Clean installation, reboot and recovery on a recoverable laptop remain pending. This validation made no driver changes.

## Windows Update and setup diagnostics

Windows Update is the first source on all seven reviewed models. The fixed elevated helper searches both automatic and optional driver offers using Windows' configured update source and policy. It does not change policy, disable Windows Update, stop services or install unrelated updates. It selects by the exact reviewed INF hardware/compatible IDs, provider and current target topology; display titles are not installation authority. Every current device matching those IDs is included, including generic common-control matches outside the usual instance naming pattern. Unrelated or ambiguous targets block installation.

The helper retains the actual searched update objects and identities through download and installation. A durable journal records each selected update ID, revision and target set before a fresh host, session, AC-power, controller, service and all-target check immediately preceding native submission. Asynchronous WUA jobs have bounded waits and request cancellation without claiming that an unfinished installation stopped. Busy, policy, search, download, metadata and license refusals retain their exact diagnostic details; they do not trigger an alternate installation path. Windows may still require a restart or offer no complete stack for a model.

When the source proves that installation never started, a canceled or blocked final check closes its pending intent so setup can be tried again. An earlier attempted installation, unexpected interruption or uncertain native call still requires recovery. Refused driver offers have separate guidance from Windows Update availability problems. After a successful installation result, setup permits up to 25 read-only verification retries at 200-millisecond intervals while Windows exposes the new devices. It never repeats an update during that wait; an unconfirmed result still stops with recovery required.

The pinned packages remain a fallback only after a successful initial search finds no matching offers and no Windows Update installation was attempted. Their existing source hashes, signatures, catalog membership, operation order and independent readback remain mandatory. A partial Windows Update install, cancellation, timeout, failed readback or uncertain result never falls through to pinned packages. A fully successful pass may reveal a missing common-control or virtual child; only a fresh admitted target set preserving every previous target permits another pass, bounded by the five reviewed INF roles. Previously submitted update identities are never replayed.

Setup verifies existing bound Razer packages even in a partial stack. Compatible installed updates use actual package/service binding and Windows catalog trust rather than a build-hash allowlist. Unknown orphan images are preserved; the previously reviewed exact-source inbox binding repair remains bounded to its established evidence. Final success requires independent topology and catalog-backed readback of DKM, keyboard, mouse, virtual and common roles. Windows Update's result alone is not proof that SnapTap is ready, and runtime admission remains independent.

The exported `OpenBlade.Tray.DriverSetup.log` records helper outcomes, selected update identities, overall and per-update results, restart requirements, and exact blocked stages. Inspection failures also retain their concrete cause in SnapTap's diagnostic chain. Ordinary topology logs omit unique device-instance suffixes and serials. The protected administrator journal preserves the full transaction evidence; an unfinished intent blocks automatic replay. Follow the setup guide after a requested restart or recovery result.

This path is covered by offline source, selection, transaction, failure and recovery tests. No Windows Update scan or driver installation was performed during development. Physical setup, restart and recovery remain unvalidated on the six additional models; the Windows Update delivery path also needs attended validation on RZ09-0581. Windows Update availability is not guaranteed for any model.
