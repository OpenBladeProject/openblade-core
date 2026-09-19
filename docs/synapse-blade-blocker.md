# Synapse Blade Blocker coexistence

[Synapse Blade Blocker](https://github.com/OSSBlade/synapse-blade-blocker) is an optional, separately installed patch that lets Synapse serve peripherals while excluding recognized Blade devices. OpenBlade does not install or depend on it.

When every running Synapse AppEngine instance uses an installation with a verified applied patch, OpenBlade excludes those verified AppEngine processes from its competing-controller checks. Independent Razer controllers, including legacy Synapse services, razerwdl and the gaming-services updater, retain their existing checks because the archive patch does not verify their behavior. Hardware identity, supported commands, power requirements and other feature admission checks still apply.

Verification uses the applied patch record and matching installed archive/executable hashes, not a Synapse version allowlist. A new version can work without an OpenBlade update if the patcher can apply its required edits and write a valid record. Missing or malformed records, changed files, mixed patched/unpatched instances and a process started before patch application retain the usual conflict behavior. Restart Synapse after applying the patch. Its short-lived unpatched launcher may still be reported while starting.

Synapse updates can replace the patch. Use the blocker's preparation/apply flow for the new installation and report compatibility failures in its repository. OpenBlade will not treat an old record as proof that the updated application is patched.

This is detection of a cooperating local patch, not a security isolation mechanism or proof that every possible native code path is harmless. The user-reported Pro Click V2 result covers that peripheral and the tested build; other devices and lifecycle paths may behave differently.

## Local validation

On 2026-09-19, a read-only probe of the production controller guard at core revision 466966e84c2114444684d53e2dc309ad1c7f3fc2 recognized four live patched AppEngine 4.0.823 processes, returning no conflicts or detection error. The blocker at revision 1261363e1c53a963f23b06d9cef554d22d5932d5 successfully applied and restored under PowerShell 7.6.5. The user confirmed Pro Click V2 visible, Blade absent and ordinary mouse operation. The original archive and normal installed OpenBlade setup were restored afterward.

The installed OpenBlade binaries were unchanged: this validates live patch-to-guard detection, not the new build's installed UI, every peripheral feature, or full hardware isolation.
