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
| Blade 18 RZ09-0484 | [Three-channel zero duty established](fan-stop-blade18-source-findings.md), KI890 EC 1.07 | Resolve third-channel read completion, then extend ownership, journal and restoration to all three channels. |
| Blade 18 RZ09-0509 | [Three-channel zero duty established](fan-stop-blade18-source-findings.md), KI8100 EC 1.02 | Same concrete third-channel read-completion gap, independently found in this image. |
| Blade 18 RZ09-0529 | [Three-channel zero duty established](fan-stop-blade18-source-findings.md), KI8110 EC 1.06 | Same independently reviewed gap. Absence of a Maximum Fans command says nothing about fan count. |
| Blade 17 RZ09-0423 | [Three-channel zero duty established](fan-stop-blade18-source-findings.md), DI780 EC 1.05 | The alternative read has the same independently reviewed completion gap; third-channel confirmation remains unresolved. |
| Blade 14 RZ09-0482 | No exact Stop contract yet | [PI490 BIOS 2.00](https://dl.razerzone.com/drivers/Razer%20Blade%2014%E2%80%9D%20%282023%29%20%7C%20RZ09-0482x/RazerUpdater_v1.1.1.3_PI490-BIOSv02.00.exe) is available. Locate and identify an EC payload or another exact command source. |
| Blade 14 RZ09-0508 | No exact Stop contract yet | [PI4100 BIOS 1.06](https://dl.razerzone.com/drivers/Razer%20Blade%2014%E2%80%9D%20%282024%29%20%7C%20RZ09-0508/RazerUpdater_v1.1.1.3_PI4100-BIOSv01.06.exe) is available; an embedded EC region has not been established. |
| Blade 15 Base RZ09-0328 | No exact Stop contract yet | [DA550 BIOS 1.06](https://dl.razerzone.com/drivers/Razer%20Blade%2015%E2%80%9D%20Base%20%282020%29%20%7C%20RZ09-0328x/RazerUpdater_v1.1.1.2_DA550-BIOSv0106.exe) is available; EC image and control path remain unidentified. |
| Blade 14 RZ09-0370 | No exact Stop contract yet | [BIOS 1.08](https://mysupport.razer.com/app/answers/detail/a_id/5502/) provides a package lead, not an established EC control contract. |
| Blade 15 Advanced RZ09-0330 | [Source reviewed](fan-stop-ch551-source-review.md), CH551 EC 1.01 | Shared two-fan backend. Uses the exact existing Windows product name and reported EC version; mapped E0/E1 are not version registers on this model. |
| Blade 15 Base RZ09-0351 | No exact Stop contract yet | The bounded official-source search found no usable exact BIOS/EC carrier. |
| Blade 15 Advanced RZ09-0301 | No exact Stop contract yet | Its reviewed manual-RPM API starts at 3,700 RPM. Locate exact EC or vendor zero-command evidence; that range neither proves nor rules out direct PWM stop. |
| Blade 15 Base RZ09-0300 | No exact Stop contract yet | No usable standalone EC payload was identified in the reviewed sources. |

For the models with additional channels, the missing observation is actual third-channel PWM,
not the zero command itself. The candidate raw-memory read clears its completion
flag inside the handler and again on return; a following command could be erased.
The linked technical review records the exact instructions and the implementation
work needed once completion is resolved.

For the remaining models, firmware availability determines the next useful
investigation. Existing Automatic-only HID support does not prevent an independent
EC Stop implementation. Models without a completed contract remain unproven,
rather than being classified as incapable of fan stop.
