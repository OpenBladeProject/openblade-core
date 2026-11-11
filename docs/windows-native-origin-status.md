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

The installed Service exposes real metadata GetStatus and SubscribeStatus.
ObserveState returns a bounded refusal because full-adapter, active/unlocked
session and coordinated managed-drain admission are not implemented. Metadata
status remains useful on unsupported hosts, battery, controller conflict and HID
read denial. No verified/write-capable fields or fresh performance are invented.
The existing exact RZ09-0581 1532:02E0 usage1/2 0D/82 getter is unchanged and is not
acquired by this composition. Next supplier work must reuse the existing Rust
07/8C query/decoder/exchange after parity with the canonical C# fixtures.

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
managed handoff never becomes a first retirement certificate. Missing suppliers
keep acquisition denied. A failed or uncertain worker drain cannot release an
owner fence on Drop. These are software contracts, not live native ownership proof.

Guarded shutdown retains cleanup proof after origin revocation. Only successful
listener and metadata-worker drain writes a status-drain acknowledgement. Setup
binds it to the retained original Service process, independently refuses unresolved
firmware-owner history, then retires package selection. Enrollment replacement or
removal refuses active or inconsistent selection/history before changing private
trust. Metadata Retired and status drain are not firmware-drain certificates.

Offline tests use pure algorithms, inert native-operation adapters and private
current-token filesystem fixtures. Native launch, standard-user access, SCM,
process/token/pipe semantics and native kernel behavior remain compiled-only
controlled gates. Full 51 Service requests, 17 Agent forms, background lifecycle,
model/backend parity, default packaging and cutover remain subsequent work.
