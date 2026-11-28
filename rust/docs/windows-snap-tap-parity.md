# Windows SnapTap Rust replay

The policy replay uses OpenBlade commit
`d6b003ca50800310b3ae79611a0bdf147a14138d` as its reference. It follows
`SnapTapWindowsHookLoop.InputState`, `SnapTapPriorityCore`, and the Windows
admission rules in `RazerSnapTapDriverProfiles`. The shared trace under
`tests/Fixtures/Parity/SnapTap` runs against the managed state and Rust policy.

## Windows adapter

The adapter starts from policy commit
`93d2e01a49356f94e5449ef450eb293e9a5a2ffd`. Its managed boundary is
`src/OpenBlade.SessionAgent/SnapTapWindowsHookLoop.cs:239–386` at that commit.
Active-console identity follows
`src/OpenBlade.SessionAgent/RazerSnapTapDriverAdmission.cs:477–482`.

The owner thread primes its message queue, installs the low-level hook, and
sets a 10 ms thread timer. The callback lends the policy synchronous access
during message retrieval and dispatch. Negative hook codes, missing packets,
non-A/D scans, extended packets, and both injected flags go straight to the
next hook with their original arguments. The adapter skips session and focus
sampling for this traffic. The pump and the admitted A/D callback still enforce
admission. A fresh winner press reaches the next hook after the old winner's
typed release. A winner release that restores a held key is suppressed only
after the paired batch succeeds. The timer checks the active foreground,
layout, release state, and admission, and only its own timer ID can drive
typematic output.

The Win32 sink takes the policy's fresh console, stop, and focus/layout
observation immediately before submitting a tagged scan-code batch. It passes
the exact SendInput insertion count back to the policy. Zero, short, and
ambiguous outcomes poison the session. There is no replay or guessed cleanup.
A pump error ends output before teardown. While cancellation is pending, a
forwarded physical release of the winner relinquishes that press.
On exit, one confirmed winner may be released only with a fresh admitted
snapshot and matching foreground and layout. Hook and timer teardown are
attempted separately after the callback lease ends. The retirement report keeps
each error, and policy `Off` alone cannot prove clean retirement.

## Activation gates

The managed helper remains the product path. The native adapter requires an
isolated, supervised helper that implements all of the following before it can
be selected:

- Bind startup to the user's explicit fixed A/D Windows SnapTap selection.
  Supply exact matching Razer registry identities from both sources and a
  complete competing-controller observation at startup and on refresh. The
  Win32 facade supplies the active-console check and current focus/layout.
- Prove driver ownership is clear and durably acquire the Windows ownership
  marker before starting. Bind the marker to the owner and boot, and preserve
  it on a crash, uncertain output, retirement error, or failed publication.
- Poll stop and supervision loss without blocking the callback. Publish live
  and terminal status through the supervised lifecycle. Confirm worker exit,
  policy cleanup, unhook, and timer retirement before completing the journal.
  A failed unhook requires retiring the helper without reusing its thread.
- Verify live callback and SendInput order, preservation of the forwarded
  physical event, delivery during nested callbacks and reconciliation,
  Windows typematic takeover, cancellation and session
  transitions, and recovery on the exact supported Windows path. The hook is
  global across keyboards and cannot attribute input to an individual device.

The adapter tests use an inert Windows facade. The Win32 code is build-only
for this slice.

## Private Windows supervisor fixture

The supervisor starts from adapter commit
`d0edc940baf0c3eb84e82610be7c4dba0642be81` (parent
`93d2e01a49356f94e5449ef450eb293e9a5a2ffd`). At the adapter commit, the
managed reference is `SnapTapInputHost` (`--snap-tap-input <parent> windows-hook`),
`SnapTapInputProcess`, `SnapTapProcessCoordinator` and
`SnapTapWindowsHookLoop` in `src/OpenBlade.SessionAgent`. The managed coordinator
tracks a child separately from its status, binds terminal failure to PID and
launch time, and publishes the result before clearing ownership.

