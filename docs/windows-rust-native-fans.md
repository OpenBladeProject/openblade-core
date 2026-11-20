# Native Windows fan slice

The optional `service-fan-mutation-v2` composition adds explicit linked
Fixed, Maximum and Automatic requests on the currently admitted native
RZ09-0581 / 1532:02E0 backend. The installed managed default is unchanged.
Other model catalogs do not grant this backend's actuation capability.

Fixed accepts 2,000–5,400 RPM in 100 RPM steps. Legacy Maximum retains its
own requested mode and resolves to 5,400 RPM. The 0D/01 target ACK proves
setter completion; 0D/82 independently proves mode and selector. Target RPM,
manual subtype, settled EC output and physical rotation are not readable here.
No 0D/81 getter or tachometer inference is added.

The codec, installed dispatcher and runtime use the existing serialized
ReadSource, Worker and Reader. Generic manual target changes send targets only.
Balanced and Performance entry preserve their admitted selectors; Performance
entry includes independent intermediate mode proof. An explicit Automatic
request that leaves saved manual fan intent goes directly to the saved
Balanced or Performance preset, as managed does: that preset's 0D/02 pair
carries the Automatic fan mode, so the stored performance intent is applied in
the same pair. Without such a preset, or with a saved GPU clock effect, it keeps
the observed selector. Owned Stop cleanup always keeps the owner's selector.
Automatic under the destination selector is a true no-op and never establishes
ownership. Custom, Silent, battery and delegated states
store intent with a zero-setter pending software outcome until their separate
native recipes exist. No fallback profile or background control loop is added.

Every setter has fresh exact-device, caller/session, controller, lifecycle and
decoded 07/8C source admission. State-channel, state-to-target and target-channel
spacing remain at least 10, 210 and 65 ms, alongside native feature settling.
The complete recipe and all command IDs are frozen before Prepared. Ordinary
preflight consumes four IDs. Remaining target-only, Balanced-entry,
Performance-entry and direct-Automatic budgets are 6, 10, 12 and 6 IDs.
Before any manual setter, another ten never-reused IDs are retained for normal
Stop's fresh reads, checks, direct Auto pair and independent final proof.
Ordinary observations cannot consume these retained IDs. The finite process ID
budget remains an unresolved production limitation; no reset or refill exists.

The fan history contract is `service-fan-mutation-v2/rz09-0581-linked-v1`.
It binds requested intent, complete selected recipe, source, selector, ordinary
and cleanup IDs to the original producer/request/user/session. Existing v1
performance rows, numeric meanings and raw commitments remain unchanged.
Fan progress uses 0 for Prepared, 10–17 for indexed setter intent/ACK, 30 for
mode-proven manual ownership, 31 for owned release preparation, 40–43 for
release intent/ACK and 50 for independent Automatic proof. Numeric 6 remains
Prepared-only KnownNotSubmitted after positive native retirement. Progress 30
completes the explicit request while retaining its firmware handback obligation.
Transport retirement alone cannot settle that obligation. Partial/future/foreign
contracts remain fenced. Original cleanup may resume after a later fully retired
Prepared-only request, using only its exact original record and retained IDs.
A partial later submission cannot authorize that resumption.

Normal Stop immediately denies ordinary writes, joins active original work,
then permits one bounded direct Auto release of the positively established
in-process owner. Expected Stop denies ordinary reads without discarding retained
custody. Unexpected origin/trust/epoch loss never becomes cleanup authority.
The established owner is kept separately from a candidate request, so a refused
identical request cannot discard its cleanup budget. Metadata-only settlement
can append only exact Prepared-to-KnownNotSubmitted evidence and grants no
firmware authority. Final result admission is separate from permission to submit
another command. Cleanup failure still permits mandatory resource retirement
and keeps firmware settlement unresolved. Fixed/Maximum sleep policy is unchanged.

Protected storage reserves the complete fan branch and retained prior cleanup
capacity before Prepared. The Rust and managed readers share canonical vectors,
original-owner/producer joins, global submission fences and history commitments.
The compatible rollback reader must ship first; an older incompatible reader
refuses new rows without deleting evidence. The new signed producer capability
is required; the original performance-only composition remains supported.

Validation for this slice is offline source, cached compilation and explicitly
injected/private-root tests. No installed-native execution, provider execution,
hardware observation, installation or sleep/resume test establishes this slice.
Curves, Stop guardian, duty control, cooling pad, broad automation, other models,
full Windows replacement and coordinated runtime/package cutover remain later work.
