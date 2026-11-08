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
| Blade 18 RZ09-0484 | [Three-channel zero duty](fan-stop-blade18-source-findings.md) and [conditional readback design](fan-stop-blade18-readback-resolution.md), KI890 EC 1.07 | Verify hardware-trigger and asynchronous-writer assumptions, then implement the typed reader and three-channel ownership, journal, guardian and Auto restoration. |
| Blade 18 RZ09-0509 | Same source-backed control and conditional readback design, independently examined for KI8100 EC 1.02 | Same unresolved readback evidence and three-channel software work. |
| Blade 18 RZ09-0529 | Same source-backed control and conditional readback design, independently examined for KI8110 EC 1.06 | Same unresolved readback evidence and three-channel software work. Absence of a Maximum Fans command says nothing about fan count. |
| Blade 17 RZ09-0423 | Same source-backed control and conditional readback design, independently examined for DI780 EC 1.05 | Same unresolved readback evidence and three-channel software work. |
| Blade 14 RZ09-0482 | No exact Stop contract yet | [BIOS 2.00 and MCU 1.04.00 inspected](fan-stop-blade14-missing-ec-evidence.md). No EC payload identified in the complete package, UEFI and AMD PSP inventories; an exact EC source is still needed. |
| Blade 14 RZ09-0508 | No exact Stop contract yet | [BIOS 1.06 inspected](fan-stop-blade14-missing-ec-evidence.md), including its UEFI and AMD PSP contents. No EC payload identified. |
| Blade 15 Base RZ09-0328 | No exact Stop contract yet | [Signed DA550 BIOS 1.06 inspected](fan-stop-blade15-missing-ec-evidence.md), including the full package and UEFI tree. EC image and control path remain unidentified. |
| Blade 14 RZ09-0370 | No exact Stop contract yet | [BIOS 1.08 inspected](fan-stop-blade14-missing-ec-evidence.md), including its UEFI and AMD PSP contents. No EC payload identified. |
| Blade 15 Advanced RZ09-0330 | [Source reviewed](fan-stop-ch551-source-review.md), CH551 EC 1.01 | Shared two-fan backend. Uses the exact existing Windows product name and reported EC version; mapped E0/E1 are not version registers on this model. |
| Blade 15 Base RZ09-0351 | No exact Stop contract yet | [Official-source acquisition search](fan-stop-blade15-missing-ec-evidence.md#rz09-0351-and-remaining-acquisition-limits) still lacks a usable exact BIOS/EC carrier. |
| Blade 15 Advanced RZ09-0301 | No exact Stop contract yet | [Signed C3 BIOS 1.04 and 1.02 recovered and inspected](fan-stop-blade15-missing-ec-evidence.md); no EC payload identified. Its 3,700-RPM manual minimum neither proves nor rules out direct PWM stop. |
| Blade 15 Base RZ09-0300 | No exact Stop contract yet | [Signed D3 BIOS 1.03 recovered and inspected](fan-stop-blade15-missing-ec-evidence.md), including both exact Base 2019 variants. No EC payload identified. |

For the models with additional channels, a [conditional compound-read design](fan-stop-blade18-readback-resolution.md)
saves provisional third duty, resets status and requires one mapped read to
complete with fresh success. Its finite models include an erased-command zero
handler and serialized dispatch, but do not verify hardware event generation,
exclude foreign clients or establish all asynchronous status writers. The
readback gap therefore remains open, ahead of typed implementation, complete
three-channel ownership and software failure/recovery coverage.

For the other seven models, six have signed BIOS packages inspected through
their complete extractable payload trees; RZ09-0351 still lacks a usable carrier.
No exact EC Stop contract was established. Existing Automatic-only HID support
does not prevent an independent EC implementation, and unidentified payloads do
not establish that a model is incapable of fan stop. The linked evidence reports
pin the packages, extraction methods, remaining limits and next useful sources.
