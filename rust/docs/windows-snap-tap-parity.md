# Windows SnapTap Rust replay

The reference is OpenBlade commit `d6b003ca50800310b3ae79611a0bdf147a14138d`.
The port follows `SnapTapWindowsHookLoop.InputState`, the surrounding callback
and output gates, `SnapTapPriorityCore`, and the Windows admission rules in
`RazerSnapTapDriverProfiles`. The shared trace under
`tests/Fixtures/Parity/SnapTap` runs against the managed state and the Rust
policy. The managed repeat tests use a manual clock; the Rust tests provide
monotonic timestamps and a sink that only records typed batches.

The fixture represents registry, console, controller, release, context, and
journal evidence as observations. `Off` means the policy finished its owned
output cleanup; it does not establish successful hook or timer retirement or
permit deletion of a real ownership marker. The Rust output seam takes a fresh
observation before each batch. It preserves the first failure, poisons the
session after a short or uncertain submission, and never retries that batch or
sends a guessed release. Function-row queue effects have a different ownership
and dispatch model; SnapTap makes its forward-or-suppress decision synchronously.

Native activation still needs the managed helper's selection and supervision,
exact registry and console observations, key release sampling, and durable
journal acquisition and recovery. It also needs a bounded hook/message/timer
adapter, immediate pre-submission context checks, tagged scan-code output, and
verified retirement and status publishing before the journal can be cleared.
Native testing must separately establish the delivered event order, raw device
identity, physical typematic takeover, and recovery on the exact supported
Windows path. This slice has no production caller or native implementation.
