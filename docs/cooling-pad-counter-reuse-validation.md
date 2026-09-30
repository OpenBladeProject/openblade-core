# Cooling-pad developer counter reuse validation

This prepares one finite, attended native experiment. It has not been run on
hardware. Production pad admission remains closed. The vendor trace shows the
`1E -> 00 -> 01 -> 02` boundary; it does not contain an identical fan transaction
ID twice. A successful software test cannot replace native verification of reuse.

The command is:

```powershell
OpenBlade.Capture.exe validate-rz09-0528-cooling-pad-counter-reuse --confirm-target RZ09-05289EN4:1532:0F43:0200:CounterReuse1000-2200-Medium-PreferredQuiet --confirm-recovery ManualPowerCycleOnly
```

Run only after a separately approved attended preparation: exact RZ09-0528 /
RZ09-05289EN4 host, sole exact `1532:0F43` revision `0200`, MI00, usage `000C:0001`,
91-byte feature geometry and report prefix zero. BIOS is provenance, never a gate.
The operator must freshly confirm preferred quiet fan behavior, green lighting,
no red/sleep indication and HyperBoost inactive. The installed OpenBlade service
and all competing controllers must remain stopped for the entire experiment.
The command does not stop them. It rechecks OpenBlade service/process absence,
vendor-controller absence and exact metadata before each SET. Relevant services must
be exactly Stopped. The current process must be the recognizable Capture apphost
and the sole Capture PID in a fresh process snapshot; a second process or detection
uncertainty blocks admission. These observations
do not establish cross-process exclusion: another controller must not be started.

The ordinary `01..FF` nonreusing allocator and ordinary actor's zero rejection
are unchanged. This internal developer factory is single-use. Its actor requires
an explicit exact-pad opt-in plus a tagged assignment, strict admission and a
validated response. Zero is accepted only for the typed `0D01` demand exchange.
SET-only/batch transports, arbitrary commands, arbitrary configured demands and
`1F` are excluded from this policy. Only configured 1000 and 2200 demands and the
existing exact raw/entry/restoration commands are admitted. One actor exchange is
outstanding at a time. Full response envelope, checksum, status, ID, class,
command, length and payload validation remain required.

The coordinator performs six startup exchanges: raw0000 query, acquire raw0300,
the acquire's internal query, the coordinator's separate raw0300 verification,
`000600` entry and the first configured 2200 demand. Entry and first demand have
no human pause between them. After the first normal-fan physical confirmation,
33 alternating configured 1000/2200 demand updates run two seconds apart. There
is no workload, telemetry loop, arbitrary duration or retry option.

The wire policy starts at `01`, advances through `1E`, then `00`, and continues.
It reserves 39 normal operations and four restoration operations. Reservations
consume capacity; actual IDs are allocated when the actor claims each exchange.
It does not reserve literal restoration IDs ahead of demand traffic. On the
complete prescribed sequence, the first demand's `06` is reused at exchange 37
with the other configured demand, followed by demand `07` and `08`. Completion of
that milestone is derived from recorded assignments and their ACK outcomes, not
from the loop count. Restoration then uses `09`, `0A`, `0B`, `0C`.

The operator watches the entire interval and cancels on abnormal behavior. After
the milestone, a normal-fan/green/HB-inactive confirmation explicitly permits the
existing Medium `01012C` restoration step. Its physical confirmation permits
quiet `000600`; quiet confirmation permits the saved raw0000 SET and matching
query. Final quiet/green confirmation permits durable evidence resolution. Each
operator wait is bounded to ten minutes; the last configured demand may remain
held during the wait. No observation measures RPM. Raw0000 is not fan-state or
ownership readback, and `000600` does not establish firmware autonomous control.

The same core service coordinator and protocol adapter perform the operations.
Protected mutation evidence is written before possible mutating port calls and
is retained on failure, Stop or process loss. Only the complete explicit attended
restoration resolves its matching record. This does not exercise outer installed
service IPC/admission or add cross-process controller serialization.

Any ambiguous exchange, guard failure, timeout, cancellation or rejected physical
gate stops subsequent firmware operations. There is no retry, reopen, reacquire,
automatic restoration, service restart or vendor launch. `finally` only drains
resources. Manual power cycling is a contingency and does not guarantee quiet
recovery. An unresolved protected record intentionally blocks later admission;
this command neither removes it nor supplies a recovery bypass.

JSON records actual actor-assigned wire IDs and outcomes, requested/acknowledged
configured demand decisions, physical confirmations and restoration outcomes.
An assigned ID is not proof of native submission. Missing completion is unknown;
a complete logical total is emitted only for successful completed cleanup and
verified actor FullyDisposed release with no pending factory disposal. This
is application evidence, not an independent packet trace, measured fan RPM,
thermal tracking, firmware handback or production approval. Native reuse with two
ACK successors remains the next attended check; production lifecycle, retained
effects, cross-process admission and lighting requirements remain separate.

Version impact: this developer-only preparation leaves product version `0.24.0`,
IPC `59`, public contracts and stored schemas unchanged. No production capability
is opened. No installed-service deployment or hardware run is part of these checks.

## Software verification

The full solution builds with zero warnings and errors. Full Protocol tests pass
1668/1668 and Capture tests pass 507/507, including 18 counter-specific cases.
Service tests pass 2046 in the sandbox; the sole named-pipe failure passes 1/1 on
the host rerun. The unchanged elevated protected-store test is skipped in this
run; its earlier positive result is separate. Independent final review passes,
with hardware-free Protocol 52/52 and Capture 18/18 checks independently repeated.
No native pad operations were executed by this verification.
