# SnapTap

When A and D are held, the last pressed key takes priority. Releasing it restores the other key if it is still held.

Open **Laptop**, find **SnapTap (A/D)** under **Keyboard**, and turn it on. Release keys and mouse buttons to activate it. SnapTap is off by default and stays running when you close the visible Tray.

SnapTap uses the laptop keyboard through the supported Razer driver on RZ09-0581. The pair uses the A/D scan positions. The information button includes game compatibility details.

## Driver implementation

The Session Agent starts an authenticated current-user supervisor, which starts a separate input worker. Parent image, user, session, process lifetime, preference and coordinated shutdown checks apply to both helpers. A per-user/session mutex prevents concurrent supervisors. A failed helper stays stopped until an explicit off/on transition.

Admission requires both Windows host-identity sources to identify Razer Blade 16 RZ09-0581, the exact `1532:02E0` keyboard collection and control endpoint in the same device container, the reviewed Razer keyboard filter, and pinned `RzDev_02e0.sys` and `RzCommon.sys` content hashes. A competing mapping controller or incomplete discovery rejects admission. Other models need their own driver review. SKU is diagnostic metadata.

The worker installs and independently enumerates only the two canonical A/D hook records. Existing A/D hooks prevent activation. It subscribes only after both records match. The driver consumes those physical events, so the priority processor explicitly forwards each accepted original event or emits the priority transition through the same typed driver output. There is one backend and no Windows-hook fallback.

The supervisor opens its own unsubscribed driver file; it never duplicates the worker's subscribed file object. A non-inherited kill-on-close Windows job contains the worker before its startup gate opens. The worker also holds and checks its authenticated parent process handle. Heartbeats come from the input loop. A missed heartbeat triggers bounded cooperative stop, then termination of that exact child if needed. Driver I/O uses overlapped requests with bounded cancellation; buffers remain alive until completion or process termination.

Normal stop releases a confirmed owned winner in the same foreground, then closes the subscription. The supervisor proves child exit, enumerates the global hook table, removes only canonical owned records, and verifies each removal while preserving unrelated records. It avoids the aggregate disable and global reset commands because those can release unrelated held output. First subscription rechecks keys and mouse buttons immediately before submission for the same reason.

Uncertain output stops further input writes. Closing the last subscribed file allows subsequent physical events through; cleanup does not prove that an earlier output key was released. Recovery reconciles hook configuration only and does not guess missing key releases. A durable ownership journal blocks restart after uncertain output or interrupted supervisor cleanup. Restarting Windows clears that recovery gate once fresh enumeration confirms no A/D hooks remain. Foreground/layout loss while a key is owned also stops the worker.

## Source and verification

The protocol is pinned to `mapping_engine.dll` 1.3.16.5, SHA-256 `6EABDFDEDF797E042738B630D827C06F7A45DBE560EBAEC96C66698F88F3320A`, `RzCommon.sys` 1.0.0.76, SHA-256 `64B72EF9CE409BB17D31096C119B68C4A6A02AB35B7535D763157A4E1A587E59`, and `RzDev_02e0.sys` 1.0.0.78, SHA-256 `BC1666048D81A8DBA6BBFFDBDC86C2119632FD68EF2BB6D5D417FE3B84744787`. No vendor binary is bundled or loaded by this implementation.

The codecs include literal known-answer packets, exact response sizes, ignored undefined padding, fixed-pair validation and bounded enumeration. Hook-session tests exercise ordering, ownership, failure latching and subscription closure. Priority tests compare all 1,024 five-event sequences with an independent press-order oracle.

The earlier user validation applies to the Windows-input implementation. Driver output, external-keyboard exclusion, Reactive lighting attribution, latency, normal shutdown and independent recovery still require attended physical validation on the final build. Keep the driver implementation draft until that validation is complete.
