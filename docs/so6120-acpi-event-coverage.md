# SO6120 ACPI Event 7 coverage

The inspected Windows ACPI driver emits Event 7 when it creates and destroys an
**evaluation context**. It does not emit a separate Event 7 for each nested AML
method. A clean trace can therefore lower concern about direct external
`TBCC`/`ECRR` evaluations during the exercised conditions, but name absence alone
cannot exclude these methods running inside another evaluation.

This is offline evidence for interpreting a bounded observation on SO6120 BIOS
4.01. It authorizes no AML evaluation, hardware request, or direct-control write.
It is specific to the inspected Windows driver image, not a Windows-wide contract.

## Source and reproducibility

- ACPI.sys: 884,088 bytes, SHA-256
  `5de94c7819c690178fef3d656e4f743b2c46bfd58d6fd981e8bdfe7efa3d3022`.
- Provider: Microsoft-Windows-Kernel-Acpi,
  `{c514638f-7723-485b-bcfc-96565d735d4a}`.
- Event descriptor: ID 7, version 0, channel 16, level 4, opcode 0, task 105,
  keywords `0x8000200000000001`. The complete descriptor at RVA `0x75D78`
  matches [the saved metadata](../tools/mailbox-observation/event7-metadata.json).
  Its file offset is also `0x75D78` in this image.
- Ghidra 12.1.3 decoded the copied x64 PE as data. No image execution or PDB
  symbols were used. Function names below are addresses, not recovered symbols.
- Ignored local evidence is under `artifacts/mailbox-emitter-20261106/`:
  `input-and-evidence-pins.json`, `critical-instructions.txt`,
  `initial-refs.txt`, and the `initial`, `paths`, and `verify` selected listings.
  The evidence pack checks decoded instruction bytes against the pinned file.

All addresses below are **RVAs**. Add preferred image base `0x140000000` to
compare with the local listings. Only the two stated data locations have their
file offsets asserted here; other RVAs must be mapped through PE sections.

## Emitter and filters

Registration at `0xC63CA–0xC63E2` supplies the GUID at `0x76A60` to imported
`EtwRegister`, storing the handle at `0x8B450`. The GUID bytes were independently
checked against the saved provider identity.

Ghidra finds four decoded references to the Event 7 descriptor: `0x1241F` and
`0x1283C` in context creation (`0x120D0`), and `0xB07D` and `0xB345` in context
cleanup (`0xAF70`). These are concrete references, not a claim that no indirect
reference could exist elsewhere.

Both sites require a nonzero provider handle and a successful
`EtwEventEnabled(handle, descriptor)` check. Creation checks at `0x1240F–0x12434`
and calls imported `EtwWrite` at `0x128B1`; cleanup checks at `0xB064–0xB098` and
writes at `0xB375`. The Event 7 branches have no additional method-name or
elapsed-time threshold. Nearby frequent/slow-method telemetry is a separate
path and must not be mistaken for an Event 7 filter. The provider/session's
actual level and keyword enablement still matters, and `EtwWrite` results are
not checked by these sites.

Creation sends four payload descriptors: the root name length, root name,
state **1**, and elapsed value **0**. Cleanup sends the same saved root name,
state **0**, and an elapsed value. Neither event contains a parent-frame
identifier or nested call path. The event header's thread/activity fields do
not encode nested method names or nested frame identity.

The name is built from the initial namespace object through `0x12D50` and saved
in context fields `+0x1B8`/`+0x1C0`. Both emitters read these fields; the finish
name is not the last nested method. Name allocation/conversion failure can
leave an empty name while evaluation continues. Unnamed activity cannot support
a claim that named targets were absent.

