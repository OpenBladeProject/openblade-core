# Keyboard input methods

Open **Laptop → Keyboard** and expand **SnapTap (A/D)** for **SnapTap input**,
or **Function key behavior** for **Multimedia key input**. Choose **Windows hook**
or **Razer driver** separately for each feature. These are per-user choices and
remain selected across profiles.

Existing preferences keep SnapTap on the Razer driver and the function row on
the Windows hook. An unknown saved selection pauses the affected feature. Temporary preference
read failures preserve the last confirmed choices.

| Method | Behavior |
| --- | --- |
| Windows hook | Uses OpenBlade's current-user input handling without a Razer driver. SnapTap applies to A/D across keyboards. Some games use keyboard input paths that bypass or interfere with hook-based remapping. |
| Razer driver | Uses the exact laptop's trusted installed keyboard filter and shared control driver. It can consume the original key before forwarding it to Windows. Driver setup remains an explicit user action. |

Function-row driver handling is available on RZ09-0581, RZ09-0528 and
RZ09-0484. It is source-backed experimental until each model's own physical
validation is complete. Blade 18 uses its separate Fn decoder and host input
handling; it does not require a firmware Driver-mode change. Choose **Razer
driver** to change Blade 18's primary function-row behavior. Its Windows-hook
selection retains the existing default Fn shortcuts. Other models keep their
existing function-row capabilities.

One driver supervisor and worker own all selected driver keys, including both
features when requested. RzCommon stores hook records per device and delivers
notifications to enabled file subscribers. Independent subscriptions could
consume another feature's keys during setup or closure, so changing the selected
set stops the whole driver owner, closes its subscription, verifies restoration,
then starts the new set. Windows SnapTap may run alongside a driver-owned
function row because their key sets are separate.

Switching waits for held keys to be released and for the previous owner to
finish cleanup. A failed or ambiguous output or cleanup keeps its recovery
state and blocks another backend from bypassing it. Missing-driver admission
before any write permits an explicit switch to the Windows hook. No failure
automatically changes the saved choice.

Keys the driver helper sends back must reach Windows. The helper watches
Windows keyboard input and stops if a key it sent has not arrived within two
seconds, so the driver's hooks are cleared and Windows receives A, D and the
function row directly. The check runs as keys are handled, so a key lost just
before typing stops is noticed at the next key press. Turn the feature off and
on to retry. Keys sent to an app running as administrator are not checked.

The source contract is the pinned [driver review](snap-tap-driver-review.md)
and [per-file subscriber review](snap-tap-0255-catalog-api-review.md#subscriber-lifetime-and-restoration-limits).
The function-row extension uses the same reviewed keyboard record type and
fixed F1-F12 scan positions already used by OpenBlade's function-row translator;
it adds no firmware command or model admission. The keyboard reports Fn only
when it changes, so on RZ09-0581 and RZ09-0528 the driver helper assumes Fn is
released when it starts, as the Windows hook does, and again after a status,
driver-mode or input-device reset. If Fn is held across that point, F-row
presses use the other layer until Fn is released. After a malformed Fn report
or a failed input packet, the helper waits for the next Fn report or reset, and
F keys pass through unchanged; the app asks you to press Fn once. It also shows
that prompt while the service has not verified the laptop. RZ09-0484 binds Fn to the
collection that first reports it, so its helper always waits for an actual Fn
report after starting or resetting.
Diagnostics report each
feature's backend independently. Windows-hook status does not claim driver
validation.

This change updates the Session Agent and requires a coordinated installation.
It does not change the service or Session IPC protocol. Offline tests verify
routing and ownership; physical game compatibility and the new function-row
driver behavior still need attended validation.
