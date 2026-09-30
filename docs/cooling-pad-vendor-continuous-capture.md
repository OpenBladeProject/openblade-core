# Bounded vendor Smart Fan Curve capture

An attended pad-only vendor capture ran on 2026-09-30 Pacific after the operator
authorized temporary Synapse use. OpenBlade source `55718fa0` was installed,
but its service was stopped throughout capture. No OpenBlade pad control command
ran. Admission checked exact model/SKU `RZ09-05289EN4`, pad `1532:0F43` revision
`0200`, MI00, usage `000C:0001` and 91-byte Feature geometry. BIOS did not gate it.
USB and 12 V stayed connected; HyperBoost inactivity was operator provenance.

## Actions and recorded state

The operator first confirmed normal quiet fan behavior and full-brightness
static-green lighting. Synapse showed Fan Control disabled, with Fixed RPM,
Medium and 2200 retained underneath. Lighting was enabled at 100%, Static green;
display-off lighting was unchecked. These are saved UI settings and physical
observations, not device-state readback or firmware Auto evidence.

Computer Use enabled Fan Control and selected Smart Fan Curve with its existing
Balanced preset. The operator confirmed normal fan behavior and unchanged green
lighting. Computer Use started the existing windowed Steel Nomad custom run,
with full-screen and loop disabled. The benchmark initially collected system
information, then rendered. Synapse subsequently displayed CPU/GPU maxima of
85.6/69.0 degrees C and a 2200 fan value. UI values are vendor-reported context;
no independent measured-RPM or timed telemetry trace was collected.

The recorder reached its five-minute limit and stopped gracefully. Fixed/Medium
commands were captured immediately before timeout. Turning Fan Control off and
the final operator quiet/green confirmation occurred afterward. The recording
therefore does not establish complete captured restoration. The operator later
fully exited Razer AppEngine, the two temporary Razer services were stopped,
and the previously running OpenBlade service was restored and verified by
read-only IPC59. Its pad control remained stopped and observed-only.

## Packet evidence

The private PCAP is 13,037 bytes, SHA-256
`0D3B7C8014E7BD8F75F47F94FC9E7B51EABEABFDAD4C043FEC5EFE4D71EEB0DB`.
It contains 142 raw frames and 77 decoded payload entries. All 68 complete
90-byte report bodies have valid checksums, with no truncated report bodies.
All decoded traffic belongs to the freshly verified pad bus/address.

Four fan request/response pairs have exact echoes, successful status `02`,
valid checksums and matching transaction/command/length/payload:

| Request/response frames | Transaction | Command and payload | Observation |
| --- | --- | --- | --- |
| 21/24 | `15` | `0D/10:01012C` | Fixed Medium 2200 |
| 35/38 | `16` | `0D/01:01052C` | Configured demand 2200 |
| 133/136 | `17` | `0D/10:01012C` | Fixed Medium 2200 |
| 137/140 | `18` | `0D/10:01012C` | Fixed Medium 2200 |

The final two responses occur at approximately 296.94 and 296.96 seconds after
the first packet, before timeout. There are no unmatched or ambiguous fan
reports. There is exactly one demand and zero changed-demand successors;
`000600` is absent. Sixty lighting requests have no paired responses. Their
IDs wrap `1F -> 00 -> 01` twice, at frames 61/63/65 and 125/127/129.
Lighting rollover does not establish the acknowledged fan-control policy.

## Conclusion

The capture corroborates one configured-2200 demand during the observed Smart
Fan Curve selection. It does not establish a continuing temperature-responsive
demand sequence, acknowledged fan-counter reuse, session renewal, measured RPM,
thermal causality or firmware ownership. A single unchanged target cannot
identify whether the saved curve selected a constant target or why further
writes were absent. The continuous-control evidence gap remains open.

Read-only inspection revalidated the previously reviewed cached vendor module.
Its ordinary monitor callback evaluates saved CPU/GPU curves and suppresses
unchanged scaled targets. The inspected Smart UI setter updates settings without
directly issuing `0D/01`; with operator-reported inactive HyperBoost, the initial
demand is consistent with an ordinary callback, but no runtime call trace proves
that origin. Flat or effectively plateaued curves and later missing/invalid
callback inputs remain possible. Neither the capture-time Balanced point arrays
nor correlated callback inputs were recorded. Present profile settings alone
cannot establish past settings.

Subsequent narrow read-only inspection found the current saved Smart configuration
in the active pad profile: Balanced, CPU input, with CPU points
`(40,1000), (90,2200), (100,2200)` and GPU
points `(30,1000), (80,2200), (90,2200)`. The inspected host algorithm selects
the first point strictly above the current temperature rather than interpolating.
The saved CPU curve therefore selects 2200 for rounded CPU `40 <= T < 100` degrees C,
including 85.6. This supports an effective-plateau explanation for an unchanged
target. The stored records have no record-level timestamps, so they do not prove
capture-time settings or continuing callback activity. A future separately
authorized changing-demand experiment needs reviewed curve steps within the
observed telemetry range before starting the workload.

Preserve this bounded result as inconclusive for counter reuse. Do not infer
wrapping from lighting or automatically repeat/extend the capture. Inspect the
saved curve policy and host-loop evidence before proposing another attended run.
Raw packets, local paths, identifiers and vendor source remain private.
