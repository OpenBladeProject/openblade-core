---
title: Native profile layer decodes the committed profile bytes
author: Jack
date: 2026-11-21
branch: feat/native-profile-reconcile-core
---

## Decided
- Native thermal and fan requests resolve their profile layer through the domain. `Storage::profile_layer(&Foreground)` reads the committed aggregate once. `typed_profiles` decodes its `profiles` into a typed `ProfileSet`, and `ApplicationContext::report` selects the reported path. `ProfileLayer::at(power)` calls `application::resolve`. `desired`, `gpu_incomplete`, `custom_unsupported`, `desired_fan` and `desired_manual_fans` take one `Option<&Resolution>`.
- The precedence is unchanged: persistent override (4), then temporary (3), then the resolved profile. The profile's source is 2 only when the application's override sets the preset (`Resolution::application_override`); otherwise it is 1.
- Native applies no profile on unclassified external or unknown power. `resolve` returns managed's observed fallback there; `thermal_storage::profile_layer` drops any resolution whose `desired_source()` is not the step's class. Native has no recovered HyperBoost layer: a resolved battery preset under HyperBoost is Plan NoDirectPath.
- A thermal request resolves its layer before it stores the override, as the fan path does before `save_fan`. A document the decoder refuses stores nothing.
- `Foreground.context` stays empty in Stage 4b B1. Each read selects the reported path again in the profiles it read. B2 should select at the report and fill the context.

## Tried and ruled out
- Decoding `aggregate()`'s `serde_json::Value` via `to_vec` then `json::parse`. This host builds serde_json without `preserve_order`, so the Value writes members alphabetically. The domain decoder needs the lighting `kind` discriminator first, so every Static, Ripple, Breathing, Reactive, Tidal, Starlight, Wave, Wheel and AudioMeter profile was refused. Lanes stayed green because every fixture uses `{"kind":"off"}`. Decode the committed `profiles.json` bytes instead (`committed_aggregate`). Apply the schema-26 `normalize::preserve_legacy_independent_profile_intent` to the typed set. Do not enable `preserve_order`.
- Using `desired_intent` for the fan intent. Its `with_mode` turns the legacy Maximum shape (fans 3 with Fixed 5400) into mode 3, where the stored intent keeps mode 1.
- Swapping the persistent and temporary override order as a mutant. `save` always clears the other lifetime, so it is equivalent.

## Watch out
- `Option<&Resolution>` accepts the `None` that old `Option<&str>` application calls passed. `storage.desired(0, None)` compiles and silently drops the profile layer. Use the `saved_desired` and `saved_fan` helpers in readiness_tests/mutation.rs.
- Test fixtures that write a non-off lighting through `MemoryFiles::put` (a `Value`) store `kind` after `color`. Managed and the decoder refuse that. Use `put_plugged_in_lighting` in readiness_tests/profile_layer_lighting.rs, which writes the lighting text in order.
- A thermal pair resolves again only on a class change between setters (`Power::admits` -> `destination_holds`). The fan permit re-resolves before every fan setter.
- 18 vectors that native's `aggregate()` admits are recorded as refused by managed; the corpus test skips them. The profiles lane resolves 93 aggregates and the other lanes 16.