Start emission precedes interpreter method-frame setup and its argument
validation; the external request parser may already have validated the request.
Finish is context cleanup, not proof of successful AML execution. Elapsed time is based
on the saved interrupt-time timestamp and division by 10,000; timestamp setup
at `0x12B5E–0x12B68` is conditional on an auxiliary allocation. Treat elapsed
values cautiously if that setup failed. Windows documents interrupt time in
100-nanosecond units in
[KUSER_SHARED_DATA](https://learn.microsoft.com/en-us/windows-hardware/drivers/ddi/ntddk/ns-ntddk-kuser_shared_data).

## Concrete entry paths and the nested blind spot

| Path in this image | Instruction-backed route | Event 7 scope |
| --- | --- | --- |
| Synchronous ordinary / EX evaluation, control codes `0x32C004` / `0x32C018` | Dispatcher `0x101A0` calls `0x509E4` at `0x10519`; ordinary/EX parsers at `0x50B56` / `0x50B21`; evaluation at `0x50B81 → 0x14164 → 0x143C8` | Creates a traced context if this thread has no existing context; reuse is different. |
| Asynchronous ordinary / EX routes, control codes `0x32C000` / `0x32C01C` | Dispatcher selects the ordinary parser at `0x10456` or EX parser at `0x10292`, then `0x102CC → 0x10934`; normal evaluation calls `0x120D0` at `0x10A04` | The normal new-context evaluation path reaches the same emitter. Validation failure or interception before it does not establish an AML invocation. |
| EC query dispatch | `0x1F4A0` builds `_Qxx` at `0x1F4DA`, resolves the method at `0x1F4E0`, and calls `0x10934` at `0x1F509` | A resolved query entering normal evaluation gets its root `_Qxx` name. Calls inside that query are not individually named. |
| Nested AML method invocation | Interpreter `0xE5F0` resolves a method object (type 8) and calls frame allocator `0x13020` directly at `0xF569`, with the existing context | Bypasses context creation and its Event 7 start. No corresponding separate nested-name finish is supplied by the context cleanup emitter. |
| Reentrant synchronous evaluation | Existing-thread context lookup `0x147AC`; branch at `0x144AF–0x144B2`; admitted reuse calls `0x6D37C` at `0x146E9`, then `0x13020` at `0x6D49A` | Reuses the context without a new Event 7 for the requested method. Some reuse conditions reject evaluation instead. |

The current-thread lookup is instruction-backed: `0x147AC` reads `GS:[0x188]`
and `0x147DC` compares that value against context field `+0x198` while walking
the active-context list. The no-context branch calls `0x120D0` at `0x145AA`.
This establishes why ordinary new external requests and reentrant requests
cannot be assigned identical coverage.

The asynchronous rows use the control codes and parsers actually present in
this binary; they do not assert undocumented API availability to applications.
Microsoft documents ordinary and EX synchronous driver evaluation through
ACPI.sys, including their different namespace addressing, in
[Evaluating ACPI control methods synchronously](https://learn.microsoft.com/en-us/windows-hardware/drivers/acpi/evaluating-acpi-control-methods-synchronously).

## Meaning for a finite caller challenge

The [earlier passive observation](so6120-mailbox-runtime-observation.md) captured
112 Event 7 records (56 starts and 56 finishes) for 16 method names, including
EC query activity, with no `TBCC` or `ECRR` name and no reported event/buffer
loss. This proves that normal root evaluation activity was instrumented during
that window. It does not make nested methods visible. The instruction audit
explains that limitation rather than requiring another quiet trace to infer it.

For a finite set of normal operations, a trace with the exact provider enabled,
relevant root activity, usable names, and no reported collection loss can
support **no observed new-context evaluation of these targets under the tested
conditions**. This lowers the practical likelihood of an ordinary external
caller competing during the same attended scenario. It is not statistical proof
about untested states or proof that every attempted event reached the trace.

The remaining nested/reentrant scope should be handled explicitly: combine
observed root names with the reviewed AML caller graph and the exact installed
client/DSP evidence described in
[the ACPI/vendor transport review](so6120-acpi-vendor-transport.md). A root with
an unknown descendant path remains unresolved; a start/finish pair alone cannot
clear it. No `TBCC` caller in the captured AML set and no target literal in a
bounded client catalog are scoped negative evidence, not global unreachability.

A target-name hit or a reviewed root reaching the target identifies a possible
competing path; a hit or conditional root path alone does not prove port use or
a collision. Missing relevant root activity, unnamed events, loss, or a changed driver image
makes that observation inconclusive for its intended scope. If a finite caller
challenge is clean and its exercised roots have been reviewed, the remaining
possibility can be recorded as a low practical concern for that attended
scenario, with nested/reentrant and untested-state residuals stated. This is a
bounded risk judgment, not hardware admission or a waiver of separate ownership,
fresh per-write checks, readback, restoration, and stop-on-ambiguity requirements.
