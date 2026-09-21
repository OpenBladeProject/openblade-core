# Synapse Blade Blocker coexistence

[Synapse Blade Blocker](https://github.com/OSSBlade/synapse-blade-blocker) is an optional, separately installed patch that lets Synapse serve peripherals while excluding recognized Blade devices. OpenBlade does not install or depend on it.

## Setup

1. Install and apply the blocker using the instructions in its repository.
2. Restart Synapse so it loads the patched files.
3. Open OpenBlade. If it reports a conflict, check that the patch is still
   applied and that no other Razer controller is running.

After a Synapse update, apply the blocker again to the updated installation.
OpenBlade checks the installed files each time; an old patch record does not
cover a new installation.

## How OpenBlade checks coexistence

When every running Synapse AppEngine instance uses an installation with a verified applied patch, OpenBlade excludes those verified AppEngine processes from its competing-controller checks. Independent Razer controllers, including legacy Synapse services and the gaming-services updater, retain their existing checks. Hardware identity, supported commands, power requirements and other feature admission checks still apply.

Synapse can also start its `razerwdl` Windows lighting helper after the UI loads. OpenBlade excludes this helper only when its expected installation path and product metadata match, its direct parent is a verified patched AppEngine in the same session, and process start times support that parent relationship. Every running helper must satisfy these checks. On every observation, OpenBlade then enumerates present, enabled Windows LampArray interfaces. Any endpoint matching a Blade product ID in the verified patch records, missing endpoint identity, or enumeration failure retains the helper conflict. Independent helpers remain conflicts even when a patched AppEngine is running. There is no helper-version allowlist.

The endpoint check reads SetupAPI interface metadata without opening HID device handles. It matches the HID interface class, usage page `0x59` and usage ID `1` used by [Windows' LampArray selector](https://learn.microsoft.com/en-us/uwp/api/windows.devices.lights.lamparray.getdeviceselector). The result is refreshed rather than cached, so later device arrivals are reconsidered under the existing snapshot-based controller policy. This classifies current competing access; it does not prevent the helper from seeing future devices or patch its native code.

Verification uses the applied patch record and matching installed archive/executable hashes, not a Synapse version allowlist. A new version can work without an OpenBlade update if the patcher can apply its required edits and write a valid record. Missing or malformed records, changed files, mixed patched/unpatched instances and a process started before patch application retain the usual conflict behavior. Restart Synapse after applying the patch. Its short-lived unpatched launcher may still be reported while starting.

Report blocker compatibility failures in the Synapse Blade Blocker repository.

The blocker changes which devices Synapse handles; it does not sandbox Synapse. The trials below cover a Pro Click V2 and the listed builds.

## Local validation

On 2026-09-19, a read-only probe of the production controller guard at core revision 466966e84c2114444684d53e2dc309ad1c7f3fc2 recognized four live patched AppEngine 4.0.823 processes, returning no conflicts or detection error. The blocker at revision 1261363e1c53a963f23b06d9cef554d22d5932d5 successfully applied and restored under PowerShell 7.6.5. The user confirmed Pro Click V2 visible, Blade absent and ordinary mouse operation. The original archive and normal installed OpenBlade setup were restored afterward.

The installed OpenBlade binaries were unchanged: this validates live patch-to-guard detection, not the new build's installed UI, every peripheral feature, or full hardware isolation.

A subsequent installed trial at core revision `7ce1b87c` and blocker revision `50375ea6` exposed a delayed `razerwdl` conflict after Synapse finished loading. The original archive was restored and OpenBlade restarted. This failed trial supersedes the initial snapshot as readiness evidence.

The resulting helper investigation found LampArray enumeration and lighting operations; the resolved dynamic API lookups were filesystem/runtime support and Windows Runtime activation. It found no separate HID, WinUSB or Razer hardware transport. This is evidence for the helper's observed architecture, not a guarantee about future vendor changes. The native endpoint probe found 50 present/enabled HID interfaces with no property errors, including two Pro Click V2 LampArray interfaces and no Blade LampArray interface; the Windows Runtime query independently returned the same two mouse interfaces. Final installed validation must include the loaded Synapse UI and delayed helper.
