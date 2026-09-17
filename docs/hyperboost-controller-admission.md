# HyperBoost controller admission

HyperBoost uses a stricter controller policy than ordinary manual performance
controls because entry and restoration coordinate performance, fans, and durable
ownership. Detection must succeed before every relevant thermal submission.

The shared `RazerThermalControllerPolicy` exempts only these exact,
case-insensitive lighting service names from HyperBoost admission:

- Razer Chroma SDK Diagnostic Service
- Razer Chroma SDK Server
- Razer Chroma SDK Service
- Razer Chroma Stream Server

[Razer's Chroma SDK documentation](https://assets.razerzone.com/dev_portal/docs/index.html)
describes the SDK and its Windows service as a lighting interface. This is the
basis for the narrow classification; it is not physical coexistence evidence.
The services remain in diagnostics and retain their existing lighting-conflict
behavior. Strict capture and developer hardware-validation guards are unchanged.

RazerAppEngine, razerwdl, both Synapse service aliases, RGS / Razer Gaming
Services Updater, and every other name supplied by controller discovery remain
blocking. A shared process can support both peripherals and the laptop; the
presence of a mouse or keyboard does not establish that laptop control is idle.
The policy does not discover additional processes and is not a generic guarantee
about unrecognized software. Enumeration failures remain blocking.

The same predicate applies to HyperBoost entry, exit, pre-submission checks,
explicit recovery, deferred restoration, indeterminate-result restoration, and
power-boundary restoration. Exact-device, power, cooling-pad absence, readback,
ownership, and no-replay requirements remain unchanged. No Razer software is
stopped automatically.

The tray explains unavailable HyperBoost next to the performance controls and
names the blocking components. A lighting-only Chroma observation should not
produce that warning when all other HyperBoost gates pass.

## Validation before readiness

Synthetic coverage must verify Chroma-only entry and restoration, retained
lighting discovery, mixed-controller and enumeration-failure rejection, and
warning updates. Before marking the admission change ready, complete the
repository's physical-validation requirements on an admitted device with the
reviewed Chroma components present, preserving prior confirmed state and
verifying restoration. Include controller arrival and lifecycle restoration;
sleep/resume requires a present user and fresh approval. Do not infer hardware
validation from unit tests or the documented service role.
