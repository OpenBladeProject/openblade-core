# Fan Stop coverage across Blade models

All 19 supported model families were screened. Support below concerns the
guarded Stop/Auto feature; ordinary fixed-RPM or Maximum Fans controls have
separate contracts. A positive minimum RPM does not establish whether the EC
can stop a fan.

The shared implementation requires exact host/HID and reviewed EC-version
admission, an already-installed reviewed InpOut driver, fresh temperature and
power checks, and verified Auto restoration. PawnIO supplies CPU readings only.
Entry remains USB-C Balanced. Source review and physical validation are recorded
separately; the new models do not inherit RZ09-0581's physical results.

| Model | Stop evidence | Implementation or remaining work |
|---|---|---|
| Blade 16 RZ09-0581 | [Physically validated](so6120-stop-auto-guardian.md), SO6120 EC 1.04 | Existing guarded backend. |
| Blade 16 RZ09-0528 | [Source reviewed](fan-stop-so6110-source-review.md), SO6110 EC 1.09 | Shared two-fan backend with its own identity and firmware contract. |
| Blade 16 RZ09-0483 | [Standard SO690 EC 2.01](fan-stop-so690-source-review.md) and [Lamborghini SO695 EC 1.01](fan-stop-so695-source-review.md), source reviewed | Separate firmware entries; journaled board-identifier reads distinguish standard `3006` from Lamborghini `3009`. |
| Blade 16 RZ09-0510 | [Source reviewed](fan-stop-so6100-source-review.md), SO6100 EC 1.02 | Shared two-fan backend; ordinary fixed-RPM support is not a prerequisite. |
| Blade 14 RZ09-0530 | [Source reviewed](fan-stop-pi4110-source-review.md), PI4110 EC 1.03 | Shared two-fan backend with its own exact contract. |
| Blade 15 RZ09-0421 | [Source reviewed](fan-stop-ch580-source-review.md), CH580 EC 1.03 | Shared two-fan backend with its own exact contract. |
| Blade 14 RZ09-0427 | [Source reviewed](fan-stop-pi480-source-review.md), PI480 EC 1.03 and 1.06 | Separate reviewed version entries share the two-fan backend. |
| Blade 18 RZ09-0484 | [Three-channel zero duty](fan-stop-blade18-source-findings.md), [primary-reader conditions](fan-stop-it8320-trigger-investigation.md) and [alternate-reader ownership gap](fan-stop-bram3-readback-review.md), KI890 EC 1.07 | Both reader routes remain conditional. Establish completion/descriptor ownership, then implement three-channel journal, guardian and Auto handling. |
| Blade 18 RZ09-0509 | Same source-backed control and conditional readback design, independently examined for KI8100 EC 1.02 | Same unresolved readback evidence and three-channel software work. |
| Blade 18 RZ09-0529 | Same source-backed control and conditional readback design, independently examined for KI8110 EC 1.06 | Same unresolved readback evidence and three-channel software work. Absence of a Maximum Fans command says nothing about fan count. |
| Blade 17 RZ09-0423 | Same source-backed control and conditional readback design, independently examined for DI780 EC 1.05 | Same unresolved readback evidence and three-channel software work. |
| Blade 14 RZ09-0482 | No exact Stop contract yet | [MCU fan/mode bridge traced](fan-stop-pi490-mcu-bridge-review.md): named duty command unsupported; EC-side zero duty/readback/Auto still missing. [BIOS host interfaces](fan-stop-missing-model-host-interfaces.md) add generic transport, not fan semantics. |
| Blade 14 RZ09-0508 | No exact Stop contract yet | [BIOS 1.06 inspected](fan-stop-blade14-missing-ec-evidence.md). [Host EC interfaces traced](fan-stop-missing-model-host-interfaces.md); no implemented complete fan hook or EC payload identified. |
| Blade 15 Base RZ09-0328 | No exact Stop contract yet | [Signed DA550 BIOS 1.06 inspected](fan-stop-blade15-missing-ec-evidence.md). [DPTF fan API resolves to stubs](fan-stop-missing-model-host-interfaces.md); EC image/control meaning still missing. |
| Blade 14 RZ09-0370 | No exact Stop contract yet | [BIOS 1.08 inspected](fan-stop-blade14-missing-ec-evidence.md). [EC-init modules resolve to host ACPI plumbing](fan-stop-missing-model-host-interfaces.md); no complete fan contract identified. |
| Blade 15 Advanced RZ09-0330 | [Source reviewed](fan-stop-ch551-source-review.md), CH551 EC 1.01 | Shared two-fan backend. Uses the exact existing Windows product name and reported EC version; mapped E0/E1 are not version registers on this model. |
| Blade 15 Base RZ09-0351 | No exact Stop contract yet | [Official-source acquisition search](fan-stop-blade15-missing-ec-evidence.md#rz09-0351-and-remaining-acquisition-limits) still lacks a usable exact BIOS/EC carrier. |
| Blade 15 Advanced RZ09-0301 | No exact Stop contract yet | [Signed C3 BIOS 1.04/1.02 inspected](fan-stop-blade15-missing-ec-evidence.md). [DPTF fan API resolves to stubs](fan-stop-missing-model-host-interfaces.md); no EC payload identified. Its manual minimum does not settle PWM stop. |
| Blade 15 Base RZ09-0300 | No exact Stop contract yet | [Signed D3 BIOS 1.03 inspected](fan-stop-blade15-missing-ec-evidence.md), including both exact Base 2019 variants. [Host EC interfaces traced](fan-stop-missing-model-host-interfaces.md); fan semantics still missing. |

For the four models with additional channels, the [expanded scheduling model](fan-stop-it8320-trigger-investigation.md)
finds no false read success from extra notifications alone under its writer and
execution assumptions. It does find a stale callback that can erase a later
Auto request. Trigger meaning and computed-writer exclusion remain unresolved.
The [alternate reader](fan-stop-bram3-readback-review.md) has exact 302/303
port-base evidence on all four images, but ordinary callbacks share its address
bytes. A conditional staging race returns a wrong zero; reachability under an
independently clean FE session is not established. Observing FE alone does not
prove callback drainage. Neither reader is admitted for runtime use yet.

For the other seven models, six have signed BIOS packages inspected through
their complete extractable payload trees; RZ09-0351 still lacks a usable carrier.
No exact EC Stop contract was established. Existing Automatic-only HID support
does not prevent an independent EC implementation, and unidentified payloads do
not establish that a model is incapable of fan stop. The linked evidence reports
pin the packages, extraction methods, remaining limits and next useful sources.
The [host-interface follow-up](fan-stop-missing-model-host-interfaces.md) traces
six BIOSes beyond payload labels, and the [PI490 MCU review](fan-stop-pi490-mcu-bridge-review.md)
adds a model-specific bridge. These partial interfaces do not supply the missing
zero-duty, actual-readback and Auto contracts.
