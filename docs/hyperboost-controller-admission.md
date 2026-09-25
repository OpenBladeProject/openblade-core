# HyperBoost controller admission

HyperBoost uses a stricter controller policy than ordinary manual performance
controls because entry and restoration coordinate performance, fans, and durable
ownership. Detection must succeed before every relevant thermal submission.

The name-based fallback in `RazerThermalControllerPolicy` exempts these exact,
case-insensitive names from HyperBoost admission. The Windows observer also
checks process or service identity before exempting the helper and updater:

- Razer Chroma SDK Diagnostic Service
- Razer Chroma SDK Server
- Razer Chroma SDK Service
- Razer Chroma Stream Server
- razerwdl
- RGS
- Razer Gaming Services Updater

[Razer's Chroma SDK documentation](https://assets.razerzone.com/dev_portal/docs/index.html)
describes the SDK and its Windows service as a lighting interface. This is the
basis for the Chroma classification; it is not physical coexistence evidence.
The `razerwdl` helper handles Windows lighting, and the `RGS` service is the
gaming-services updater. The updater remains visible in diagnostics. The helper
remains visible when its separate controller checks retain it.
A helper without verified Razer path and product metadata, a process named
`RGS`, or a service borrowing the updater display name remains a HyperBoost
blocker. The helper's other controller checks, lighting
conflict behavior, strict capture, and developer hardware-validation guards
are unchanged.

OpenBlade omits verified patched AppEngine processes from controller discovery.
When every running AppEngine is verified patched, the two known Synapse service
aliases also stop blocking HyperBoost. They remain visible in diagnostics and
continue to participate in other controller checks. Without verification, they
block. Other detected controllers keep their classification. A shared process
can support both peripherals and the laptop; the presence of a mouse or keyboard
does not establish that laptop control is idle. The policy does not discover
additional processes or classify unrecognized software. Enumeration failures
remain blocking.

The same classification applies to HyperBoost entry, exit, pre-submission checks,
explicit recovery, deferred restoration, indeterminate-result restoration, and
power-boundary restoration. Exact-device, power, cooling-pad absence, readback,
ownership, and no-replay requirements remain unchanged. No Razer software is
stopped automatically.

The tray explains unavailable HyperBoost next to the performance controls and
names the blocking components. A lighting-only Chroma observation should not
produce that warning when all other HyperBoost gates pass.

## Validation before readiness

Synthetic coverage must verify Chroma-only entry and restoration, retained
diagnostic visibility, mixed-controller and enumeration-failure rejection,
and warning updates. Before marking the admission change ready, complete the
repository's physical-validation requirements on an admitted device with the
reviewed Chroma components, `razerwdl`, Synapse service aliases, and the RGS updater
present. Exercise each newly exempted component's arrival during operation,
then verify exit and restoration with each still present. Preserve prior
confirmed state and verify restoration. Sleep/resume requires a present user
and fresh approval. Do not infer hardware validation from unit tests or the
documented service role.
