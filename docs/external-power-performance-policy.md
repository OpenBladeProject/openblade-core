# Performance modes on external power

OpenBlade offers every performance preset supported by the detected model on
both sufficient and limited external power, including Custom and HyperBoost
where that model already admits their typed commands. There is no connector or
minimum-wattage threshold for mode choice. Battery-specific presets remain
available on external power when included in the model capabilities.

Adapter classification is unchanged: below-recommendation power remains limited.
Unknown, malformed or contradictory power observations do not admit a write.
Exact-device identity, competing-controller exclusion, current session and
ownership checks, fresh power admission, readback and durable recovery remain
required. CPU voltage tuning is a separate feature with its own admission.

## Firmware behavior

Mode selection uses the existing reviewed commands. It does not modify firmware,
spoof an adapter, or write raw EC power-limit or protection registers. The EC and
other platform firmware continue to choose their source-dependent operating
limits. A selected mode therefore does not promise its full-adapter performance.
Firmware refusal or an unconfirmed result remains visible; uncertain submissions
are reconciled read-only instead of replayed.

For RZ09-0581, the pinned SO6120 MCU 1.07 image SHA-256 is
`90BBE0D8DD1CA31614A2465C7C8821347B2A667606C39BEDADBD59B98886344F`;
the EC 1.04 image SHA-256 is
`8537E9FD55B7B35504D8D87B0191ACC4391B67365B3FE72F6E1029A424C9E707`.
MCU handler `0x96D4` accepts normal mode selectors through 7 and queues them through
the [normal EC bridge](so6120-mcu107-ec-fan-bridge.md). EC routine `0x11770`
selects input-dependent power policy independently of the requested selector;
helper `0x10D68` tests the actual adapter code and a hardware-state flag rather
than the HID actual-versus-recommended comparison. That flag is not established
as a connector identity. This static evidence does not claim physical validation
on an insufficient adapter, nor another model's EC behavior.

## Profiles, transitions and feedback

AC and low-power profiles remain separate and retain the user's settings.
Custom levels, supported GPU clock settings and HyperBoost restoration are not
silently normalized to Balanced merely because external power is limited.
Confirmed battery transitions and suspend/shutdown handback retain their own
cleanup behavior. Changing external sources still uses the destination profile
and current transition authority.

The existing external-power battery-discharge badge and notification remain
unchanged, including their notification preference, live-reading requirements
and charge-limit suppression. They report observed battery drain without
choosing or restricting performance modes.

## Verification status

Offline verification covers source eligibility, exact-model capability filtering,
Custom and HyperBoost application/restoration, source changes and failure paths.
No new low-power physical trial is claimed by this change. Existing physical
captures and developer-only validation plans retain their historical conditions.
