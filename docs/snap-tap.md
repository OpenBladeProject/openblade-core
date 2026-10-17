# SnapTap

When A and D are held, the last pressed key takes priority. Releasing it restores the other key if it is still held.

Open **Laptop**, find **SnapTap (A/D)** under **Keyboard**, and turn it on. Release keys and mouse buttons to activate it. SnapTap is off by default and stays running when you close the visible Tray.

If the driver is missing, choose **Get driver** to open the RZ09-0581 setup guide in the driver catalog. After setup, turn SnapTap off and on.

SnapTap uses the laptop keyboard through the supported Razer driver on RZ09-0581. The pair uses the A/D scan positions. The information button includes game compatibility details.

## Driver implementation

The Session Agent starts an authenticated current-user supervisor, which starts a separate input worker. Parent image, user, session, process lifetime, preference and coordinated shutdown checks apply to both helpers. A per-user/session mutex prevents concurrent supervisors. A failed helper stays stopped until an explicit off/on transition.

Admission requires both Windows host-identity sources to identify Razer Blade 16 RZ09-0581, the exact `1532:02E0` keyboard collection and control endpoint in the same device container, the reviewed Razer keyboard filter, and pinned `RzDev_02e0.sys` and `RzCommon.sys` content hashes. The active physical console session must own the worker. A competing mapping controller, non-console session or incomplete discovery rejects admission. Other models need their own driver review. SKU is diagnostic metadata.

The worker installs and independently enumerates only the two canonical A/D hook records. Existing A/D hooks prevent activation. It subscribes only after both records match. The driver consumes those physical events, so the priority processor explicitly forwards each accepted original event or emits the priority transition through the same typed driver output. There is one backend and no Windows-hook fallback.

The supervisor opens its own unsubscribed driver file; it never duplicates the worker's subscribed file object. A non-inherited kill-on-close Windows job contains the worker before its startup gate opens. The worker also holds and checks its authenticated parent process handle. Heartbeats come from the input loop. A missed heartbeat triggers bounded cooperative stop, then termination of that exact child if needed. Driver I/O uses overlapped requests with bounded cancellation; buffers remain alive until completion or process termination.

Normal stop releases a confirmed owned winner in the same foreground, then closes the subscription. The supervisor proves child exit, enumerates the global hook table, removes only canonical owned records, and verifies each removal while preserving unrelated records. It avoids the aggregate disable and global reset commands because those can release unrelated held output. First subscription rechecks keys and mouse buttons immediately before submission for the same reason.

Uncertain output stops further input writes. Closing the last subscribed file allows subsequent physical events through; cleanup does not prove that an earlier output key was released. Recovery reconciles hook configuration only and does not guess missing key releases. A durable ownership journal blocks restart after uncertain output or interrupted supervisor cleanup. Restarting Windows clears that recovery gate once fresh enumeration confirms no A/D hooks remain. Foreground/layout loss while a key is owned also stops the worker.

## Source and verification

The protocol is pinned to `mapping_engine.dll` 1.3.16.5, SHA-256 `6EABDFDEDF797E042738B630D827C06F7A45DBE560EBAEC96C66698F88F3320A`, `RzCommon.sys` 1.0.0.76, SHA-256 `64B72EF9CE409BB17D31096C119B68C4A6A02AB35B7535D763157A4E1A587E59`, and `RzDev_02e0.sys` 1.0.0.78, SHA-256 `BC1666048D81A8DBA6BBFFDBDC86C2119632FD68EF2BB6D5D417FE3B84744787`. No vendor binary is bundled or loaded by this implementation.

The codecs include literal known-answer packets, exact response sizes, ignored undefined padding, fixed-pair validation and bounded enumeration. Hook-session tests exercise ordering, ownership, failure latching and subscription closure. Priority tests compare all 1,024 five-event sequences with an independent press-order oracle.

Attended validation on 2026-10-17 used version 0.34.0 at commit `53d044e6d67dc4308e5fbc0b3d9b4ffd16e09b51` on RZ09-0581. A normal-integrity validation launcher exercised the production driver loop and supervisor without installing a new build or changing the saved SnapTap preference. An initial A-hook configuration round trip matched readback and restored the empty hook table before input testing.

Both laptop-keyboard press orders passed: hold A, press and release D, release A; then hold D, press and release A, release D. The window and independent Raw Input observer each recorded 129 events, including repeats, with matching priority transitions, no delivered A+D overlap and no held key at the end. Raw Input attributed every event to the laptop keyboard collection. Normal stop exited successfully and restored the empty hook table; a separate read-only query confirmed it. Installed binaries, preferences and saved status were unchanged after restoration.

The pinned driver keeps hooks per device instance. Its IOCTL wrapper at RVA `0x1A10` obtains the queue's WDF device and typed context, then passes it to dispatch at `0x2E6C`; dispatch and input matching use that context's hook table at `+0x570`. Together with the exact keyboard-filter and container checks, this establishes laptop-only scope from source. No external keyboard was physically exercised.

Additional native checks confirmed that the production kill-on-close job terminates an idle child when its owner dies, and both real helper entrypoints exit cleanly with SnapTap off. These checks used no driver writes. Existing failure tests cover hook ownership, ambiguous submission, restart latching and the reboot-required recovery journal.

The observed laptop Raw Input attribution reaches the unchanged Reactive/Ripple input path; rendered lighting was not separately inspected. Input latency remains unmeasured. The complete enabled installed-agent startup and a live held-output or pending-I/O crash were not exercised.
