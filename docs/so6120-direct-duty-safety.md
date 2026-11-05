# SO6120 direct-duty protection and restoration review

Static review on 2026-11-05 confirms that the internal `FE` duty path retains
some thermal responses. It does not establish a bounded, independent return
to normal cooling after host failure. The required permission bit also changes
non-fan policy calculation, and the MCU can continue queued work independently
of the host. These are concrete obstacles to a reliable Silent fan-stop switch.

This review uses the exact RZ09-0581 images identified in the
[EC 1.04 analysis](so6120-ec104-fan-analysis.md#input-and-method) and
[MCU 1.07 bridge analysis](so6120-mcu107-ec-fan-bridge.md#exact-inputs-and-method).
It does not generalize their behavior to another model or firmware image.
No updater or vendor code was executed, no hardware was accessed, and no
installed settings or production admission changed. The live Silent Automatic
comparison remains paused.

## Thermal responses that survive direct duty

EC supervisor `0x1586A` calls `0x1210C` at `0x15878..0x1588A`, before the
`FE` check at `0x158AE..0x158B6` skips ordinary fan adjustment `0x15510`.
The active-state supervisor still reaches the following responses. None of
these established responses restores the normal fan selector or raises duty.

| Response | Instruction-backed behavior | Limit |
|---|---|---|
| Warm, zero-tach response (`0x1162C`, separately scheduled from `0xD234`) | With its enable gates satisfied, raw sensor `0x80256 >= 71` and either tach byte zero accumulate toward count 90; this sets CPU/GPU PROCHOT reason bit `0x20` in `0x80460/0x80461`. | Throttling, not fan restart; does not cover every inadequate nonzero speed. |
| Sensor/table CPU response (`0x110E6`) | Compares `0x8035A` with table extension `+1`; asserts CPU PROCHOT output and reason bit `0x02`, with clearing governed by extension `+2`. | Factory Silent values are both raw 120; equality can assert then clear the stored reason in one invocation. |
| Sustained high-sensor response (`0x113FA`) | With a table threshold above 80, the 12th qualifying invocation from a zero counter clears `0xF01605` bit 1 and `0xF0160C` bit 2. Factory Silent threshold is raw 125. | The same GPIO clears appear in power-off routine `0xD88A`; shutdown purpose is an inference, not verified board power removal or a cooling guarantee. |

The zero-tach counter is not necessarily consecutive: an intervening warm
sample with both tach bytes nonzero does not clear the pre-latch count; a
sensor value below 71 does. Once latched, both tach bytes becoming nonzero,
or 60 qualifying invocations below raw 53, clears that response. CPU/GPU
PROCHOT identification is supported by diagnostic strings and the output
maintenance at `0xC75C..0xC798`. Actual component response was not measured.

These values are raw sensor units and invocation counts, not established
Celsius thresholds or seconds. Tach bytes are quantized speed/100 telemetry.
Timer `0x667C`, dispatcher `0x20F5E` and nested cadence counters schedule the
work, but pending counts can coalesce; no maximum wall-clock response time
was established. The runtime table was not read and may differ from factory
data.

Sensor freshness is a separate limitation. Routine `0x1393E` samples through
`0xB426` on its tenth cadence opportunity and updates the filtered values only
after a successful, in-range result. Failed or rejected reads retain the old
value. Additional flags can suppress sampling. No stale-sensor expiry was
established that forces cooling or exits `FE`.

The direct branch at `0x16262` reapplies requested duty when it differs from
actual PWM. At `0x1627C/0x162AC`, NDS32 `beqs38` compares the printed operand
with implicit `a5`, which holds requested duty `0x80394/0x80395`; the nearby
RPM loads are used for separate bookkeeping. The [Ghidra instruction definition](https://raw.githubusercontent.com/NationalSecurityAgency/ghidra/master/Ghidra/Processors/NDS32/data/languages/nds32.sinc)
confirms that operand rule. A transient outside PWM write would therefore not
alone establish a durable override of this branch. No independent emergency
high-duty writer was found in the recovered paths; unrecovered or indirect
code prevents treating that as a universal absence proof.

## Complete Silent policy preservation is unproved

The `FE` branch precedes normal table loading at `0x16314`. It retains the
loaded table at `0x8A000` and last-normal selector `0x8034F` while resetting
fan-stage counters. Helpers `0x10DD8/0x10E36` also treat Silent `5` and `FE`
alike locally. These facts do not prove complete Silent equivalence.

The permission required by direct duty, `0x80425` bit 6, has a concrete
non-fan effect. At `0x1101A..0x11022`, routine `0x10FD4` branches over normal
recomputation of policy/event byte `0x80493`. Its old value still participates
in the maximum selection at `0x1109C..0x110E0`, producing `0x8033C`. Under
its own active-state gates, `0x1157A` consumes that result through event path
`0xFE3E -> 0x9DD0`. The downstream OS/driver meaning is unresolved; these
bytes must not be labeled watts or a specific CPU/GPU power cap.

The flags byte is not a boolean: bit 5 separately suppresses sensor updates.
Clearing or replacing the entire byte would have different effects from
changing bit 6. Also, conditional profile-data reload `0x17026` normalizes
selectors above 7 to selector 0, then looks up slot `selector + 0x28`.
Periodic `0xCB34` can invoke it when
`0x80439` bit 3 is set; this is a conditional path, not an observation that
idle `FE` actually triggers a Balanced transition.

## MCU ownership, completion and recovery

The generic gate at MCU RAM `0x100000C1` does not pause thermal worker
`0x4B5C`, which runs from main loop `0x66B4`. Pending mask `0x100000C8` and
cache `0x10000220` remain live independently of the host process.

- Normal SET handlers update shared cache before bus work. Failed mode,
  manual or target transfers leave pending bits for later retry. Mode,
  mailbox, manual state and targets are separate bus transactions.
- Ordinary GET `0D/81` and `0D/82` returns that same cache. Independent worker
  reads also overwrite it, without an inspected per-operation generation or
  completion marker. A matching GET can be an intent echo. If read and write
  bits are both pending, a worker read can replace the byte before its write.
- Successful target writes clear their write bits without scheduling target
  readback. Successful manual writes set pending bit 16, which reads indices
  `DC/78/DE/79/DD/DF`; the separate manual-flag read at index `36` uses bit 18.
  Instructions `0x4C82..0x4C88` compute `2 << 15`, not `1 << 18`. This corrects
  the earlier manual-readback interpretation: that successful-write branch
  does not itself enqueue manual-flag confirmation. Countdown
  `0x100000D0 = 50` concerns separate telemetry; queue delay is `0x100000CC`.
- Periodic work can enqueue fresh reads. Stopping host requests or observing
  an empty queue would not establish a durable pause. The normal worker
  neither checks the generic gate nor forces the shared EC bank to zero.
- Generic transfers run from timer-dispatched command handling; normal work
  runs in main context. Both use helpers `0x5864/0x58B0`, transfer state
  `0x100000D4` and buffer `0x1000024C`. Full interrupt priority and reentrancy
  behavior remain unresolved; no actual or inevitable collision is claimed.

The inspector follow-up narrows that interrupt uncertainty. Startup `0x5E18`
calls priority initialization `0x6094`; setter `0x28EC` keeps only the low two
priority bits. The recovered initialization assigns timer IRQ16 logical
priority 3, I2C IRQ15 priority 0 and USB IRQ22 priority 1. This is source
configuration, not live readback or proof about every later priority change.
Transport wait `0x2520` repeatedly calls watchdog feed `0xB018`, whose final
`cpsie i` at `0xB024` re-enables interrupts without restoring a saved mask.
A blanket interrupt-mask assumption therefore cannot establish ownership.
The next unresolved interval is the timer dispatch gates and all shared-helper
callers: if generic dispatch reaches the helpers during a normal worker
transaction, the examined path can replace shared state. No such overlap was
observed on hardware.

The subsequent [transfer ownership audit](so6120-mcu-transfer-ownership.md)
enumerates the recovered direct callers, including main-loop non-EC peripheral
transfers. It distinguishes report-ready, initialization and application-state
gates from bus ownership; pausing only thermal work would not prove exclusive
access to the shared transport.

The MCU feeds its hardware watchdog from the main loop and inside transfer
waits. A host-process crash does not stop this execution or itself trigger a
USB reset. MCU initialization clears the gate and refreshes cache, but is not
an exact user-state restore or an established host-loss timeout.

Application vectors at `0x2000` identify USB IRQ handler `0x3A8C`. Its reset
branch calls `0x4090/0x40F8`; the reviewed reset, status-change and secondary
endpoint callback paths clear USB/input state without directly clearing the
thermal queue/gate or restoring EC fan state. Endpoint-0 callback `0x3874`
still has incompletely recovered switches at `0x38B8/0x394E`. This is bounded
coverage, not proof about every USB request or disconnect path. Even complete
USB cleanup would not alone establish recovery from host-process death.

On the EC, inactive-state reset `0x1488A`, power transitions and permission
clearing can leave `FE`. They select a normal policy or reset state; they do
not restore an independently captured host baseline. No bounded active-mode
lease or exact handback was established.

## Decision and remaining evidence

The static bridge is real, and some thermal protection survives. Present
evidence nevertheless does not justify describing a direct-duty trial as
fail-safe or shipping a Silent fan-stop switch. Such a design still needs:

1. A bounded cooling recovery independent of the host and its transport,
   including a justified treatment of stale sensor data.
2. Exclusive or provably quiescent EC access despite MCU queued work,
   with independent completion, full baseline and exact restoration checks.
3. A reviewed treatment of the permission bit's non-fan effects and the
   difference between internal `FE` and normal Silent policy.

These are specific missing properties, not a claim that fan stop is impossible.
The existing firmware's Silent Automatic zero-target stage remains a separate,
normal-policy route; its paused idle comparison is not replaced by this review.
Production fan-stop admission and other-model scope remain unchanged.

The subsequent [register review](so6120-ec-register-semantics.md) maps all
override bits, follows the held contributor into the host event queue, and
resolves the saved-mode fallback. It also establishes ordinary zero writes
at the PWM layer and the generic interface's separate bus-result status.
Those details refine the contract without supplying independent recovery.

Independent review checked the surviving protections and their limits, the
permission-bit side effect, queue/cache behavior, watchdog and bounded USB
findings. It confirmed the scoped conclusions after resolving the NDS32
operand interpretation against the instruction definition. Full local traces
remain ignored in `artifacts/silent-firmware-research` and
`artifacts/silent-mcu-research`; no vendor payload or full decompilation is
included here.
