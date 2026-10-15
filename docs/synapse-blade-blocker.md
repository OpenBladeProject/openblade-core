# Synapse Blade Blocker coexistence

[Synapse Blade Blocker](https://github.com/OpenBladeProject/synapse-blade-blocker) is an optional, separately installed patch that lets Synapse serve peripherals while excluding recognized Blade devices. Its repository is the source of truth for current model coverage and patch instructions. OpenBlade does not install or depend on it.

## Setup

1. Install and apply the blocker using the instructions in its repository.
2. Restart Synapse so it loads the patched files.
3. Open OpenBlade. If it reports a conflict, check that the patch is still
   applied and that no other Razer controller is running.

An update may leave the patched AppEngine files untouched, so the blocker can
remain applied. If Synapse replaces those files, apply the blocker again.
OpenBlade rejects a patch record that no longer matches the installed files.

## How OpenBlade checks coexistence

When every running Synapse AppEngine instance uses an installation with a
verified applied patch and a patched AppEngine is running, OpenBlade excludes
those verified AppEngine processes from competing-controller checks. The two
known Synapse service aliases also stop blocking HyperBoost thermal admission
in that verified state. They remain visible in diagnostics and continue to
participate in other controller checks. Without verified patch evidence, they
block HyperBoost.

The active Windows service whose internal service name is exactly `RGS` remains
visible but does not block HyperBoost thermal admission. A process merely named
`RGS` or `Razer Gaming Services Updater`, or a service that only borrows either
display name, still blocks. Independent Razer controllers retain their existing
checks. Hardware identity, supported commands, power requirements, and other
feature admission checks still apply.

Synapse can also start its `razerwdl` Windows lighting helper after the UI loads. OpenBlade excludes this helper from controller discovery only when its expected installation path and product metadata match, its direct parent is a verified patched AppEngine in the same session, and process start times support that parent relationship. Every running helper must satisfy these checks. On every observation, OpenBlade then enumerates present, enabled Windows LampArray interfaces. Any endpoint matching a Blade product ID in the verified patch records, missing endpoint identity, or enumeration failure retains the helper conflict. Independent helpers remain conflicts even when a patched AppEngine is running. There is no helper-version allowlist. A detected helper with verified Razer path and product metadata does not block HyperBoost thermal admission; one with unverified identity does. A retained helper continues to trigger the other controller checks.

The endpoint check reads SetupAPI interface metadata without opening HID device handles. It matches the HID interface class, usage page `0x59` and usage ID `1` used by [Windows' LampArray selector](https://learn.microsoft.com/en-us/uwp/api/windows.devices.lights.lamparray.getdeviceselector). The result is refreshed rather than cached, so later device arrivals are reconsidered under the existing snapshot-based controller policy. This classifies current competing access; it does not prevent the helper from seeing future devices or patch its native code.

Verification uses the applied patch record and matching installed archive/executable hashes, not a Synapse version allowlist. A new version can work without an OpenBlade update if the patcher can apply its required edits and write a valid record. Missing or malformed records, changed files, mixed patched/unpatched instances and a process started before patch application retain the usual conflict behavior. Restart Synapse after applying the patch. Its short-lived unpatched launcher may still be reported while starting.

Report blocker compatibility failures in the Synapse Blade Blocker repository.

The blocker changes which devices Synapse handles; it does not sandbox Synapse. The trials below cover a Pro Click V2 and the listed builds.

## Local validation

On 2026-09-19, a read-only probe of the production controller guard at core revision 466966e84c2114444684d53e2dc309ad1c7f3fc2 recognized four live patched AppEngine 4.0.823 processes, returning no conflicts or detection error. The blocker at revision 1261363e1c53a963f23b06d9cef554d22d5932d5 successfully applied and restored under PowerShell 7.6.5. The user confirmed Pro Click V2 visible, Blade absent and ordinary mouse operation. The original archive and normal installed OpenBlade setup were restored afterward.

The installed OpenBlade binaries were unchanged: this validates live patch-to-guard detection, not the new build's installed UI, every peripheral feature, or full hardware isolation.

A subsequent installed trial at core revision `7ce1b87c` and blocker revision `50375ea6` exposed a delayed `razerwdl` conflict after Synapse finished loading. The original archive was restored and OpenBlade restarted. This failed trial supersedes the initial snapshot as readiness evidence.

The resulting helper investigation found LampArray enumeration and lighting operations; the resolved dynamic API lookups were filesystem/runtime support and Windows Runtime activation. It found no separate HID, WinUSB or Razer hardware transport. This is evidence for the helper's observed architecture, not a guarantee about future vendor changes. The native endpoint probe found 50 present/enabled HID interfaces with no property errors, including two Pro Click V2 LampArray interfaces and no Blade LampArray interface; the Windows Runtime query independently returned the same two mouse interfaces. Final installed validation must include the loaded Synapse UI and delayed helper.
