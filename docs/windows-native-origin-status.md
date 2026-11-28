# Native Windows origin and status integration

The managed Windows Service and Session Agent remain the default. The internal
`OpenBladeNativeStatusIntegration=true` Setup build also requires
`OpenBladeLocalDevDeployment=true`. It selects the original authenticated active
LocalDev package through the broker producer; it accepts no executable path or
signing key from the command line. No Stable trust root is established.

Setup publishes two native images and a minimal public verification projection
under a fresh `%ProgramData%\OpenBlade.NativeEpochs/<epoch>/app` namespace.
The root has protected SYSTEM/Administrators full control and Users read/execute.
The private OpenBlade machine-data parent, broker and token DACLs stay private.
An existing old NativeEpochs store denies automatic transition, including retired
history; its mappings and unresolved state are preserved. No relocation or
adoption is performed. Package v2 binds the projection digest and static-CRT
system-import closure; original signed package, full role digests and per-artifact
compiled identities are independently checked. Previous v1 mapping records remain
part of conflict detection when present in an explicitly admitted store.

Both native mains acquire their actual main module, compiled role record,
authenticated selected package, token/session and original process object before
Service dispatch or Agent monitor construction. The guarantee relies on trusted
privileged installer and SCM configuration writers maintaining non-reused protected
namespaces. Arbitrary malicious Administrator/SYSTEM replacement is outside that
TCB guarantee. No undocumented loaded image file-object API is assumed.

Committed boot needs no live Setup process. Provisional Service readiness retains
the original privileged Setup process bound by the epoch's startup-owner digest;
it grants no firmware admission. The only allowed live phase promotion is that
exact attempt becoming committed. Failed/unknown observations and delivered
relevant SCM callbacks latch loss. This does not detect transients that were never
reported or sampled. StartPending may omit its PID only while the independently
captured own-process origin is retained; readonly peers require Running/nonzero PID.

The installed Service exposes real metadata GetStatus and SubscribeStatus. The
separate `service-mailbox-observation-v1` composition connects ObserveState to the
existing serialized exact RZ09-0581 1532:02E0 usage1/2 paired 0D/82 getter. It
requires the original generation, exclusive read custody, known-empty native
owner history and authenticated managed transport handoff. Its compiled
capability is `service-mailbox-observation-v1`; it cannot claim metadata-only
capability or mutation authority. The default managed product and the separate
metadata-only composition remain valid.

Read evidence is distinct from IPC caller admission and write admission. The
getter requires neither Full adapter power nor a nonzero UI session. Ordinary
tray callers still pass the existing active-local-interactive IPC check. A
private, nonserialized fresh-read witness is checked before returning a fresh
ObserveState result. GetStatus/subscriptions carry retained presentation only;
they cannot mint that witness. Write capabilities remain absent and the existing
strong ConfirmedPerformance meaning for Agent/firmware consumers is unchanged.
Use no-live-sensors requests when validating fresh paired observation. Direct
completion across a low-power transition remains physically unvalidated.

The shared C#/Rust fixture covers Balanced, Performance, Silent, BatterySaver,
BatteryBalanced, their admitted manual forms, FixedMaximum and HyperBoost.
FixedMaximum and HyperBoost retain an unknown preset; only HyperBoost reports
its confirmed active flag. No fan target/RPM is inferred. Custom remains
incomplete until actual CPU/GPU 0D/87 getters exist. Unknown/channel-mismatched
responses retain poisoning/no-replay semantics. This bounded getter does not
replace the whole managed ObserveAsync power/controller/reconciliation path.

The status-only Agent monitor retains a separately verified SCM own-process,
autostart, LocalSystem, exactly quoted selected Service path and one original
limited-query+synchronize process object. It checks package/SCM/process identity,
the connected pipe PID/session before and after exchanges/events, and a duplicate
of that same pipe object during bounded idle checks. It never queries a System
token, elevates, implements firmware Authority or grants fresh observation.

Firmware owner history lives in a private owner directory outside replaceable
epochs. Canonical append-only Reserved/Active/Retiring/Retired records bind the
predecessor, installation, artifact, original process and nonce. Exclusive custody
and flush/readback precede publication; partial records remain unresolved. Missing
managed handoff never becomes a first retirement certificate. A typed read lease
can hold a genuinely known-empty history without appending a synthetic Retired
record or obtaining write reservation. A failed or uncertain worker drain
quarantines custody on Drop. These are software contracts, not live native
ownership proof. Interrupted bootstrap repair is limited to the authenticated
never-selected root and empty owner directory before its original lock exists;
selected, partial and unknown history remain fenced.

