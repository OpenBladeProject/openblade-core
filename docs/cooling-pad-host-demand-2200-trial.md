# Attended cooling-pad 2200 host-demand trial

Tracking: issue #119 and developer draft #624. This prepares one bounded,
experimental host-demand transition on RZ09-05289EN4 with the exact
1532:0F43 revision-0200 pad, MI_00 usage 000C:0001, Windows Feature buffer91
with prefix zero. BIOS is provenance only. No production admission follows.

## Purpose

August evidence on RZ09-0581 physically confirmed one captured vendor command
`0D/10:000600` immediately followed by target demand `0D/01:01052C`.
September validated the complete raw acquisition/Medium/quiet sequence on this
RZ09-0528 host. This trial combines those observed commands to test the missing
host-demand path, rather than inferring thermal regulation from quiet behavior.

The combination and restoration from a host-demand starting state are
experimental. Reasserting Medium before the known Medium-to-quiet transition
is not proof that all hidden firmware state is normalized. The demand is
configured2200, not measured2200RPM. No changing-temperature curve, additional
getter, range sweep, timer loop, process lifecycle or USB experiment is added.

## Preconditions and fixed sequence

The present operator must freshly confirm preferred quiet behavior, normal
full-brightness static-green lighting, no flashing-red/sleep indication,
HyperBoost inactive, USB/12V connected and availability for manual recovery.
Synapse and Razer services remain stopped. USBPcap cannot run concurrently.
The initial physical check precedes native actor construction; exact CLI
acknowledgements precede native access. Saved evidence cannot authorize writes.

One strict session reserves nine transactions before access:

1. Query raw state, require `0000`, retain that original raw value.
2. Acquire raw `0300` with exact acknowledgement.
3. Query and require `0300`.
4. Send captured vendor `0D/10:000600` with exact acknowledgement.
5. Immediately send typed captured2200 demand `0D/01:01052C`; no human pause
   separates steps4/5. Confirm normal perceived fan response and unchanged green.
6. Reassert captured Medium `0D/10:01012C`; confirm normal Medium and green.
7. Send captured vendor `0D/10:000600`; confirm preferred quiet behavior and green.
8. Restore original raw `0000` with exact acknowledgement.
9. Query raw `0000`; independently confirm final quiet behavior and green.

The captured vendor command is not named autonomous curve activation or firmware
handback. Raw state, exact semantic acknowledgement, configured demand and
physical fan observation remain separate facts.

## Stop and recovery limits

Unexpected raw state, failed or mismatched acknowledgement, timeout, cancellation,
refused observation, or abnormal fan/lighting stops every subsequent write.
There is no retry, reacquisition, automatic vendor restart, emergency Auto,
or firmware restoration in `finally`; cleanup only disposes native resources.
Attempt flags preserve the possibility of submission on ambiguity. If an invoked
runner produces no result, submission state is unknown and manual attention
is required rather than reporting safe restoration.

Only exact demand acknowledgement and affirmative normal physical observation
permit the experimental Medium/quiet restoration path. Manual USB+12V power
cycling is a contingency and previously returned Medium rather than preferred
quietness; it does not guarantee restoration. No service/Synapse restart is
performed. A present operator must remain available throughout the live run.

```powershell
OpenBlade.Capture.exe validate-rz09-0528-cooling-pad-host-demand-2200-trial `
  --confirm-target RZ09-05289EN4:1532:0F43:0200:HostDemand2200-Medium-PreferredQuiet `
  --confirm-recovery ManualPowerCycleOnly
```

Before execution, verify reviewed committed source/build hashes, exact-device
and controller inventory, and the fresh physical baseline. Private raw typed
output is not committed; publish a sanitized annotation only after the actual
outcome and final physical observation. A planned sequence is not execution
or independent packet-count evidence. Production fan-state/RPM, ownership,
handback and host-managed temperature tracking remain unvalidated.
