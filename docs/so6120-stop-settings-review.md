# Stop settings review

Claude Opus 5.5 at high effort completed one isolated read-only working-tree
review against base `08f908c8be875dcd7ccca9e27868a9a868310aeb` on 2026-11-07.
The frozen evidence patch SHA-256 was
`756e3ac4c8057ee888f5d6edda7b5e65f1a39106a65d5aa9d78f5272efb8e68d`.
The runner reported `success_native_review`, 50 Read/Glob/Grep calls, no
unexpected tools, and completed snapshot cleanup. Claude did not edit files
or run tests. Codex checked the findings and made the following corrections;
no second Claude review of those corrections is claimed.

| Finding | Verification and disposition |
| --- | --- |
| Custom timing counted allowed read latency as missing telemetry | Confirmed. Each read retains its own two-second freshness bound; continuity measures the uncovered interval before the next read. A genuinely missing interval still ends owned Stop. Fake-clock tests cover both cases. |
| Missing automatic-entry readings permanently paused waiting | Confirmed. Failed, invalid, canceled or slow read-only samples reset the full cool interval while unowned. They do not disable waiting intent. Owned faults and uncertain commands retain recovery fences. |
| Temporary admission failures permanently paused waiting | Confirmed. Out-of-scope and transient unowned admission states reset waiting; fresh entry gates still run before any command. Guardian admission refresh follows completion reconciliation so cached terminal status cannot overwrite the fresh result. Recovery is never treated as a transient retry opportunity. |
| Every full profile transition was classified as an explicit mode change | Confirmed. Pauses now belong to explicit mode/profile intent boundaries. Lower-power transitions retain waiting intent; known increases still pause it, as requested. The review's battery-to-USB-C example remains a deliberate power increase. |
| Sampling gaps and gate contention could prevent entry | Partially confirmed: acquisition latency was counted twice and is corrected. Truly missing observations still reset the interval. The claim that normal contention makes entry rarely possible was not measured. No background writer, stale-sample credit or speculative timing relaxation was added. |
| Resume could overwrite a concurrent pause | Confirmed. Eligibility, generation and pause reason now change in one atomic state transition. Enable/Resume compare against the state captured at ingress; concurrent manual or lifecycle boundaries win, including when already paused. |
| Rejected drafts appeared in the collapsed policy summary | Confirmed. Summaries use service-confirmed policy, while editable drafts have an unsaved indicator. Rejected settings, Resume and direct Stop/Auto actions show failure feedback rather than claiming saved settings. |
| Guardian notifications could sample and start a new Stop | Confirmed. Notification reconciliation cannot enter the automatic-start path. Only the cancelable periodic maintenance path can sample for entry and start Stop. A notification regression verifies no sensor read or new activation. |
| Stop sampling repeatedly closed an ordinary fan-curve GPU session | Confirmed. Stop may borrow an existing unexpired active-D0 session without extending its idle deadline. Temporary, expired, failed or inactive sessions are released. Separate fresh command observations remain; a hidden shared sample cache was not introduced. |
| Disabling was represented as an empty pause reason | This was not independently shown to permit a command because the enabled preference was also checked. The state representation was nevertheless simplified to an explicit Disabled state with regression coverage. |

The first full regression run also identified stale IPC/API/schema expectations
and UI row counts. Their reviewed fixtures were updated for IPC 76, schema 24
and the added settings rows; historical migration and rejection cases remain.
The new localization strings received a separate English/Russian/Chinese
wording review. Disconnected native renders cover independent expansion,
warnings, paused/invalid states and wrapped text.

Trail checks flagged documentation word overlaps with earlier learnings. The
implementation preserves their relevant rules: nested Stop settings, the user's
50 C entry choice, explicit power-state evidence, exact-model command admission,
separate ordinary fan intent, and no uncertain-write replay. It adds no keyboard
or CPU-voltage command, BladeDLL temperature getter, forced Balanced preparation,
or per-activation job around the service. Earlier physical evidence is not
presented as validation of the installed path or the new thermal policies.

No installation, live fan command, or new physical validation was performed.

The final full solution build completed with zero warnings or errors. All
12,455 current test cases have passing results across the 17 project runs and
focused reruns, with no skips. Complete affected suites passed Capture 1,129,
Contracts 603 and Service 2,657. The Tray run passed 2,696 cases and timed out
one localization lifecycle case; that unchanged case passed alone in 20 seconds
under its original 75-second limit. The final focused 26 also passed, including
the additional direct-mode rejection case, bringing Tray coverage to 2,698.
The earlier timeout's cause remains unproven; the first full run was not clean.