Guarded shutdown retains cleanup proof after origin revocation. Metadata-only
composition writes its original status drain; the read composition requires
actual native worker/callback retirement and a distinct transport drain. Setup
binds it to the retained original Service process, independently refuses unresolved
firmware-owner history, then retires package selection. Enrollment replacement or
removal refuses active or inconsistent selection/history before changing private
trust. Metadata Retired and status drain are not firmware-drain certificates.
Enrollment replacement/removal retains managed and legacy exclusion plus native
custody proof throughout maintenance, refusing active signed ownership or an
unresolved transfer. First enrollment has a separate protected creation mutex
and positively unenrolled admission, so an unenrolled default Service may keep
running. It never stops that Service automatically.

## Managed upgrade prerequisite

An old, uninstrumented Service cannot be certified retrospectively. Before native
read cutover, a later authorized rollout must install and run this handoff-capable
managed build from an enrolled, signed standard LocalDev package. During that
managed upgrade the Service authenticates the broker's original signed package,
verifies its actual resolved Service dependency closure and loaded module IDs,
and preserves the original package/signature/enrollment binding in protected
`%ProgramData%\OpenBlade\ManagedHandoff` storage under the installer-protected
namespace before broker staging disappears. A new authenticated run revalidates
its current enrollment. Legacy storage remains unchanged in its old namespace. Self-reported hashes, copied markers,
SCM Stopped and process exit cannot replace this prerequisite. Without enrollment,
ordinary managed operation continues but cannot issue a transferable receipt.

One shared scope reserves before actual CreateFile/native submission and follows
control/battery, fan-RPM, retained lighting/keyboard and matrix workers, handles,
deferred disposal and late callbacks. After producers and guarded cleanup finish,
remaining legitimate owner close runs before final admission sealing. Its actual
retirement task is separate from bounded disposal waiting and stays rooted on
timeout or failure. Shutdown waits within the caller's remaining budget for that
task and successful terminal resources.
Full external Fan Stop Guardian exclusion is checked independently. Fault,
timeout or unknown guardian custody prevents the protected create-once receipt.
Stored/uncertain firmware settings alone do not prevent a transport-only receipt;
that receipt makes no claim of Automatic fans, Normal keyboard or write authority.

Setup retains the actual original process before requesting stop, authenticates
its signed managed build, and requires a bounded cancellable monotonic wait for
that original's exit plus its protected receipt. Installed-tree pins are released only then, permitting activation.
StagePayload -> PrepareService/stop -> StopAgent -> Activate ordering is preserved.
Before startup submission, Begin records original custody, policy and origin,
then binds the exact receipt/request/package through immutable per-request intent
to one epoch. Partial mapping/terminal publication can finish only for those same
bytes under authenticated custody. The native read factory retains exclusive
transfer through worker and native callback retirement. Positive terminal
resolution permits a new authenticated managed run, new original process and
new request/receipt. Unknown history, receipt reuse and ambiguous submission
remain fenced. Intact ffc legacy mappings use a separately pinned historical
exit-contract proof; they are never remapped.

A valid-origin attempt that never acquired a reader can keep diagnostic GetStatus
and subscriptions. It certifies only its own transport work on stop. Read
revocation remains sticky while independent origin permits metadata; real origin
loss remains fatal. Both caller projections retain last confirmed thermal
presentation for five monotonic seconds from getter completion, using current
metadata and no fresh-read or write authority. At expiry those values become
unavailable. See [mailbox corrections](windows-rust-mailbox-corrections.md).

Offline tests use pure algorithms, inert native-operation adapters and private
current-token filesystem fixtures. Native launch, standard-user access, SCM,
process/token/pipe semantics and native kernel behavior remain compiled-only
controlled gates. Full 51 Service requests, 17 Agent forms, background lifecycle,
model/backend parity, default packaging and cutover remain subsequent work.
Specifically unfinished are Custom 0D/87, adapter/power suppliers, thermal writes,
automation/recovery, configuration/profiles/durable journals, other models and
optional driver/audio/display/lighting/input/CPU backends. Linux, macOS and SwiftUI
remain deferred. No installed role, process/token/pipe/SCM/task probe, hardware,
VM or sleep/resume validation was executed in this stage. Held compiled bytes and
injected tests are offline evidence; default native cutover remains blocked.
