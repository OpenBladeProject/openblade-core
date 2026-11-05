# SO6120 mailbox runtime observation

The 2026-11-05 investigation adds passive Windows observations to the
[mailbox contract](so6120-host-mailbox-contract.md) and
[ACPI transport analysis](so6120-acpi-vendor-transport.md). It does not yet
establish an exclusive interval for a direct-PWM experiment.

The two investigations ran in parallel: possible runtime callers of
`ECRR/TBCC`, and actual users of the existing InpOut port driver. The diagnostics
did not evaluate firmware methods or issue port commands. Normal Windows AML
activity continued during passive recording. The observations did not change
profiles, fan settings, service configuration or installed drivers. Two
separately authorized administrator recordings used UAC; their helpers did
not adjust token privileges.

## ACPI callers and the installed IPF catalog

The pinned DSDT defines `EC0.TBCC -> ECRR(0x80B82, 1)`. Inside `TBCC`, the
call precedes its returned-data and flag checks; those later checks do not
prevent entry to the mailbox. The prior copied-table search found no caller
of `TBCC`. That does not exclude an external driver evaluation: Windows
supports [ACPI method evaluation through a device stack](https://learn.microsoft.com/en-us/windows-hardware/drivers/acpi/evaluating-acpi-control-methods-synchronously).

The running `ACPI`, `ipf_cpu`, `ipf_acpi`, `ipfsvc` and `dptftcs` registrations
were confirmed through read-only Windows metadata. The installed DSP catalog
still matches the prior 778,547-byte input, SHA-256
`86dc900633399b2cfcbf678f535f9887a3301c99492908df67a8cac88ee634bc`.
All 35 record hashes and compact primitive/action bounds were checked. The
catalog has 2,185 primitives and 360 ACPI actions, with no literal `TBCC` or
`ECRR` anywhere in its ASCII or UTF-16LE bytes.

This excludes direct literal selectors in that exact installed catalog. It
does not establish the active in-memory catalog, exclude constructed method
names or intermediate calls, or enumerate all external evaluators. The EC
devnode's registration exposes no per-device service or filters in the queried
properties; that is not a complete runtime namespace or caller inventory.

## One passive method trace

A separate, uniquely named ETW session collected diagnostic keyword `1` from
`Microsoft-Windows-Kernel-Acpi` at informational level. The installed analytic
channel remained disabled; its before/after metadata was byte-identical. The
owned session stopped successfully and a subsequent query found no session.

The ETL header gives the interval **20:01:34.731667 to 20:02:05.246170,
Asia/Shanghai**, on 2026-11-05: approximately 30.515 seconds. It contains:

- 112 method events: 56 state-1 and 56 state-0 records across 16 method names.
- Ordinary activity including `EC0._Q34`, `EC0._QD1`, `BAT0._BST` and
  `AC0._PSR`, as well as graphics and PCI methods.
- No method-name event containing `TBCC` or `ECRR`.
- Zero reported lost events, lost buffers or skipped events. One decoder
  warning concerns the partition-information metadata event; all 112 method
  event payloads decoded.

The ETL SHA-256 is
`e3894aa1fb7d8d43b4499db1abb89cae6fb08cad16f95e2c49fd3aaee17bb70f`.
Raw traces and process metadata remain local ignored artifacts.

Event 7 carries a method name, state and elapsed value. Microsoft's older
[generated event schema](https://github.com/microsoft/Tx/blob/master/Generated/Microsoft_Windows_Kernel_Acpi.cs)
interprets states 1/0 as started/finished; the installed template supplies no
state map or elapsed-time unit. Its payload has no invocation ID, caller,
arguments or evaluation result. All observed activity IDs were zero, and
eight starts/finishes could not be paired by method plus PID/TID. Header
threads therefore cannot be treated as an authoritative caller chain.

Nested-call coverage is also unproved. Observed `RP11._PS0`, `_PS3` and
`_DSD` have an unconditional initial `ADBG` call in the pinned DSDT, yet no
`ADBG` event appears; observed `EC0._Q34` likewise references `GGOV`, which
is absent from the trace. With no evaluation result, this is a concrete
coverage limitation rather than proof that every nested body executed.
The trace confirms working method instrumentation, not global absence of
`TBCC/ECRR`. Repeating a quiet interval would not establish exclusion.

## Port-driver client metadata

Windows resolved the DOS alias `inpoutx64` to `\Device\inpoutx64` without
opening that device. A bounded extended-handle snapshot at 19:59:20 +08:00
contained 240,382 rows, including 21,364 File handles across 576 process IDs.
The File type was identified using only an open regular file belonging to
the diagnostic itself. These counts do not identify InpOut clients.

A separate snapshot at 20:06:11 +08:00 checked the prerequisite for joining
handle rows with file-object tracing. Every one of its 21,516 File rows had
an object address of zero under the current Windows token. Object type-list
tracking was disabled and left unchanged. Existing Security audit records
were inaccessible under that token. No absent-client conclusion follows.

Broad foreign-handle duplication was not used: File handles can include
sockets, and [Microsoft documents a socket-reference-count concern](https://learn.microsoft.com/en-us/windows/win32/api/handleapi/nf-handleapi-duplicatehandle).
The proposed zero-access duplicate did not establish that this concern was
avoided. A normal loaded-module scan is also insufficient to identify handles.

The official signed x64 Microsoft Handle 5.0 executable was inspected as data.
Its SHA-256 is
`24bafcc570cc9bbb6b6e6652a57a519e0464e3996891aaba6f55299cce20b04f`.
Normal query startup attempts to open `PROCEXP152`, with fallback that writes
and loads an embedded kernel driver. Documented name/output filters do not
bypass that initialization. It was not executed, and no EULA was accepted.
The presence of a signed inspection tool does not authorize adding its driver.

## Object Manager recording

The alternative uses Windows' existing System Object ETW provider, with an
open regular file belonging to the diagnostic as a positive control. It
neither opens the InpOut device nor duplicates foreign handles. A non-admin
start returned access denied before recording. Each subsequent administrator
run had separate user approval, a unique session, a 128 MiB file cap and
cleanup restricted to its own acquired trace handle.

The first run enabled General and Handle keywords (`0x3`). It reached the
128 MiB cap after approximately 16.513 seconds, before the scheduled 30-second
stop. The session was already absent at cleanup; its process exited. Xperf
reported 1,199,064 lost events and 129 lost buffers. Tolerant decoding retained
3,299,014 Object Manager events, including 3,137,565 object-reference events,
but no object-type or handle-rundown records. Neither the held-file control
nor an InpOut client could be attributed. This lossy recording supplies no
absent-client evidence.

The corrected run enabled only Handle (`0x2`) and immediately stopped to
request the inventory, with 128 to 512 requested 64 KiB buffers. Start,
enable and stop returned success; the post-stop query found no session, and
the process exited. It produced 21,626,880 bytes with zero events or buffers
reported lost. Its SHA-256 is
`38c97eab33dd66a5b98c0566542ae52fae4e26ff797db87012d3ade02432fe35`.

Strict xperf decoding succeeded. The ETL header covers approximately
0.311 seconds, **20:26:38.245923 to 20:26:38.557164 Asia/Shanghai**. It contains
72 object-type rundown events (opcode 37) and 238,555 handle-rundown events
(opcode 39), both version 2. Of 21,067 File handles, 20,120 have a nonempty
name and 947 have no name; 20,071 names begin with `\Device\`.

The diagnostic's held regular file appears exactly once, with the expected
PID, handle and name. Its original ETL payload independently confirms the
x64 layout: object address at offset 0, PID at 8, handle at 12, type index at
16 and UTF-16LE name at 18. The type index is 42, matching File. This positive
control validates the attribution layout for this captured event version;
it must not silently admit another version. These events are distinct from
the file-name identity in classic File I/O tracing.

No handle-rundown name contains `inpout`, case-insensitively, including no
exact `\Device\inpoutx64` match. **No named InpOut client was observed in
this rundown.** The unnamed File handles prevent a complete named-client
inventory. A short rundown also does not exclude transient or future opens,
kernel I/O outside a named handle, AML/SMM access, or another port driver.
The held-file control proves the captured layout, not universal name coverage
or an exclusive reservation. Raw names and process identities remain local.

## Meaning for direct control

The catalog and trace narrow the ACPI question, but do not prove that another
mailbox client cannot run during a test. The corrected handle capture now
supplies validated named-handle evidence, with no named InpOut client observed
and explicit coverage limits. Neither result supplies a reservation that
blocks competing access across the index/data pair and whole transaction.

Formal mailbox ownership would require a supported mechanism that covers
the actual host and firmware actors, or source-backed exclusion of each
noncooperating path during the experiment. Another quiet trace would not
establish that guarantee. Request-specific
completion, independent applied-state readback and bounded restoration also
remain necessary. The generic Auto-exit requirements and the distinction
between [attended exit and host-loss recovery](so6120-direct-pwm-handback.md)
remain unchanged. These observations add no direct-PWM write authority.

The subsequent [trigger-test plan](so6120-mailbox-trigger-test-plan.md)
defines a finite empirical alternative for assessing competing-client risk:
validate detection, deliberately exercise plausible callers, and make a
scoped decision from the result. It distinguishes accepting residual risk
for an attended experiment from proving a transport ownership guarantee.
