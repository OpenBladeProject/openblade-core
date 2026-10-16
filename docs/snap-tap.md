# SnapTap

SnapTap is an experimental A/D keyboard feature. When both keys are held, the last pressed key takes priority. Releasing it restores the other key if it is still held.

Open **Laptop**, find **SnapTap (A/D)**, and turn it on. It is off by default. Release A and D before activating it. The status first shows that the helper is ready, then shows Active after it handles an A/D event.

Physical A/D presses keep their device identity for Reactive lighting. Restoring a still-held key is synthetic and does not trigger a new physical-key lighting pulse.

The feature applies across keyboards; it cannot select only the laptop keyboard. The pair is fixed to the A/D scan positions. Game compatibility has not been verified.

SnapTap keeps running when you close the visible Tray. Turning it off or shutting down the Session Agent stops its input helper. If you change focus while a key is held, or Windows does not accept an output batch completely, the helper pauses instead of guessing or retrying input. Release A and D, then turn SnapTap off and on to retry.

This feature uses Windows input APIs. It does not change keyboard firmware or install a driver. The current validation was performed with the existing Razer drivers still installed; operation after removing them has not been physically verified.

## Development and validation

The Session Agent launches its own executable with a dedicated input-helper mode in a separate process. That mode does not initialize Raw Input, the normal Session Agent, device ownership, or any firmware backend. It verifies the parent image, user, and session, holds the parent process handle, observes the existing coordinated shutdown event, and uses a per-user/session ownership mutex.

Input settings are read independently of unrelated UI fields, so an unknown navigation entry cannot silently disable input features. Malformed input settings retain the last confirmed bindings in the existing watcher and prevent helper startup.

The parent reuses the current preference watcher, supervises one child, and never restarts a faulted helper until an explicit off/on transition. Status is versioned, atomically written per user/session, and checked against the reporting process lifetime. The hook thread only queues status changes; file I/O runs outside it. A confirmed owned key is released on normal cleanup only while the same foreground window remains current. Uncertain output, lost foreground ownership, and crashes cannot be treated as confirmed cleanup.

The fixed-pair core is tested against an independent press-order oracle across all 1,024 five-event A/D sequences. Focused tests also cover repeats, release order, disabled physical tracking, permanent output faults, cleanup once, child failure, cooperative shutdown, explicit restart, malformed preferences/status, command gates, and native ABI geometry. Existing function-row recovery rejects unmatched A/D packets without reinstalling its hook.

Disposable native attended prototypes established ordinary-window remapping and delivery to a separate background Raw Input receiver: 45 physical events were suppressed, and all 47 replacement events matched both delivered sequences. The same-process experiment lost hook callbacks after Raw Input registration. These observations identify a useful isolation boundary, not a universal Windows rule.

The later attended coexistence test on RZ09-0581 used commit `c1569838` alongside the unchanged installed Session Agent and THX helper. Physical initial A/D presses passed through with Rz09_0581 device attribution; priority releases and held-key restorations remained synthetic and Unsupported. Both overlap directions delivered the old-key release before the new physical down, with no delivered A+D overlap. The window and Raw Input streams matched, and the user confirmed Reactive flashes on initial A/D presses with SnapTap both enabled and disabled. This establishes the exercised sequence on that machine, not a universal scheduling guarantee. Ripple was not visually tested.

The preceding coexistence run also confirmed the requested Fn+F2 combination delivering F2 and M1 delivering its default Page Up with SnapTap enabled and disabled. Custom special-key actions were not exercised. Cooperative stop, preference/status restoration, Raw Input registration removal, and unchanged installed process identities and protected hashes passed. Firmware lighting readback is unavailable; the latest post-test status still reported Reactive rather than the original Spectrum intent.

Before release, validate production startup, the final preference-watcher integration, output-failure presentation, and the rendered UI on the final build. Keep the implementing pull request draft until those checks are complete. No existing Synapse, THX, service, or driver configuration needs changing for the source implementation.
