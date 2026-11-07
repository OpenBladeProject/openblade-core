# Stop/Auto guardian review

Claude Opus 5.5 reviewed the frozen working-tree implementation on 2026-11-07
using the restricted read-only native review workflow. The review completed;
Codex checked its ten findings against the code and made the corrections below.
The review snapshot preceded these corrections and the final tests; it is not
an independent review of the final patched tree.

| Finding | Disposition and correction |
| --- | --- |
| Clean entry refusal incorrectly retained ownership forever | Confirmed. A resolved refusal now releases only with explicit zero-mutation, no-pending-command evidence and verified native closure. |
| Early command-start flag incorrectly implied uncertain mutation | Confirmed in part. Separate possible, completed, mutating, pending and ambiguous command evidence replaces the coarse flag. Native HID ambiguity still blocks release. |
| Any failed power read prevented Auto | Confirmed for a complete but unrecognized typed reply: it now requests Auto. Rejected for unresolved native I/O: another command would violate stop-on-ambiguity. |
| Completed developer marker permanently blocked entry | Confirmed lifecycle gap. An explicit offline tool validates the pinned reviewed journal, native close and independent exits, then can archive only that matching marker under exclusion. Product acquisition never bypasses an existing marker. |
| Heartbeats called mutable runtime admission outside its gate | Confirmed. Heartbeats use a separate immutable snapshot/volatile-flag callback; they perform no HID or OS query and cannot race ordinary controls after lease release. |
| Continuous monitoring repeatedly collected expensive immutable data | Confirmed source overhead; its actual runtime cost was not measured. Cheap continuation checks are sampled once per second. Fresh command admission remains; only pinned driver bytes and the boot EC version are cached. |
| Publishing the helper into the service output could replace shared binaries | Confirmed overwrite risk. Publish the helper separately, require identical shared-file hashes, and copy only its four unique runtime files. A reference to the service assembly alone was not established as a defect. |
| Capture and product duplicated native pin/open code | Confirmed. Both now use the same bounded native acquisition implementation. |
| Product precheck accepted unsupported initial power sources | Confirmed. Entry explicitly requires resolved USB-C; a clean refusal does not become unresolved recovery. The generic downward-power policy remains available after entry. |
| Startup heartbeat timing could expire before connection | Confirmed. A bounded ten-second first-heartbeat grace precedes the normal five-second lease. Capture's existing default is unchanged. |

Additional checks found a missing capability projection and the distinction
between zero PWM and zero tach. The service now projects exact-device backend
availability into `SupportsFanStop`. The owner performs bounded coast observations
before publishing Stopped; continued rotation requests Auto, while ambiguity
preserves the ownership fence. Setup also refuses to replace or remove an active
guardian and its unfinished records.

Focused regressions cover refusal/release, ambiguous native operations, startup
timing, coast confirmation, power classification, immutable heartbeats, restart
fencing, setup preservation and non-overwriting payload assembly. The disconnected
UI render covers Starting, Returning to Auto and guardian-unavailable states.
The installed Session-0 path and real downward power transitions still need
attended physical validation. This implementation performed no installation or
hardware action.

Review provenance: wrapper 8.1.1, model `claude-opus-5-5`, high effort, native
review completed successfully with 48 read/search calls and no unexpected tool
calls. Run ID `9548f88280ca4d37a2f07b440b0a6ede`; Claude session
`9ea28213-89f6-4042-a70d-c6513a6dcf5c`. Frozen source HEAD:
`304811314cd69e201f8ea1b824f40e733cacde03`. Working-tree patch SHA-256:
`1f40d890d2bb58cac6eb44a654ea46b15e373b72cb0ba1c027c85daeefd8208b`.
The snapshot contained 88 changed files and 324,434 patch bytes. The local
review directory is named `so6120-stop-auto-guardian-review-20261107-v1` in the
operator's temporary directory; raw local metadata is not committed.
