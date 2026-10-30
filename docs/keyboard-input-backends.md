# Keyboard input methods

Open **Laptop → Keyboard** to choose **Windows hook** or **Razer driver**
separately for SnapTap and the function row. These are per-user choices and
remain selected across profiles.

Existing preferences keep SnapTap on the Razer driver and the function row on
the Windows hook. An unknown saved selection pauses the affected feature. Temporary preference
read failures preserve the last confirmed choices.

| Method | Behavior |
| --- | --- |
| Windows hook | Uses OpenBlade's current-user input handling without a Razer driver. SnapTap applies to A/D across keyboards. Some games use keyboard input paths that bypass or interfere with hook-based remapping. |
| Razer driver | Uses the exact laptop's trusted installed keyboard filter and shared control driver. It can consume the original key before forwarding it to Windows. Driver setup remains an explicit user action. |

The function-row driver path currently requires the existing exact-model Fn
semantics for RZ09-0581 and RZ09-0528. It is source-backed experimental on both
models until its own physical validation is complete. The other models retain
their existing function-row capabilities; choosing a backend does not grant a
new model capability.

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

The source contract is the pinned [driver review](snap-tap-driver-review.md)
and [per-file subscriber review](snap-tap-0255-catalog-api-review.md#subscriber-lifetime-and-restoration-limits).
The function-row extension uses the same reviewed keyboard record type and
fixed F1-F12 scan positions already used by OpenBlade's function-row translator;
it adds no firmware command or model admission. A newly started driver helper
waits for an actual Fn report before interpreting the multimedia layer; press
Fn once when the app asks. Until then, F keys pass through unchanged.
Diagnostics report each
feature's backend independently. Windows-hook status does not claim driver
validation.

This change updates the Session Agent and requires a coordinated installation.
It does not change the service or Session IPC protocol. Offline tests verify
routing and ownership; physical game compatibility and the new function-row
driver behavior still need attended validation.