The Windows test harness starts an inert child of the same executable only
for an explicit Rust fixed A/D selection. Injected current-user, session,
console, exact-model, controller and ownership evidence is checked before
spawn and again at the start gate. A private pipe scopes authorization to
that child. Closing it requests cancellation or signals loss of the parent.
The child runs the existing adapter with fake messages and output. The
supervisor observes exit on its retained process handle and requires a
matching receipt from that child's pipe. Quiescence requires requested
stop, policy Off, and successful hook and timer retirement. A rejected
start has a separate no-write receipt. Uncertain output, missing or
mismatched evidence, stop timeout and publication failure block handoff.

The fixture publishes through an injected callback while it still owns the
child. The product still selects the managed helper. A production Rust host
needs independent Windows token, session and image checks, live admission
and cancellation wiring, a durable boot- and owner-bound journal shared with
the managed handoff guards, and integration with the managed live and
terminal status stores. Physical input behavior remains a separate gate.

Run the fixture on Windows with
`cargo test --locked --offline -p openblade-platform-windows supervisor`
from the Rust workspace.

## Private supervisor restart boundary

The restart fixture is based on `a4cc57c8f058393d69f6f13c0994185fc6944849`.
The managed references below are pinned to that commit under
`src/OpenBlade.SessionAgent`.

| Managed reference | Contract |
| --- | --- |
| `SnapTapInputHost.cs:98–211` | Rechecks preferences, validates the parent, observes stop, shutdown and parent exit, and checks competing keyboard ownership before selecting the Windows helper. |
| `SnapTapWindowsHookLoop.cs:44–85, 251–328` | Admits the exact host and active console, acquires a marker before the hook loop, and explicitly completes the marker on clean return. Shutdown attempts only the confirmed output release in the original focus and layout, then retires the native hook and timer. |
| `SnapTapWindowsOwnershipJournal.cs:11–96, 131–151` | Exclusively creates and flushes a boot-bound marker with PID, start time, helper version and executable path hash. Clean completion checks the exact marker before deleting it. A surviving valid marker requires recovery in the same boot. A separately validated later boot may discard it. |
| `SnapTapProcessCoordinator.cs:155–205, 325–385` | Observes exit separately, matches terminal status to the launched child, publishes before clearing ownership, and retains recovery on ambiguous cleanup or a stop timeout. |
| `SnapTapDriverSupervisor.cs:130–249, 280–331` | The Razer driver path observes worker exit and independently reconciles its owned records against a driver hook baseline before completing its own journal. |

The fixture uses one exclusive, size-bounded, append-only file in trusted
scratch storage. Explicit creation and reopening are separate operations.
Before spawning it flushes a reservation for the modeled generation. Before
sending the gate byte it flushes the child binding and activation intent.
The generation and PID bind receipts to one launch in this journal. The
named inert worker, private pipe and retained process handle supply the
remaining fixture identity. Reopening rejects malformed or conflicting
history, including a file with only its header, and refuses to recreate a
missing file. It requires a fresh successful
flush before trusting a complete persisted terminal record.

A gate cancellation needs the exact child's no-write receipt and independently
observed successful exit. An activated child needs requested stop, confirmed
policy Off, successful hook and timer retirement, and independently observed
successful exit. The supervisor publishes while it still owns the child and
flushes the terminal record before relinquishing ownership. Only those terminal
records admit a strictly newer generation. Timeout, uncertain output,
retirement errors, failed status publication and loss of the supervisor leave
an unresolved reservation. A later receipt or exit cannot complete it. This
intentionally models the conservative same-boot boundary without importing
the managed later-boot discard rule or Razer driver restoration.

The scratch tests cover reopen after modeled supervisor loss, successful
pre-activation cancellation and post-activation retirement, same-generation
reentry, missing or conflicting evidence, timeout and failed publication. The
managed product path remains the default. Production activation still requires
real boot, token, session, image and host identity, the shared managed
ownership and status integration, review of protected storage and abrupt
host/power loss, and live native input and recovery validation.
