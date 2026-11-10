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
