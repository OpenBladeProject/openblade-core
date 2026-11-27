# Changelog

All notable changes to OpenBlade are documented in this file. The project follows
[Semantic Versioning](https://semver.org/).

## [Unreleased]

### Fixed

- Media keys, the function row and SnapTap no longer stop working until a restart after OpenBlade is force-closed, for example from Task Manager, on laptops that use the Razer driver for these keys. OpenBlade clears the key hooks it left behind and resumes on its next start. A force-close before this update still needs one restart. ([#PR](https://github.com/OpenBladeProject/openblade-core/pull/PR))

## [0.77.1] - 2026-11-26

### Added

- Install a newer OpenBlade release from Settings with **Install update**. OpenBlade downloads the installer, checks it against the published release, and opens the usual setup. ([#1013](https://github.com/OpenBladeProject/openblade-core/pull/1013))
- Add GPU core and memory clock offsets on Blade 18 (2025, RZ09-0529) and Blade 14 (2025, RZ09-0530). They need external power and exactly one NVIDIA laptop GPU, and are hidden on battery. Physical validation is pending. ([#991](https://github.com/OpenBladeProject/openblade-core/pull/991))
- Add experimental temporary full charge on Blade 14 (2025, RZ09-0530) and Blade 18 (2025, RZ09-0529). It needs a charge limit set and the charger connected. If Synapse is running with Windows HDR on, turn off HDR and close Synapse first. ([#987](https://github.com/OpenBladeProject/openblade-core/pull/987))
- Use the keyboard's own firmware edition on Blade 14 (2025, RZ09-0530) to pick its regional key map, so Reactive, Ripple and Keyboard .chroma imports include regional Enter, Alt, ISO and Japanese keys. If the edition can't be read, only keys shared by every regional map respond. ([#984](https://github.com/OpenBladeProject/openblade-core/pull/984))
- Turn local dimming off on Blade 16 (2024, RZ09-0510) and Blade 18 (2024, RZ09-0509) with mini-LED displays. Turning it back on stays in Synapse. Diagnostics now list the Synapse CPU power limits, firmware Gaming-mode GPU overclock and HDR-linked local dimming on the models that have them. ([#994](https://github.com/OSSBlade/openblade-core/pull/994))
- Show a notice on the Lighting page when Windows Dynamic Lighting is on and can override OpenBlade keyboard lighting on Blade 16 (2025, RZ09-0528), Blade 18 (2025, RZ09-0529), Blade 18 (2024, RZ09-0509), Blade 16 (2024, RZ09-0510) and Blade 14 (2025, RZ09-0530). On Blade 18 (2025) it also names the Vapor chamber. The notice explains where to turn Dynamic Lighting off. ([#992](https://github.com/OpenBladeProject/openblade-core/pull/992))
- Add the Custom Maximum Fans switch on Blade 14 (2023, RZ09-0482). Starting HyperBoost from Custom turns Maximum Fans off, and ending HyperBoost turns it back on. If CPU Boost or another option set in Synapse is on, turn it off there first. ([#986](https://github.com/OpenBladeProject/openblade-core/pull/986))
- Add HyperBoost on Blade 16 (2024, RZ09-0510) on full-power AC, with or without the Razer cooling pad. It starts from Silent, Balanced, Turbo or Custom and returns to that mode when it ends, or to Balanced on battery. It needs MCU firmware 1.3.0.0 or later. As in Synapse, HyperBoost can't start on Blade 16 (2023 and 2024) configurations with a system SKU Synapse excludes, including some Blade 16 (2023, RZ09-0483) laptops; the app explains why, and HyperBoost can still be turned off. Update the service and app together. ([#993](https://github.com/OpenBladeProject/openblade-core/pull/993))
- Add Fire, Wheel, Starlight, Tidal, Ambient Awareness and Audio Meter keyboard lighting on Blade 15 Advanced (Mid 2019, RZ09-0301), Blade 15 (2022, RZ09-0421), Blade 17 (2022, RZ09-0423) and Blade 14 (2022, RZ09-0427). Start them from Off, Breathing, Wave or, where available, Static. If another app changes the keyboard effect, OpenBlade stops drawing until you choose an effect. These effects haven't been tested on these laptops yet. ([#988](https://github.com/OpenBladeProject/openblade-core/pull/988))
- Light the Vapor Chamber on Blade 18 (2025, RZ09-0529) with the keyboard's Spectrum, Fire, Wheel, Starlight, Tidal, Ripple, Ambient Awareness and Audio Meter effects when its current effect is Off, Static, Breathing or Wave. With any other Vapor Chamber effect, these effects stay on the keyboard. ([#985](https://github.com/OpenBladeProject/openblade-core/pull/985))
- Add an Automatic fan mode for the cooling pad with Quiet, Balanced and Performance profiles, following the CPU, the GPU or both from Razer Synapse's speed tables. The speed changes smoothly with temperature between table points. ([#983](https://github.com/OpenBladeProject/openblade-core/pull/983), [#999](https://github.com/OpenBladeProject/openblade-core/pull/999))
- Run the cooling pad at full speed in Automatic and curve modes when the CPU stays at 95 °C or the GPU at 87 °C for 2 seconds, the same limits as the laptop's manual fan curve. It returns to normal speed once the temperature drops 3 °C. ([#999](https://github.com/OpenBladeProject/openblade-core/pull/999))

### Changed

- Redesign the cooling-pad page with separate Fan and Lighting sections, a status card, sliders for fan speed and brightness, and color pickers in place of hex codes. Changes apply as you make them, and the fan curves use the same editor as the laptop. ([#983](https://github.com/OpenBladeProject/openblade-core/pull/983))
- Smooth the temperatures that drive manual fan curves, on the laptop and the cooling pad, so brief CPU spikes no longer change the fan speed. Missing readings and overheating still raise the laptop fans immediately. ([#983](https://github.com/OpenBladeProject/openblade-core/pull/983))
- The cooling pad now follows **If settings change outside OpenBlade**. When its fan or lighting is changed elsewhere, such as in Synapse or with the pad's lighting button, **Restore them** puts your settings back and **Notify me** hands the pad back with the change in place. Lighting is put back only when **Override external lighting changes** is on; otherwise the new lighting stays, OpenBlade keeps controlling the fan, and the page shows no lighting until you choose one again. To use the pad's fan button, choose **Fixed**; in **Automatic**, OpenBlade keeps setting the fan speed. Update the service. ([#1005](https://github.com/OpenBladeProject/openblade-core/pull/1005))
- Credit the Blade 18 (2025, RZ09-0529) keyboard lighting effects and per-key color editing in 0.65.1-nightly.117 to their contributor. Thanks to [@TheChrisTech](https://github.com/TheChrisTech). ([#977](https://github.com/OpenBladeProject/openblade-core/pull/977))

### Fixed

- The cooling pad could get stuck at full speed on Blade 16 (2026, RZ09-0581) after briefly running at its maximum, because OpenBlade stopped controlling it when the fan sped slightly past 3200 RPM. OpenBlade now keeps controlling the pad and lowers the speed again. ([#1016](https://github.com/OpenBladeProject/openblade-core/pull/1016))
- Links in the app and installer, including About, the changelog and the update check, now go straight to the project's current GitHub home instead of relying on a redirect from its former name. ([#1012](https://github.com/OpenBladeProject/openblade-core/pull/1012))
- The notice shown while Synapse or other Razer software is running listed process names and didn't say what to do. It now names the app, says what it can change, and tells you to quit Synapse from its tray icon. ([#1011](https://github.com/OpenBladeProject/openblade-core/pull/1011))
- Experimental Razer-driver SnapTap and function-row handling now hands A, D and the F keys back to Windows when a key it sends doesn't arrive, instead of leaving them untypeable. Turn the feature off and on to retry. ([#1008](https://github.com/OpenBladeProject/openblade-core/pull/1008))
- Experimental Razer-driver function-row media keys on Blade 16 (2026, RZ09-0581) and Blade 16 (2025, RZ09-0528) now work right away, without pressing Fn first after sign-in, unplugging the charger or switching input methods. ([#1007](https://github.com/OpenBladeProject/openblade-core/pull/1007))
- Fix "Changed outside OpenBlade: Fans" appearing after OpenBlade restarts or updates while the fans are on Automatic and nothing changed them. Update the service. ([#1009](https://github.com/OpenBladeProject/openblade-core/pull/1009))
- Fix the cooling pad going back to its own lighting and fan settings after the laptop sleeps or restarts, or OpenBlade updates. OpenBlade now applies your cooling-pad settings again once Windows is awake and you're signed in, when **If settings change outside OpenBlade** is set to **Restore them**; **Release control** turns this off. Update the service. ([#1004](https://github.com/OpenBladeProject/openblade-core/pull/1004), [#1005](https://github.com/OpenBladeProject/openblade-core/pull/1005))
- Fix the cooling-pad page saying the pad did not accept a change it had accepted. A change the pad refuses, for example while Windows settles after waking, is now tried again a few times, and the page checks the pad by itself while it's open. Update the service. ([#1005](https://github.com/OpenBladeProject/openblade-core/pull/1005))
- Fix OpenBlade losing control of the cooling pad's fan when the pad's lighting button picks a built-in effect OpenBlade can't read. The fan stays under control and the lighting stays as the pad set it; pick another effect on the pad before changing lighting in OpenBlade. Update the service. ([#1005](https://github.com/OpenBladeProject/openblade-core/pull/1005))
- Fix OpenBlade stopping control of the cooling pad when the pad briefly stops answering. With **Restore them**, OpenBlade now reconnects and applies your settings again; with **Notify me**, it stops and says why. Update the service. ([#1005](https://github.com/OpenBladeProject/openblade-core/pull/1005))
- Fix the service treating a stop or update as a crash when only the cooling pad could not be handed back. That started the next run in crash recovery, which could show a false "Changed outside OpenBlade: Fans" notice. The cooling pad still asks you to check it before OpenBlade controls it again. Update the service. ([#1010](https://github.com/OpenBladeProject/openblade-core/pull/1010))
- Fix the GPU temperature going blank while the NVIDIA GPU is on but idle, for example when an app keeps it awake without using it. Update the service. ([#1002](https://github.com/OpenBladeProject/openblade-core/pull/1002))
- Return fans from a fixed speed or fan curve to Automatic without stopping the OpenBlade service. A performance mode chosen while the fans were manual is applied once when they return to Automatic. Clear manual overrides no longer reapplies the cleared mode first. ([#989](https://github.com/OpenBladeProject/openblade-core/pull/989))
- Fix fan and performance controls staying blocked on laptops with GPU clock offsets after a performance or GPU change whose offsets were never changed or whose confirmation was lost. **Resume laptop controls** now clears it, and OpenBlade sends the offsets again with the next change. After a power-source change, the remaining fan, lighting and battery settings still apply if the GPU step fails. ([#991](https://github.com/OpenBladeProject/openblade-core/pull/991))
- Fix the new profile's performance setting being refused after a power-source change when an earlier unconfirmed change had in fact been applied. ([#991](https://github.com/OpenBladeProject/openblade-core/pull/991))
- On Blade 14 and Blade 16 (2023, RZ09-0482 and RZ09-0483), HyperBoost now ends in Balanced when you unplug, turn it off on battery, or the laptop sleeps or shuts down on battery. Your plugged-in mode, Custom included, returns when you plug in again, and your saved battery mode is kept. OpenBlade can also end HyperBoost that Synapse left running with manual fans. ([#993](https://github.com/OpenBladeProject/openblade-core/pull/993))
- Fix Custom Maximum Fans on Blade 16 (2023, RZ09-0483), Blade 18 (2023, RZ09-0484) and Blade 14, 16 and 18 (2024, RZ09-0508, RZ09-0510 and RZ09-0509), where turning only that switch on or off was reported as applied but never reached the laptop. Ending HyperBoost on the Blade 16 turns it back on, and a saved Custom profile now also sets Maximum Fans at startup. ([#986](https://github.com/OpenBladeProject/openblade-core/pull/986))
- Make the cooling pad's fan and lighting controls work when Razer's device drivers are installed, and fix a fixed fan speed pausing control. ([#983](https://github.com/OpenBladeProject/openblade-core/pull/983))
- Apply the new power profile's performance and fan settings when you plug in or unplug after the power adapter briefly couldn't be identified. ([#997](https://github.com/OpenBladeProject/openblade-core/pull/997))
- Keep Razer-driver function-row media keys and SnapTap working after focus briefly leaves every window, such as at the lock screen, and when a key is pressed while Windows is still starting. ([#990](https://github.com/OpenBladeProject/openblade-core/pull/990))
- Keep Razer-driver function-row media keys and SnapTap working when a key or mouse button is held while OpenBlade turns them back on, such as right after switching windows. If they still need recovery, switch input methods or turn SnapTap off and on instead of restarting Windows. ([#1000](https://github.com/OpenBladeProject/openblade-core/pull/1000))
- Fix Razer-driver function-row media keys and SnapTap turning off when the laptop is briefly too busy, such as while a game starts. They now come back on their own. If this keeps happening, switch input methods or turn SnapTap off and on. ([#1006](https://github.com/OpenBladeProject/openblade-core/pull/1006))
- When Windows hook can't take over because Razer driver still holds SnapTap or the function row, the status now says to switch back to Razer driver once instead of suggesting a restart. ([#1006](https://github.com/OpenBladeProject/openblade-core/pull/1006))
- Fix clicking Function keys or Media keys under **Function key behavior**, which opened or closed the card instead of changing the setting. ([#1000](https://github.com/OpenBladeProject/openblade-core/pull/1000))

## [0.65.1-nightly.117] - 2026-11-20

### Added

- Add keyboard-only lighting effects and per-key color editing on Blade 18 (2025, RZ09-0529). Vapor Chamber RGB stays unavailable. ([#977](https://github.com/OpenBladeProject/openblade-core/pull/977))

### Fixed

- Install PawnIO for CPU temperatures when a previous PawnIO removal left only its stopped driver service behind, instead of stopping setup. ([#978](https://github.com/OpenBladeProject/openblade-core/pull/978))

## [0.64.2-nightly.116] - 2026-11-16

### Added

- Add CPU Voltage Optimizer support for Blade 14 (2024, 8945HS) and Blade 14 (2023, 7940HS), with per-core readback and restoration. ([#973](https://github.com/OpenBladeProject/openblade-core/pull/973))

### Fixed

- Fix a valid PawnIO CPU temperature driver being rejected because Windows reports its path differently. Installer logs now include the registered path and package checks. ([#975](https://github.com/OSSBlade/openblade-core/pull/975))
- Wait for the installed service process to exit before replacing or removing OpenBlade files, including broker installs. ([#974](https://github.com/OpenBladeProject/openblade-core/pull/974))

## [0.63.2-nightly.115] - 2026-11-14

### Added

- Offer all model-supported performance modes, including Custom and HyperBoost, on sufficient and limited external adapters. ([#964](https://github.com/OSSBlade/openblade-core/pull/964))
- Expand fixed fan speeds and host curves with per-preset firmware limits, add full-power AC host curves in the existing fixed-fan presets on RZ09-0482/RZ09-0483, and add Creator on RZ09-0328 and Maximum Power on RZ09-0530. Saved fan settings are kept when live limits change. ([#968](https://github.com/OpenBladeProject/openblade-core/pull/968))

### Fixed

- Finish local broker installations by recognizing supported input helpers and waiting for startup to settle before opening OpenBlade. ([#966](https://github.com/OSSBlade/openblade-core/pull/966))
- Keep HyperBoost recovery tied to the confirmed power source after an upgrade, and stop shared native build caches from reusing stale code or release metadata. ([#964](https://github.com/OSSBlade/openblade-core/pull/964))
- Accept a valid PawnIO CPU temperature driver when uninstall and reinstall leaves disconnected device registrations. ([#970](https://github.com/OSSBlade/openblade-core/pull/970))
- Keep ordinary controls available when Windows confirms external power but the adapter wattage can't be read. ([#967](https://github.com/OpenBladeProject/openblade-core/issues/967))
- Fix supported battery-setting replies being rejected, and allow a short confirmation window on source-reviewed Blade models. ([#965](https://github.com/OSSBlade/openblade-core/pull/965))

## [0.61.1-nightly.114] - 2026-11-13

### Added

- Expand lighting with independent brightness and available logo controls on 15 Blade profiles, MCU-backed keyboard effects on seven models, and software Fire on Blade 16 RZ09-0581. ([#960](https://github.com/OSSBlade/openblade-core/pull/960))

### Fixed

- Open the matching settings page and scroll to its card when you select a profile summary, including cards hidden in Customize. Show the active profile first in the compact view. ([#961](https://github.com/OSSBlade/openblade-core/pull/961))
- Show the failing installer step and underlying error instead of a generic error code. The main installer log now includes Setup diagnostics. ([#952](https://github.com/OSSBlade/openblade-core/pull/952))
- Fix unlit ISO and Japanese keys in keyboard lighting on Blade 16 RZ09-0581 and RZ09-0528. ([#958](https://github.com/OSSBlade/openblade-core/pull/958))
- Explain unavailable startup and lighting controls when another Razer app is running or can't be checked. ([#954](https://github.com/OSSBlade/openblade-core/pull/954))

## [0.60.0-nightly.113] - 2026-11-12

### Added

- Add Static, Ambient Awareness, Audio Meter, and software lighting effects on Blade 14 (2023/2025) and Blade 16 (2023). ([#953](https://github.com/OSSBlade/openblade-core/pull/953))
- Add Reactive, Ripple, and Keyboard `.chroma` imports on Blade 14 (2025). ([#953](https://github.com/OSSBlade/openblade-core/pull/953))
- Support Blade 15 Advanced (Mid 2021) RZ09-0409 and both 2019 Blade Pro 17 variants, RZ09-0287 and RZ09-0314, with Balanced/Custom performance and Automatic fan handback. Physical validation is pending. ([#951](https://github.com/OSSBlade/openblade-core/pull/951))

## [0.58.7-nightly.112] - 2026-11-12

### Fixed

- Fix keyboard and logo controls blocked by Wave readback, and avoid unnecessary lighting retries. ([#947](https://github.com/OpenBladeProject/openblade-core/pull/947))
- Keep laptop and cooling-pad fan curves responsive to hot, idle GPUs while skipping temperature reads for GPUs reported powered down. Require GPU cooling evidence before Battery Saver fan stop. ([#946](https://github.com/OpenBladeProject/openblade-core/pull/946))

## [0.58.5-nightly.111] - 2026-11-10

### Fixed

- Fix internal display brightness errors when Windows omits the change status. ([#942](https://github.com/OSSBlade/openblade-core/pull/942))
- Let Setup recover from a dropped service connection during startup. ([#942](https://github.com/OSSBlade/openblade-core/pull/942))

## [0.58.4] - 2026-11-09

### Fixed

- Suppress the plugged-in battery discharge warning above, at, or up to five percentage points below the active charge limit. ([#935](https://github.com/OpenBladeProject/openblade-core/pull/935))
- Fix false failure messages after changing internal display brightness. ([#939](https://github.com/OSSBlade/openblade-core/pull/939))

## [0.58.2-nightly.110] - 2026-11-09

### Fixed

- Fix laptop detection after a fresh install or upgrade by restoring the required machine-data permissions. ([#912](https://github.com/OSSBlade/openblade-core/pull/912))

## [0.58.1] - 2026-11-09

### Added

- Add Stop/Auto for Blade 14 RZ09-0427/0530, Blade 15 RZ09-0330/0421 and Blade 16 RZ09-0483/0510/0528 on reviewed EC versions, including the Lamborghini variant. ([#901](https://github.com/OSSBlade/openblade-core/pull/901))

### Fixed

- Keep Silent, Performance, and Custom when changing between Automatic and manual fans on full adapter power on Blade 16 RZ09-0581 and RZ09-0528. Apply AC performance presets directly when leaving HyperBoost. ([#907](https://github.com/OSSBlade/openblade-core/pull/907))

## [0.57.1-nightly.109] - 2026-11-08

### Added

- Add experimental Stop/Auto fan controls for the Blade 16 RZ09-0581 in USB-C Balanced, with rolling average temperature limits, immediate cutoffs and automatic stopping below 50°C. ([#879](https://github.com/OpenBladeProject/openblade-core/pull/879))
- Bundle CPU temperature monitoring through LibreHardwareMonitor and PawnIO. ([#879](https://github.com/OpenBladeProject/openblade-core/pull/879))
- Add an offline firmware CLI, register explorer and developer diagnostics. ([#879](https://github.com/OpenBladeProject/openblade-core/pull/879))
- Control Razer Laptop Cooling Pad fan targets, temperature curves, lighting and keyboard lighting sync from its own page, shown when connected. Support is experimental. ([#889](https://github.com/OpenBladeProject/openblade-core/pull/889))
- Support Blade 15 Advanced (Mid 2019) RZ09-0301 with Balanced/Custom performance, Balanced fixed fans and host curves on full-power AC, independent keyboard brightness and logo controls. ([#890](https://github.com/OpenBladeProject/openblade-core/pull/890))
- Search OpenBlade pages and settings from the title bar. Press Ctrl+K to open search and jump to a result. ([#883](https://github.com/OpenBladeProject/openblade-core/pull/883))
- Adjust the running CPU's all-core Voltage Optimizer on Blade 16 (2025, RZ09-0528) with the supported AMD driver already running. Off keeps the remembered offset. ([#887](https://github.com/OpenBladeProject/openblade-core/pull/887))
- Support Blade 15 Base (2019) RZ09-0300, Blade 15 Advanced (Early 2020) RZ09-0330 and Blade 15 Base (Late 2020) RZ09-0351 with preset controls and Automatic fans. Advanced Custom CPU/GPU levels are limited to Low/Medium/High. Physical validation is pending. ([#888](https://github.com/OSSBlade/openblade-core/pull/888))
- Support Blade 14 (2024) RZ09-0508, Blade 16 (2024) RZ09-0510, Blade 18 (2024) RZ09-0509 and Blade 18 (2025) RZ09-0529 with model-specific controls. ([#888](https://github.com/OSSBlade/openblade-core/pull/888))
- Support Blade 14 (2021) RZ09-0370, Blade 14 (2022) RZ09-0427, Blade 15 (2022) RZ09-0421 and Blade 17 (2022) RZ09-0423 with performance controls and model-specific charge limits. ([#888](https://github.com/OSSBlade/openblade-core/pull/888))
- Add model-specific Turbo, Maximum Power, Gaming and Creator presets, each with separate Windows power-mode preferences. ([#888](https://github.com/OSSBlade/openblade-core/pull/888))

### Changed

- Use a Rust component for reports and selected profile merges. The Windows UI and service remain C#. ([#895](https://github.com/OpenBladeProject/openblade-core/pull/895), [#897](https://github.com/OpenBladeProject/openblade-core/pull/897))
- Shorten the README support section in English, Chinese and Russian, with model details in the support guide. ([#890](https://github.com/OpenBladeProject/openblade-core/pull/890))
- Document exact BIOS/EC source findings for Blade 16 RZ09-0581 and RZ09-0528. ([#890](https://github.com/OpenBladeProject/openblade-core/pull/890))
- Open Labs from the sidebar to learn about experimental features and jump to their controls. A beaker marks experimental controls on the Lighting page. ([#885](https://github.com/OpenBladeProject/openblade-core/pull/885))
- List reviewed laptops as officially supported and track physical validation separately. ([#888](https://github.com/OSSBlade/openblade-core/pull/888))

### Fixed

- Fix CPU temperatures stuck at 45°C. ([#879](https://github.com/OpenBladeProject/openblade-core/pull/879), [#890](https://github.com/OpenBladeProject/openblade-core/pull/890))
- Use Custom temperature cutoffs for Stop entry and improve fan-state diagnostics and recovery. ([#879](https://github.com/OpenBladeProject/openblade-core/pull/879))
- Change directly between Fixed fans and host curves in an admitted preset. ([#890](https://github.com/OpenBladeProject/openblade-core/pull/890))
- Stop treating constant ACPI values as CPU temperature, so custom fan curves use the maximum-speed fallback when no CPU sensor is available. ([#890](https://github.com/OpenBladeProject/openblade-core/pull/890))
- Fix cancellation being lost when a queued service request resumes. ([#887](https://github.com/OpenBladeProject/openblade-core/pull/887))
- Keep the original diagnostic stage when a CPU read times out. ([#887](https://github.com/OpenBladeProject/openblade-core/pull/887))
- Keep older app versions in a readable, read-only upgrade view when the service has newer presets and settings. ([#888](https://github.com/OSSBlade/openblade-core/pull/888))
- Recover thermal controls when unrelated charge-limit readings are unavailable. ([#888](https://github.com/OSSBlade/openblade-core/pull/888))
- Clear Maximum Fans before applying Battery Balanced on Blade 14, 16 and 18 (2024), including after disconnecting AC power. ([#888](https://github.com/OSSBlade/openblade-core/pull/888))
- Allow paused charge-limit controls to resume after successful recovery. ([#888](https://github.com/OSSBlade/openblade-core/pull/888))
- Keep saved Windows power modes, app rules and brightness when migrating profiles. ([#888](https://github.com/OSSBlade/openblade-core/pull/888))
- Apply and verify the internal display's active signal rate when selecting a fixed refresh rate. ([#886](https://github.com/OpenBladeProject/openblade-core/pull/886))
- Keep Dynamic Refresh Rate available after choosing a low fixed rate when the panel and driver support a higher timing. ([#886](https://github.com/OpenBladeProject/openblade-core/pull/886))
- Apply an app profile's refresh rate only when OpenBlade can restore the previous rate. ([#886](https://github.com/OpenBladeProject/openblade-core/pull/886))

## [0.51.0-nightly.107] - 2026-11-05

### Added

- Choose F1–F12 or multimedia keys as the default on Blade 18 (2023) with the Razer driver. This input path is experimental. ([#880](https://github.com/OpenBladeProject/openblade-core/pull/880))

### Fixed

- Keep confirmed fixed-fan settings when reapplying the current performance preset. ([#880](https://github.com/OpenBladeProject/openblade-core/pull/880))
- Keep saved function-key and Gaming Mode preferences when saving or discarding other laptop edits. ([#880](https://github.com/OpenBladeProject/openblade-core/pull/880))
- Show Blade 18's default Fn shortcuts without a paused warning, and stop status checks from interrupting Razer-driver remapping. ([#880](https://github.com/OpenBladeProject/openblade-core/pull/880))
- Reduce memory growth when changing app language by sharing localized resource lookups across the main window and quick controls. ([#874](https://github.com/OpenBladeProject/openblade-core/pull/874))
- Reduce memory growth when closing the main and quick-controls windows to the tray. ([#874](https://github.com/OpenBladeProject/openblade-core/pull/874))
- Keep window size limits while reducing memory growth during language changes. ([#874](https://github.com/OpenBladeProject/openblade-core/pull/874))
- Reuse native windows, backdrops and icons when changing language, and release retired views and their event subscriptions. ([#874](https://github.com/OpenBladeProject/openblade-core/pull/874))
- Release XAML bindings and title-bar resources when closing the main and quick-controls windows, including after a failed window start. ([#874](https://github.com/OpenBladeProject/openblade-core/pull/874))
- Keep display edits and refresh messages in order and use current brightness levels. ([#874](https://github.com/OpenBladeProject/openblade-core/pull/874))
- Keep brightness-failure diagnostics and let display automation recover after a canceled manual change. ([#874](https://github.com/OpenBladeProject/openblade-core/pull/874))
- Avoid refreshing unrelated controls when THX audio settings haven't changed. ([#874](https://github.com/OpenBladeProject/openblade-core/pull/874))
- Keep overlapping profile edits, refresh service state before later saves, and keep drafts when a save is rejected or its result is uncertain. ([#874](https://github.com/OpenBladeProject/openblade-core/pull/874))
- Follow the latest telemetry replay selection and keep intermittent readings visible without drawing chart lines across gaps. ([#874](https://github.com/OpenBladeProject/openblade-core/pull/874))
- Remove failed driver-download files before installation begins. Packages are kept after any installation attempt or uncertain result. ([#877](https://github.com/OpenBladeProject/openblade-core/pull/877))
- Keep driver installation recovery required for malformed journals or missing THX packages, keep extraction files after uncertain termination, and tell timeouts from cancellation. ([#877](https://github.com/OpenBladeProject/openblade-core/pull/877))
- Keep invalid configuration and the original files when a save and its rollback both fail. ([#876](https://github.com/OpenBladeProject/openblade-core/pull/876))
- Keep settings tied to the correct Windows user across interrupted activation and overlapping requests. ([#876](https://github.com/OpenBladeProject/openblade-core/pull/876))
- Report incomplete profile application accurately, and keep uncertain laptop-control writes across restarts without replaying them. ([#876](https://github.com/OpenBladeProject/openblade-core/pull/876))
- Keep lighting recovery consistent across supported laptops. ([#876](https://github.com/OpenBladeProject/openblade-core/pull/876))
- Keep keyboard-hook ownership when replacement cleanup fails, and stop replacements until cleanup is confirmed. ([#876](https://github.com/OpenBladeProject/openblade-core/pull/876))
- Follow playback-device changes in Audio Meter lighting and keep configured THX profile values through saving and loading. ([#876](https://github.com/OpenBladeProject/openblade-core/pull/876))
- Keep display-automation pauses after partially submitted changes and keep the latest activated app profile. ([#876](https://github.com/OpenBladeProject/openblade-core/pull/876))
- Stop developer firmware-validation writes after unconfirmed changes or restoration failures, limit CPU probe callback buffering, and report native failures without waiting. ([#878](https://github.com/OpenBladeProject/openblade-core/pull/878))

### Changed

- Reduce telemetry chart allocations and redraw each live sample once, while keeping freshness labels and missing-sample time current. ([#874](https://github.com/OpenBladeProject/openblade-core/pull/874))
- Release worker threads between GPU activity samples and cancel pending sample waits promptly. ([#876](https://github.com/OpenBladeProject/openblade-core/pull/876))
- Reduce service and battery IPC decoding overhead. ([#876](https://github.com/OpenBladeProject/openblade-core/pull/876))

## [0.50.3] - 2026-11-01

### Added

- Choose Windows hook or Razer driver separately for SnapTap and multimedia keys. ([#867](https://github.com/OSSBlade/openblade-core/pull/867))
- Warn as soon as battery discharge is detected while plugged in, with a tray badge and configurable notification. ([#866](https://github.com/OSSBlade/openblade-core/pull/866), [#871](https://github.com/OpenBladeProject/openblade-core/pull/871))
- Reorder customizable cards and side-menu items by dragging them directly, with changes saved immediately. ([#861](https://github.com/OpenBladeProject/openblade-core/pull/861))
- Show what's new automatically on the first launch of each version, with offline release notes and a button to reopen them from About. ([#862](https://github.com/OSSBlade/openblade-core/pull/862))

### Changed

- Explain which keyboard input method to try first, with game compatibility notes for Windows hook. ([#872](https://github.com/OpenBladeProject/openblade-core/pull/872))
- Group multimedia key input and SnapTap input beneath their related keyboard settings, with collapsible sections and brief descriptions. ([#870](https://github.com/OpenBladeProject/openblade-core/pull/870))
- Reorder cards within their section and move complete sections by their headings. The dragged item follows the cursor at half opacity while nearby items move into place. ([#868](https://github.com/OpenBladeProject/openblade-core/pull/868))
- Move Windows battery reports into Telemetry with a labeled power-source timeline, explicit unknown periods and a compact empty state, and remove the Long battery life preset. ([#869](https://github.com/OpenBladeProject/openblade-core/pull/869))
- Refine the application icon's laptop proportions and replace the white README logo with a dark-display, silver-deck color treatment. ([#860](https://github.com/OpenBladeProject/openblade-core/pull/860))
- Refresh the application and installer icons with the Monolith laptop design and add matching light/dark logos. ([#859](https://github.com/OpenBladeProject/openblade-core/pull/859))

### Fixed

- Fix a dashboard loading crash during application language changes. ([#867](https://github.com/OSSBlade/openblade-core/pull/867))
- Make tray power sources and performance modes easier to tell apart, with clearer monochrome icons and a dedicated HyperBoost symbol. ([#858](https://github.com/OpenBladeProject/openblade-core/pull/858))
- Keep local-development installer logs after rollback and recognize Session Agent helpers during deployment and relaunch. ([#858](https://github.com/OpenBladeProject/openblade-core/pull/858))

## [0.44.1-nightly.105] - 2026-10-28

### Fixed

- Let SnapTap recover automatically from a brief loss of foreground focus while idle. It still stops for held keys and uncertain input. ([#854](https://github.com/OpenBladeProject/openblade-core/pull/854))

## [0.44.0-nightly.104] - 2026-10-26

### Added

- Install SnapTap drivers through Windows Update first on all reviewed Blade models, with a pinned-package fallback when the first scan finds no matching offer. ([#855](https://github.com/OpenBladeProject/openblade-core/pull/855))

### Fixed

- Keep keyboard driver setup visible for incomplete stacks, and keep blocked checks and Windows Update results in diagnostics. ([#855](https://github.com/OpenBladeProject/openblade-core/pull/855))
- Wait for SnapTap driver devices to appear after installation, keep canceled pre-install checks retryable, and tell refused driver offers apart from Windows Update availability problems. ([#855](https://github.com/OpenBladeProject/openblade-core/pull/855))

## [0.43.0-nightly.103] - 2026-10-25

### Added

- Offer **Install driver** for all seven reviewed SnapTap models, including eligible partial stacks. Setup keeps detailed failure reports and unrelated drivers. ([#852](https://github.com/OpenBladeProject/openblade-core/pull/852))

## [0.42.2-nightly.102] - 2026-10-24

### Fixed

- Keep error messages, stack locations and cause chains in diagnostics, along with driver and controller discovery details and log-writer health. Larger diagnostic archives now download fully. ([#850](https://github.com/OpenBladeProject/openblade-core/pull/850))

## [0.42.1-nightly.101] - 2026-10-24

### Fixed

- Keep failure stages and native error details in SnapTap, Windows battery report, Session Agent and THX diagnostics. Support exports now include SnapTap runtime failures, and one unreadable log no longer hides the others. ([#847](https://github.com/OpenBladeProject/openblade-core/pull/847))

## [0.42.0-nightly.100] - 2026-10-23

### Added

- Compare telemetry sensors in separate temperature, power and fan-speed lanes with a shared time axis and an analysis table. Choose visible metrics and custom colors, drag the time window or edit exact bounds, and inspect raw-sample values and range statistics. Pinned moments stay fixed as live readings arrive. ([#841](https://github.com/OpenBladeProject/openblade-core/pull/841))

### Changed

- Telemetry is off by default. A saved Enable telemetry switch turns it on; turning it off stops history collection and ends any active CSV recording. ([#841](https://github.com/OpenBladeProject/openblade-core/pull/841))
- Show timestamped sensor values beside the telemetry crosshair on hover. Use flat toolbar controls, grouped sample navigation, a live-follow toggle and an inline Full range action beside the timeline label. ([#841](https://github.com/OpenBladeProject/openblade-core/pull/841))
- Standardize app typography on the native WinUI type scale and shared command sizing. Telemetry table headers use strong text and body cells use regular text with tabular numerals. ([#841](https://github.com/OpenBladeProject/openblade-core/pull/841))

### Fixed

- Align telemetry status text with the CSV actions and center toolbar content at native display scaling. ([#841](https://github.com/OpenBladeProject/openblade-core/pull/841))
- Hide graph readouts, crosshairs and sample markers when the pointer leaves, while pinned table values stay. Hover inspection keeps working after range edits, and selection survives starting or stopping a recording. Expired readings and adjusted time bounds are now explained, and an unknown saved sensor no longer discards other preferences. ([#841](https://github.com/OpenBladeProject/openblade-core/pull/841))
- Keep application and display automation current after cancellation or rapid app switches, and let telemetry recording stop cleanly after a disk error. ([#845](https://github.com/OSSBlade/openblade-core/pull/845))
- Avoid unnecessary control pauses when a performance, fan or battery change is canceled or blocked before it is sent. Keep shutdown cleanup bounded. ([#845](https://github.com/OSSBlade/openblade-core/pull/845))
- Fix stale keyboard colors flashing before the selected effect when activity wakes idle lighting, on all supported Blade models. ([#844](https://github.com/OSSBlade/openblade-core/pull/844))
- Keep the minimize, maximize and close buttons in sync with the window theme, including when following Windows. ([#843](https://github.com/OSSBlade/openblade-core/pull/843))

## [0.41.1-nightly.99] - 2026-10-22

### Changed

- Use sliders with percentage readouts for display brightness and application brightness overrides. Show Unavailable for the live display control when its brightness is unknown. ([#837](https://github.com/OSSBlade/openblade-core/pull/837))
- Move application profile editing and installed-game discovery to full-width pages with Back and Save actions, searchable game groups, executable icons and fewer duplicate suggestions. Game executables stay available next to launchers and bootstrappers. ([#837](https://github.com/OSSBlade/openblade-core/pull/837))
- Add timestamp and value axes, a vertical crosshair, same-sample readings and keyboard inspection controls to telemetry charts. Show the selected time window and missing data without interpolation. ([#837](https://github.com/OSSBlade/openblade-core/pull/837))

### Fixed

- Stop rebuilding hidden telemetry charts while recording continues in the background. ([#837](https://github.com/OSSBlade/openblade-core/pull/837))

## [0.41.0-nightly.98] - 2026-10-21

### Added

- Add application profiles for specific game and app executables, with foreground or activated-process lifetime rules, separate power-source overrides, HDR and brightness settings, installed-game discovery, and profile import and export. Manual controls take priority, and automation keeps running in the Session Agent. ([#835](https://github.com/OSSBlade/openblade-core/pull/835))
- Add telemetry charts with raw-sample statistics, gaps for missing readings, power and profile events, source and freshness labels, and local CSV recording and replay. ([#835](https://github.com/OSSBlade/openblade-core/pull/835))
- Add per-source display brightness, Windows battery history with capacity and activity details, and a previewable Long battery life preset. ([#835](https://github.com/OSSBlade/openblade-core/pull/835))

### Fixed

- Keep display automation correct across application changes, manual controls, cancellation and restart, and resume after an uncertain state only once it is confirmed. Stop guessing at refresh-rate rollback. ([#835](https://github.com/OSSBlade/openblade-core/pull/835))

## [0.40.0] - 2026-10-21

### Added

- Add a Touchpad switch on the Laptop page to turn the Windows Precision Touchpad on or off, with English, Russian and Chinese text. Remove the redundant On/Off text beside the Gaming mode switch. ([#830](https://github.com/OSSBlade/openblade-core/pull/830))

## [0.39.0-nightly.97] - 2026-10-20

### Added

- Add experimental spatial-audio control through the installed legacy THX COM service on Blade 14 RZ09-0482, Blade 16 RZ09-0483 and Blade 18 RZ09-0484. Legacy installation stays blocked until its setup sequence is verified. Identify RZ09-0328 as Dolby Atmos. Add a developer-only read-only collector for the original legacy binding evidence. ([#819](https://github.com/OpenBladeProject/openblade-core/pull/819), [catalog #27](https://github.com/OpenBladeProject/blade-driver-catalog/pull/27))
- Install the matching THX driver and configuration without Synapse on RZ09-0581, RZ09-0528 and RZ09-0530, using verified original installers from Razer or the driver catalog. Experimental fresh setup also installs the required Realtek or Intel base audio driver and keeps unrecognized installed builds. Clean installation on RZ09-0581 has not been physically tested. ([#819](https://github.com/OpenBladeProject/openblade-core/pull/819), [catalog #27](https://github.com/OpenBladeProject/blade-driver-catalog/pull/27))
- Extend driver-backed SnapTap to all seven supported Blade models. The newly added models show experimental status and use their own Razer driver setup, and diagnostics name any failed check. ([#828](https://github.com/OSSBlade/openblade-core/pull/828))

### Changed

- Accept compatible installed THX, SnapTap and native-brightness driver updates without a release, instead of matching exact driver builds. Downloads and installers keep their hash checks. Record this policy in the agent guide. ([#819](https://github.com/OpenBladeProject/openblade-core/pull/819))

### Fixed

- Accept the vendor performance, fan and Maximum Fans replies on experimental Blade 18 (2023) and Blade 16 (2023). ([#826](https://github.com/OpenBladeProject/openblade-core/pull/826))
- Stop startup-animation and other firmware controls from loading forever after an unconfirmed read, and name the check that blocks experimental writes. ([#826](https://github.com/OSSBlade/openblade-core/pull/826))
- Include Blade 18 Fn shortcut input and execution checkpoints in exported diagnostics. ([#826](https://github.com/OSSBlade/openblade-core/pull/826))

## [0.37.3-nightly.96] - 2026-10-19

### Changed

- Simplify the SnapTap information popup in English, Russian and Simplified Chinese, and fix its localization review snapshots. ([#818](https://github.com/OSSBlade/openblade-core/pull/818))

### Fixed

- Keep unrelated controls available on all Blade models when a battery or thermal state query fails. Optional GPU and sign-in checks no longer block state updates or Tray startup. ([#824](https://github.com/OpenBladeProject/openblade-core/pull/824))

## [0.37.1-nightly.95] - 2026-10-19

### Fixed

- Restore Blade 18 controls when an optional startup-lighting or keyboard-layout read fails during startup. Diagnostics now show the failed query, including in exported firmware diagnostics. ([#820](https://github.com/OpenBladeProject/openblade-core/pull/820))

## [0.37.0-nightly.94] - 2026-10-19

### Added

- Download the missing THX client directly from Razer, with hash and signature verification. The matching audio driver is still required. Improve Chinese and Russian THX wording. ([#816](https://github.com/OpenBladeProject/openblade-core/pull/816))

### Changed

- Store the sanitized protocol evidence OpenBlade needs in the core repository, so public clones, builds and tests no longer need the private legacy capture repository. Use Synapse Observer for new evidence. ([#815](https://github.com/OpenBladeProject/openblade-core/pull/815))

## [0.36.0-nightly.93] - 2026-10-18

### Added

- Add experimental Blade 18 per-key color imports and sequence playback, single- and dual-color Breathing, leftward Wave, host Gaming Mode key blocking, startup animation and temporary full charge, independent curve targets in Balanced and linked fixed fans in Silent. Entering Custom returns fans to Automatic and keeps the saved fan setting. ([#786](https://github.com/OpenBladeProject/openblade-core/pull/786))
- Add experimental Reactive, Ripple, Tidal, Fire, Wheel, Starlight, whole-keyboard Ambient Awareness and Audio Meter lighting for Blade 18 (2023). Add its default Synapse Fn shortcuts for performance, refresh rate, touchpad, F1–F12, arrows and Delete. ([#786](https://github.com/OpenBladeProject/openblade-core/pull/786))
- Add all Custom CPU/GPU level combinations, including CPU Boost with GPU Medium and High/High, and a shared 2200–3800 RPM fixed-fan control in Balanced on Blade 18 (2023). Show richer Synapse lighting effects in diagnostics. ([#786](https://github.com/OSSBlade/openblade-core/pull/786))
- Install the supported SnapTap keyboard driver from the catalog with one button, with package verification and clear setup results. ([#811](https://github.com/OpenBladeProject/openblade-core/pull/811))

### Changed

- Clarify Blade 18 temperature-source limits, and name the temporary full charge, CPU Boost and local dimming flags in experimental Power Mode Control diagnostics. ([#786](https://github.com/OpenBladeProject/openblade-core/pull/786))
- Run SnapTap through the supported Razer laptop keyboard driver on RZ09-0581, with a short information popup. ([#809](https://github.com/OSSBlade/openblade-core/pull/809))

### Fixed

- Keep older clients read-only when new keyboard layouts are present, and leave external keyboard input alone in Blade 18 Fn handling. ([#786](https://github.com/OpenBladeProject/openblade-core/pull/786))
- Pause lighting after a partial frame batch, including after cancellation between rows, and recheck the controller for each row. This also covers the already validated models. ([#786](https://github.com/OpenBladeProject/openblade-core/pull/786))
- Use the observed logo-lighting selector on Blade 18 (2023) for reading, changing and restoring logo lighting. ([#786](https://github.com/OSSBlade/openblade-core/pull/786))
- Fix Custom-state reads on Blade 16 (2023) and Blade 18 (2023), and use the vendor Custom query on Blade 18. ([#786](https://github.com/OSSBlade/openblade-core/pull/786))
- Show the specific THX detection failure and include a fresh THX detection report in exported diagnostics. ([#805](https://github.com/OSSBlade/openblade-core/pull/805))

## [0.33.1-nightly.92] - 2026-10-17

### Changed

- Cache the verified Windows Desktop Runtime installer across full builds and worktrees. ([#800](https://github.com/OpenBladeProject/openblade-core/pull/800))
- Simplify app helper text and confirmation dialogs in English, Simplified Chinese and Russian. ([#797](https://github.com/OpenBladeProject/openblade-core/pull/797))

### Added

- Add an opt-in experimental A/D SnapTap feature that gives priority to the last-pressed key across keyboards. ([#794](https://github.com/OpenBladeProject/openblade-core/pull/794))
- Add experimental, opt-in controls on a dedicated THX Audio page for Blade 16 RZ09-0581 and RZ09-0528 and Blade 14 RZ09-0530, including custom EQ and audio enhancements. They use the THX components already installed, and all three models support saved application routing. Settings load automatically, with a read button for recovery. A warning explains when Windows audio enhancements bypass THX. Spatial changes were confirmed audibly on RZ09-0581 speakers with Windows enhancements on; other outputs and models still need testing. ([#790](https://github.com/OSSBlade/openblade-core/pull/790))
- Add experimental HyperBoost with or without the cooling pad for Blade 14 (2023), RZ09-0482, and Blade 16 (2023), RZ09-0483. Use without the pad awaits community hardware testing. ([#791](https://github.com/OSSBlade/openblade-core/pull/791))

### Fixed

- Resume text auto-repeat for restored SnapTap holds, and group SnapTap with the Keyboard controls without redundant normal-state labels. ([#803](https://github.com/OpenBladeProject/openblade-core/pull/803))
- Avoid a Session Agent shutdown error when playback or microphone mute subscriptions finish after cancellation. ([#802](https://github.com/OpenBladeProject/openblade-core/pull/802))
- Remove duplicate padding inside nested settings, match THX subcell spacing and hover states to Custom performance, hide empty application-rule spacing, and keep service status available when many rules are saved. ([#790](https://github.com/OpenBladeProject/openblade-core/pull/790))
- Keep Blade 14 (2023) and Blade 16 (2023) battery fan changes in DC Balanced, and reject power-source changes before they are sent. ([#791](https://github.com/OSSBlade/openblade-core/pull/791))
- Allow read-only experimental recovery before an explicit saved HyperBoost restore, and match saved Custom levels during recovery. ([#791](https://github.com/OSSBlade/openblade-core/pull/791))

## [0.30.0] - 2026-10-16

### Changed

- Rewrite Chinese and Russian guides with clearer instructions and more natural wording. ([#781](https://github.com/OpenBladeProject/openblade-core/pull/781))

### Added

- Allow fixed fan speed on battery and fixed speed or manual curves on low-power adapters for Blade 16 RZ09-0528 and RZ09-0581. Experimental RZ09-0483 and RZ09-0482 also gain battery fixed speed; their low-power fan controls stay Automatic-only. ([#784](https://github.com/OSSBlade/openblade-core/pull/784))
- Add Simplified Chinese and Russian versions of the README and user guides, with language navigation and stable section links. ([#769](https://github.com/OpenBladeProject/openblade-core/pull/769))

### Fixed

- Apply Blade 16 (2025) manual fan changes directly. The 2025 and 2026 models can enter HyperBoost from manual fans on either admitted cooling-pad path without resetting to Automatic first. ([#785](https://github.com/OpenBladeProject/openblade-core/pull/785))

## [0.29.0-nightly.91] - 2026-10-15

### Added

- Use Synapse's adapter wattage classification on all seven admitted Blade models, including the five experimental ones. OpenBlade compares actual and recommended power before thermal changes, and limited supplies allow Balanced and Automatic fans. ([#779](https://github.com/OpenBladeProject/openblade-core/pull/779))

## [0.28.1-nightly.90] - 2026-10-15

### Added

- Expand experimental Blade controls with model-specific Custom levels, fixed and Maximum Fans, keyboard and logo lighting, and 50–100% charge limits. Blade 16 (2023) gains whole-keyboard Static, Ambient Awareness and Audio Meter; Blade 15 Base (2020) gains single-zone RGB. Diagnostics separate tested upstream features from extrapolations. ([#770](https://github.com/OpenBladeProject/openblade-core/pull/770))

### Fixed

- Recognize all Synapse-defined adapter wattages, including 200 W, on Blade 16 (2025 and 2026). Compare connected and recommended wattage, and label insufficient supplies as low-power adapters. ([#776](https://github.com/OpenBladeProject/openblade-core/pull/776))

## [0.27.5-nightly.89] - 2026-10-15

### Changed

- Point Synapse Blade Blocker guidance to its repository for all models, not only Blade 16 (2025 and 2026), and clarify its use with experimental models. ([#768](https://github.com/OpenBladeProject/openblade-core/pull/768))
- Open Synapse Observer from **Help add support** and explain how to collect and submit evidence for an unsupported laptop. The support form accepts Observer ZIPs and OpenBlade identity reports. ([#765](https://github.com/OpenBladeProject/openblade-core/pull/765))
- Simplify experimental-support wording in the README and guides, and keep model features and testing instructions in the linked guide. ([#764](https://github.com/OpenBladeProject/openblade-core/pull/764))
- Document all five experimental Blade models, automatic controls, model limits, recovery and tester reports across the README and guides, with laptop lists ordered newest first. ([#759](https://github.com/OpenBladeProject/openblade-core/pull/759))

### Added

- Add automatic experimental Balanced/Silent controls for the Blade 16 (2023), Blade 14 (2023 and 2025), Blade 15 Base (2020) and Blade 18 (2023), with 80% battery protection on the supported experimental models. ([#744](https://github.com/OpenBladeProject/openblade-core/pull/744))
- Add experimental Blade 18 (2023) charge limits from 50–100% in 1% steps. 100% turns protection off. ([#744](https://github.com/OpenBladeProject/openblade-core/pull/744))
- Add experimental Blade 18 (2023) Performance and Custom CPU/GPU controls, keyboard brightness, Off/Spectrum/random Breathing/Wave effects, and Off/Static/Breathing logo lighting. ([#744](https://github.com/OpenBladeProject/openblade-core/pull/744))
- Let users resume paused experimental controls after a fresh read-only check, then send a new request. ([#744](https://github.com/OpenBladeProject/openblade-core/pull/744))
- Include experimental command results, source details, pause state and cached telemetry in local diagnostic exports for pre-release tester reports. ([#744](https://github.com/OpenBladeProject/openblade-core/pull/744))

### Fixed

- Prevent RPM and lighting commands from overlapping on RZ09-0528. Share cancellation handling across model backends, tell apart requests that were never sent, and let controls recover after cleanup errors. ([#762](https://github.com/OpenBladeProject/openblade-core/pull/762))
- Keep firmware query replies in exported diagnostics after an unrecognized reading or failed model check, including adapter classification failures. Failure evidence now stays for the current service run without developer tools. ([#773](https://github.com/OSSBlade/openblade-core/pull/773))
- On RZ09-0581 with AC power, apply manual fan, performance and padless HyperBoost changes directly, without first resetting to Balanced or Automatic. Remove the obsolete Balanced instruction. Battery Protection can be turned off without resetting the current charge limit first. ([#756](https://github.com/OpenBladeProject/openblade-core/pull/756))
- Prevent animated keyboard lighting from overlapping fan and battery commands on RZ09-0581. Exported diagnostics now keep the command and native I/O error when an operation fails. ([#753](https://github.com/OpenBladeProject/openblade-core/pull/753))
- Keep the selected performance mode on experimental laptops when a saved profile contains unavailable manual-fan settings. ([#744](https://github.com/OpenBladeProject/openblade-core/pull/744))
- Keep independent experimental controls available after unrecognized readings, allow unrelated profile edits when a saved setting is unavailable, and accept new lighting requests after resuming paused controls. ([#744](https://github.com/OpenBladeProject/openblade-core/pull/744))
- Read temporary full charge correctly on supported Blade laptops when other settings that share it, such as local dimming, are on. Battery controls no longer stay unavailable with an unrecognized battery value. On the Blade 16 (2026), Synapse turns local dimming on when Windows HDR turns on. While local dimming, CPU boost or maximum fan speed is on, OpenBlade leaves temporary full charge unchanged and explains why. ([#755](https://github.com/OpenBladeProject/openblade-core/pull/755))
- Stop restarting or re-requesting a one-time full charge after the laptop or another app ends it, and stop reporting that as a change made outside OpenBlade. ([#755](https://github.com/OpenBladeProject/openblade-core/pull/755))

## [0.26.2-nightly.87] - 2026-10-14

### Added

- Show “This laptop isn’t supported yet” on unsupported laptops. **Help add support** saves a short identity report and opens a support request form to attach it to. ([#745](https://github.com/OSSBlade/openblade-core/pull/745))

### Fixed

- Recognize a supported laptop when Windows reports no BIOS version or two different ones. The BIOS version is checked only where a model requires it. ([#745](https://github.com/OSSBlade/openblade-core/pull/745))
- Show the actual failed identity check in exported diagnostics, instead of a product-name mismatch against another model. ([#745](https://github.com/OSSBlade/openblade-core/pull/745))
- Keep the other controls working when the laptop reports a battery, performance or fan value OpenBlade doesn’t recognize. Only the affected controls stay unavailable, and HyperBoost shows as unknown until a later reading confirms it. The MCU firmware version is still read, and the status and exported diagnostics show the unrecognized value, including for keyboard and logo lighting. ([#752](https://github.com/OpenBladeProject/openblade-core/pull/752))

### Changed

- Start OpenBlade as soon as you sign in. Setup now uses Task Scheduler instead of the Startup folder, which Windows delays, and removes the old startup shortcuts. ([#748](https://github.com/OpenBladeProject/openblade-core/pull/748))
- Reduce the installed payload by using a shared .NET 10 Desktop Runtime. Setup prompts to install it when needed and keeps Visual C++ bundled. ([#743](https://github.com/OpenBladeProject/openblade-core/pull/743))

## [0.25.2-nightly.84] - 2026-10-13

### Fixed

- Explain why laptop controls or manual fan modes are unavailable, and offer a current-state check when controls are blocked. ([#737](https://github.com/OSSBlade/openblade-core/pull/737))

## [0.25.1-nightly.83] - 2026-10-13

### Fixed

- Name the battery or thermal query that blocks device controls in exported diagnostics. ([#735](https://github.com/OSSBlade/openblade-core/pull/735))

## [0.25.0] - 2026-10-13

### Added

- Recognize supported Razer Blade products across SKU variants by their exact product name and HID identity. ([#732](https://github.com/OSSBlade/openblade-core/pull/732))

### Fixed

- Include the exact failed device check in exported diagnostics, with expected and observed identity values and HID failure details. ([#732](https://github.com/OSSBlade/openblade-core/pull/732))

## [0.24.5] - 2026-10-12

### Fixed

- Show GPU app cleanup for closable apps that hold a GPU context while idle. ([#726](https://github.com/OpenBladeProject/openblade-core/pull/726))
- Show “No profile selected” in the display color profile picker when Windows reports no selected profile. ([#725](https://github.com/OpenBladeProject/openblade-core/pull/725))

### Changed

- Give HyperBoost its own README feature bullet and remove the model-specific callout for standard power modes. ([#727](https://github.com/OpenBladeProject/openblade-core/pull/727))

- Refresh documentation for 0.24.3 model support, HyperBoost, native brightness and Synapse coexistence. ([#724](https://github.com/OpenBladeProject/openblade-core/pull/724))

## [0.24.3] - 2026-10-12

### Added

- Start HyperBoost directly from Silent, Performance or Custom on the Blade 16 (2026) RZ09-0581, with or without the cooling pad, and restore the starting mode and Custom CPU/GPU levels afterward. ([#717](https://github.com/OSSBlade/openblade-core/pull/717))

- Enable HyperBoost on the Blade 16 (2025) RZ09-0528 with the supported cooling pad and full-power adapter. ([#596](https://github.com/OpenBladeProject/openblade-core/pull/596))
- Enable NVIDIA GPU clock tuning on the Blade 16 (2025) RZ09-0528, with model and driver limits, and restore offsets with confirmation. ([#596](https://github.com/OpenBladeProject/openblade-core/pull/596))
- Enable HyperBoost without the cooling pad on the Blade 16 (2025) RZ09-0528 on the full-power adapter, keep GPU clock tuning available, and start HyperBoost directly from Silent, Balanced or Performance with or without the pad. ([#596](https://github.com/OpenBladeProject/openblade-core/pull/596))
- Start HyperBoost directly from Custom on the Blade 16 (2025) RZ09-0528, with or without the cooling pad, and return to the same Custom CPU/GPU levels afterward. ([#610](https://github.com/OpenBladeProject/openblade-core/pull/610))
- Choose Battery Saver or Battery Balanced on battery power on the Blade 16 (2025) RZ09-0528. ([#614](https://github.com/OpenBladeProject/openblade-core/pull/614))
- Assign M1 (Fn+Page Up) and M2 (Fn+Page Down) on the Blade 16 (2025) RZ09-0528, like M3–M5. ([#616](https://github.com/OpenBladeProject/openblade-core/pull/616))
- Show Windows' own brightness flyout on the Blade 16 (2025) RZ09-0528 when its Razer driver is installed, like the RZ09-0581. ([#618](https://github.com/OpenBladeProject/openblade-core/pull/618))

### Fixed

- Fix selecting a color profile on the built-in display when Windows has no active profile, and restore the previous profile settings if the change fails. ([#720](https://github.com/OSSBlade/openblade-core/pull/720))
- Hide the apps using the NVIDIA GPU card when a successful activity scan confirms the GPU is inactive. ([#718](https://github.com/OSSBlade/openblade-core/pull/718))
- Read published firmware versions with or without a v prefix, and keep each version tied to its updater row. ([#716](https://github.com/OpenBladeProject/openblade-core/pull/716))
- Translate the cooling-pad status messages shown when HyperBoost needs the pad. ([#717](https://github.com/OpenBladeProject/openblade-core/pull/717))

- Keep the tray and Session Agent running during complete shutdown until the service is confirmed stopped. ([#663](https://github.com/OpenBladeProject/openblade-core/pull/663))
- Clear a power mode warning after unplugging during sleep when a fresh read confirms the battery profile. ([#587](https://github.com/OpenBladeProject/openblade-core/pull/587))
- Show one-shot keyboard lighting as unconfirmed on the Blade 16 (2025) RZ09-0528 when firmware leaves Driver mode. It redraws automatically only with the lighting override setting on. ([#620](https://github.com/OpenBladeProject/openblade-core/pull/620))
- Record the reason in the installer's package log when a quiet install fails, not only an exit code. ([#612](https://github.com/OpenBladeProject/openblade-core/pull/612))
- Stop showing the HyperBoost unavailable warning while a performance mode change is in progress. ([#596](https://github.com/OpenBladeProject/openblade-core/pull/596))
- Stop the fan editor from creating a temporary fan override when the selection has not changed. ([#596](https://github.com/OpenBladeProject/openblade-core/pull/596))
- Keep the restored performance and fan settings after OpenBlade recovers HyperBoost from an unexpected service stop on full-power AC, and stop reporting them as external changes. ([#596](https://github.com/OpenBladeProject/openblade-core/pull/596))
- Recognize the supported cooling pad when its auxiliary input interfaces report a different revision. ([#596](https://github.com/OpenBladeProject/openblade-core/pull/596))
- Recognize Fn+P on the Blade 16 (2025) RZ09-0528, and clear held-key state across connection, session and power changes. ([#596](https://github.com/OpenBladeProject/openblade-core/pull/596))
- Restore GPU offsets when a driver call changes a value before returning an error, and report uncertainty if restoration can't be confirmed. ([#588](https://github.com/OpenBladeProject/openblade-core/pull/588))
- Hide the HyperBoost unavailable warning on battery and USB-C power, and show it again when full-power AC returns. ([#589](https://github.com/OSSBlade/openblade-core/pull/589))

## [0.22.0] - 2026-09-25

### Added

- Keep NVIDIA GPU tuning available while HyperBoost runs on the RZ09-0581, and keep confirmed offsets after HyperBoost exits or recovers. ([#579](https://github.com/OpenBladeProject/openblade-core/pull/579))

## [0.21.10] - 2026-09-24

### Fixed

- Allow HyperBoost when Synapse Blade Blocker evidence is present despite known Synapse service aliases, and while the RGS updater service or the Windows lighting helper is running. ([#573](https://github.com/OSSBlade/openblade-core/pull/573))

## [0.21.9] - 2026-09-23

### Fixed

- Keep apps launched by the local-development deployment client independent of the command runner, or report a relaunch failure when Windows prevents it. ([#567](https://github.com/OpenBladeProject/openblade-core/pull/567))
- Recognize validated MCU 1.7.0.0 on Razer Blade 16 RZ09-0581 in the tested-firmware notice. ([#566](https://github.com/OSSBlade/openblade-core/pull/566))
- Fix firmware checks failing on the first attempt when Razer's support page starts a new session. ([#564](https://github.com/OSSBlade/openblade-core/pull/564))

## [0.21.6] - 2026-09-20

### Fixed

- Keep the requested lighting effect visible when an apply completes before the final service status arrives. ([#549](https://github.com/OSSBlade/openblade-core/pull/549))
- Keep intentional black primary colors when switching Breathing or Starlight between Single and Dual color modes. ([#541](https://github.com/OSSBlade/openblade-core/pull/541))
- Keep stored Tidal colors when disabling random colors, show visible colors when leaving effects without an editable color, and keep intentional black between colored effects. ([#539](https://github.com/OSSBlade/openblade-core/pull/539))
- Refresh HyperBoost eligibility when cooling-pad detection changes, even when all other laptop status stays the same. ([#531](https://github.com/OSSBlade/openblade-core/pull/531))
- Keep connection, control and power state steady when diagnostic messages change, and clear transient state after confirmed recovery. ([#525](https://github.com/OSSBlade/openblade-core/pull/525))
- Try once to start the OpenBlade service when the tray launches and Windows confirms it is stopped. ([#519](https://github.com/OSSBlade/openblade-core/pull/519))

## [0.21.0] - 2026-09-19

### Added

- Allow Synapse to keep serving peripherals when its running AppEngine instances have a verified Synapse Blade Blocker patch. OpenBlade falls back to its normal checks after updates or invalid patch evidence. ([#506](https://github.com/OSSBlade/openblade-core/pull/506))

### Fixed

- Explain HyperBoost restrictions beside the performance controls, and allow Chroma lighting services. ([#503](https://github.com/OSSBlade/openblade-core/pull/503))

## [0.20.23] - 2026-09-15

### Fixed

- Turn off OpenBlade-controlled keyboard lighting when Windows suspends, and restore it on resume. The separate display-off preference is kept. ([#498](https://github.com/OSSBlade/openblade-core/pull/498))
- Stop OpenBlade's own control writes from being reported as external changes during resume checks. ([#498](https://github.com/OSSBlade/openblade-core/pull/498))
- Match the Laptop performance row to other compact dashboard settings when its guidance is hidden. ([#497](https://github.com/OSSBlade/openblade-core/pull/497))
- Fix incorrect transition or retained-state diagnostics after resume events. ([#490](https://github.com/OSSBlade/openblade-core/pull/490))
- Keep dormant legacy GPU offsets inactive outside plugged-in Custom intent, which fixes an incorrect tuning label. The offsets stay available for editing. ([#489](https://github.com/OSSBlade/openblade-core/pull/489))
- Stop showing notifications for unchanged state at startup or when the power source is briefly unknown. ([#488](https://github.com/OSSBlade/openblade-core/pull/488))

## [0.20.18] - 2026-09-13

### Fixed

- Keep monitoring an already-running installer when its process ID can't be saved, and wait for the final process list after relaunch without repeating the installation. ([#468](https://github.com/OSSBlade/openblade-core/pull/468))
- Avoid redundant capability and fan-editor notifications after service updates. ([#469](https://github.com/OSSBlade/openblade-core/pull/469))
- Recover from oversized preference files by saving a backup of the original, and keep dashboard data from newer versions. ([#469](https://github.com/OSSBlade/openblade-core/pull/469))
- Keep display failure diagnostics, with limited reporting of repeated failures. ([#469](https://github.com/OSSBlade/openblade-core/pull/469))
- Recognize BIOS 4.01 as an admitted firmware version for RZ09-0581. ([#475](https://github.com/OSSBlade/openblade-core/pull/475))

- Limit pipe-connection retries and lifecycle event backlogs, keep idle GPU cleanup working during CPU-only sampling, and keep saved lighting preferences consistent after renderer recovery. ([#467](https://github.com/OSSBlade/openblade-core/pull/467))
- Release audio resources immediately when Audio Meter setup fails. ([#466](https://github.com/OSSBlade/openblade-core/pull/466))
- Refresh the notification-area icon when the Windows theme changes, and use a darker green profile line and source badge on light taskbars. ([#454](https://github.com/OSSBlade/openblade-core/pull/454))

## [0.20.12-nightly.49] - 2026-09-10

### Changed

- Nightly releases stay drafts until their generated changelog is merged into `main` and the publication check passes. ([#446](https://github.com/OSSBlade/openblade-core/pull/446))

### Fixed

- Fix false external-change warnings after uncertain battery command responses and missing battery or custom-performance reads. Charge limits, protection toggles, and temporary full charge now wait for fresh readback to confirm the requested state. Power-source transitions that stop after an uncertain write now finish their remaining battery, fan, and lighting changes once the state is confirmed. ([#444](https://github.com/OSSBlade/openblade-core/pull/444))

## [0.20.11-nightly.47] - 2026-09-09

### Changed

- Report keyboard lighting as changed outside OpenBlade only when a different, non-Off effect is confirmed or Razer Synapse-type software is detected. A keyboard that reads Off with no such software is treated as a fault and retried once, and a newer lighting choice cancels the retry. ([#424](https://github.com/OSSBlade/openblade-core/pull/424))
- Separate profile import and fan-restoration policies from their callers, and base fan reconciliation on typed outcomes instead of diagnostic text. ([#436](https://github.com/OSSBlade/openblade-core/pull/436))

### Fixed

- Keep service status updates in order so a newer snapshot is never replaced by an older one. ([#436](https://github.com/OSSBlade/openblade-core/pull/436))
- Release stale tray waits, limit profile-file imports, and keep file parsing off the UI thread. ([#436](https://github.com/OSSBlade/openblade-core/pull/436))
- Retry temporary special-key preference read failures without another save, and synchronize ambient screen samples with GDI drawing. ([#436](https://github.com/OSSBlade/openblade-core/pull/436))
- Keep special-key watcher failures during startup, and back off repeated preference-read failures without repeated warnings. ([#436](https://github.com/OSSBlade/openblade-core/pull/436))
- Stop canceled display preparations from submitting changes. ([#436](https://github.com/OSSBlade/openblade-core/pull/436))
- Limit how long Windows Service Control commands can run during installation, repair, and removal. ([#436](https://github.com/OSSBlade/openblade-core/pull/436))
- Stop a keyboard left dark by a closed lid or Modern Standby from being reported as "Changed outside OpenBlade: Lighting". A power-source change with the lid shut no longer relights the keyboard, and lighting recovery waits until the display is back on. ([#423](https://github.com/OSSBlade/openblade-core/pull/423))

## [0.20.8-nightly.44] - 2026-09-06

### Changed

- Record HyperBoost without the Razer cooling pad as admitted by model, not by BIOS. BIOS 4.00 stays as capture provenance only. ([#414](https://github.com/OSSBlade/openblade-core/pull/414))
- Read-only Windows LampArray observation on the Razer Blade 16 RZ09-0528 no longer requires a specific BIOS, EC, and MCU combination. ([#414](https://github.com/OSSBlade/openblade-core/pull/414))

### Fixed

- Stop a failed device read from being reported as a keyboard lighting change made outside OpenBlade. After such a failure during a profile transition, effects the laptop can't read back are now reported as applied without readback. The same fix covers Fixed and manual-curve fan writes. ([#419](https://github.com/OSSBlade/openblade-core/pull/419))

## [0.20.7-nightly.41] - 2026-09-03

### Changed

- Reduce the CPU the tray uses reacting to service updates. The tray now announces a setting only when its value changes, so an unchanged update triggers 0 or 1 announcements instead of 11 or 12. ([#408](https://github.com/OSSBlade/openblade-core/pull/408))

### Fixed

- Stop the tray's memory from growing while it runs. After a few hours the hidden tray held a 512 MB array of debugger bookkeeping, which is now disabled. ([#406](https://github.com/OSSBlade/openblade-core/pull/406))

## [0.20.5-nightly.40] - 2026-09-02

### Added

- Add automatic restoration of performance and fan settings changed outside OpenBlade, on by default. Choose Restore them, or Notify me to report the change and leave it in place. Fan speed and curve changes can't be detected. After upgrading, a profile asks you to acknowledge a disclosure before background restoration starts. Older IPC clients stay read-only until updated. The thermal state row appears only when it has something to report. ([#402](https://github.com/OSSBlade/openblade-core/pull/402))

### Changed

- Compile the Tray developer smoke and localization-snapshot harnesses only into harness builds, not the shipped Tray. ([#401](https://github.com/OSSBlade/openblade-core/pull/401))
- Keep the service thermal sensors open between samples, and stop blocking a thread during the GPU activity check. Manual fan curves and Tray live readings no longer re-initialize NVML every second under a GPU load. ([#396](https://github.com/OSSBlade/openblade-core/pull/396))
- Reload Session Agent special-key bindings when the tray preferences file changes instead of polling every second. ([#395](https://github.com/OSSBlade/openblade-core/pull/395))

### Fixed

- Load a `manual-overrides.json` whose root is not an object as safe defaults with thermal writes disabled, instead of failing service startup. ([#394](https://github.com/OSSBlade/openblade-core/pull/394))
- Stop counting Windows System-process video-memory paging as NVIDIA GPU activity, which made GPU temperature flicker in Tray telemetry on an idle desktop. ([#388](https://github.com/OSSBlade/openblade-core/pull/388))
- Make translator-package archives byte-identical across fresh processes by normalizing adjacent-palette ties at rounded corners in localization snapshots. ([#385](https://github.com/OSSBlade/openblade-core/pull/385))
- Stabilize disconnected translator-package screenshots on slower renderers by requiring two consecutive canonical frames before archiving. ([#377](https://github.com/OSSBlade/openblade-core/pull/377))
- Wait for each Tray window to finish closing before rebuilding the next localized shell, so rapid language changes don't overlap. ([#381](https://github.com/OSSBlade/openblade-core/pull/381))
- Rework the Simplified Chinese Tray, runtime status, installer, and Setup text to use natural Windows-style wording. ([#364](https://github.com/OSSBlade/openblade-core/pull/364))
- Detect idle NVIDIA GPU context holders, including overlays reporting zero utilization, in the GPU-app cleanup action. Cleanup appears on both Home and Laptop, needs you to select each app, and confirms each force-close separately. NVIDIA Overlay and Windows Explorer stay out of the list. ([#355](https://github.com/OSSBlade/openblade-core/pull/355))
- Refresh Windows NVIDIA GPU activity counters before thermal and clock-offset decisions so processes started after service launch are not missed. Failed refreshes stay unknown, and fan control fails safe. ([#375](https://github.com/OSSBlade/openblade-core/pull/375))
- Remove English-style spaces around embedded Latin product and power-source names in Simplified Chinese profile-change notifications. ([#346](https://github.com/OSSBlade/openblade-core/pull/346))
- Register the Tray and Session Agent for Windows Application Restart so Windows can relaunch them after supported crash and update shutdowns. Intentional exits are respected. ([#382](https://github.com/OSSBlade/openblade-core/pull/382))

## [0.19.37-nightly.39] - 2026-09-01

### Added

- Add a local-development deployment broker that installs OpenBlade builds quietly without repeated UAC prompts. It must be enrolled and TPM-authorized first. ([#297](https://github.com/OSSBlade/openblade-core/pull/297))

### Fixed

- Remove the unused Dynamic Lighting sparse-package registration from the Session Agent. Keyboard lighting is still delegated to Windows, and older installations are still cleaned up and rolled back correctly. ([#360](https://github.com/OSSBlade/openblade-core/pull/360))
- Fix concurrent changes to the HyperBoost ownership journal. They now block before another hardware write and keep the newer recovery state. ([#348](https://github.com/OSSBlade/openblade-core/pull/348))
- Block older OpenBlade installers from replacing a newer installed version. Interactive setup leaves the installation and settings unchanged and points to the installed or a newer installer. ([#342](https://github.com/OSSBlade/openblade-core/pull/342))
- Keep incomplete, invalid, mismatched and future-version profile data in quarantine instead of replacing it with safe defaults. Thermal settings stay read-only while the rest of the app keeps working. ([#340](https://github.com/OSSBlade/openblade-core/pull/340))
- Fix read-only performance and fan queries failing before the HID session opens when no write commands are available. HyperBoost and base-mode capabilities are now read-only in that case. ([#334](https://github.com/OSSBlade/openblade-core/pull/334))
- Route explicit Strict fan reapply and shutdown handback through the shared thermal executor. Reapply gets at most one fresh retry after a failure that happened before the command was sent, and shutdown cleanup does not wait for it. ([#332](https://github.com/OSSBlade/openblade-core/pull/332))
- Route automatic HyperBoost destination restoration through the shared Strict thermal executor. A power or user change during restoration now cancels it, and only a failure before the command was sent gets one retry. ([#330](https://github.com/OSSBlade/openblade-core/pull/330))
- Fix unexpected-exit fan recovery so safety-floor, handback and deferred fan writes get at most one fresh retry. Resume, controller arrival, device loss, external changes or user changes can no longer revive stale handback authority. ([#328](https://github.com/OSSBlade/openblade-core/pull/328))
- Require confirmed readback before a successful fan write counts as OpenBlade controlling the fans. Fan commands without readback stay unresolved, and a confirmed Automatic result clears stale ownership. ([#326](https://github.com/OSSBlade/openblade-core/pull/326))
- Keep Strict fan-transition retries inside the shared thermal executor. A transient read failure before submission gets one fresh retry, and FirmwareSmoke now restores the original state immediately instead of starting a second transition. ([#324](https://github.com/OSSBlade/openblade-core/pull/324))
- Fix explicit HyperBoost restore recovery so it uses the shared Strict thermal executor's single retry. A restore journal that changes during restoration is kept instead of overwritten. ([#322](https://github.com/OSSBlade/openblade-core/pull/322))
- Unify HyperBoost entry, exit, compensation and restoration under one Strict thermal transition with at most one fresh retry. Periodic power classification no longer queues behind a running thermal transition, and queued Driver-mode and lighting maintenance rechecks low-power state first. ([#330](https://github.com/OSSBlade/openblade-core/pull/330))
- Tie automatic manual-fan curve maintenance to the exact session and device state that performed the successful OpenBlade fan write. Reconnect, resume, profile edits and generic manual readback no longer grant ownership. ([#320](https://github.com/OSSBlade/openblade-core/pull/320))
- Route full power-profile performance and fan changes through the Strict thermal executor. A failed step no longer replays an earlier write, and a possible thermal write stops later profile writes until the state is read back. ([#319](https://github.com/OSSBlade/openblade-core/pull/319))
- Route explicit fan transitions through the Strict thermal executor, sharing one retry across the Automatic and Balanced bridges, the fan write and performance restoration. Earlier confirmed steps are never replayed. ([#320](https://github.com/OSSBlade/openblade-core/pull/320))
- Verify bundle registration, installer ownership, service registration and installed files before reporting uninstall success. A skipped or incomplete removal now reports a failure. ([#318](https://github.com/OSSBlade/openblade-core/pull/318))
- Centralize scoped performance transitions under one Strict executor. It keeps the EC settling delay and allows one retry after a failure that happened before the command was sent. ([#313](https://github.com/OSSBlade/openblade-core/pull/313))
- Fix consecutive brokered upgrades leaving duplicate installer registrations or rolling back. The cached Stable-plus-LocalDev pair now passes the LocalDev-only bootstrapper gate. ([#312](https://github.com/OSSBlade/openblade-core/pull/312))
- Treat an exact-device identity rejection before a firmware command as not applied. Outcomes stay uncertain once a command may have been sent. ([#310](https://github.com/OSSBlade/openblade-core/pull/310))
- Block control writes when HyperBoost recovery finds an invalid ownership journal, and keep the journal unchanged for manual inspection. ([#309](https://github.com/OSSBlade/openblade-core/pull/309))
- Stop automatic manual-fan curve maintenance after a target write has an unknown outcome and while low-power preparation is active. Fan ownership returns only after a successful explicit fan action. ([#305](https://github.com/OSSBlade/openblade-core/pull/305))
- Fix thermal-only readings showing up as external battery-protection changes. Confirmed battery-protection and lighting state is now preserved across performance and fan readback. ([#303](https://github.com/OSSBlade/openblade-core/pull/303))

## [0.19.15-nightly.34] - 2026-08-27

### Fixed

- Release the quick-controls surface when it is dismissed, so the tray no longer holds a discrete-GPU context after a game exits. ([#294](https://github.com/OSSBlade/openblade-core/pull/294))
- Make disconnected translator screenshots identical across active and inactive window states, so repeated localization package exports stay byte-identical. ([#306](https://github.com/OSSBlade/openblade-core/pull/306))
- Make HyperBoost a temporary Laptop performance choice available from every admitted full-power profile and fan mode. Its guidance appears only while HyperBoost is selected, and every exit restores the prior settings. ([#282](https://github.com/OSSBlade/openblade-core/pull/282))
- Retry failed thermal or adjustable-fan reads once in a fresh session, and confirm indeterminate manual-fan restoration once with a read. Submitted writes are not retried. ([#282](https://github.com/OSSBlade/openblade-core/pull/282))
- Recover an indeterminate second-channel manual fan entry by reading both channels back and continuing with the target only. ([#282](https://github.com/OSSBlade/openblade-core/pull/282))
- Let the lifecycle validator check the HyperBoost journal through read-only service diagnostics, refuse mismatched tool builds before writes, and require the exact prior state after a failed arm. ([#282](https://github.com/OSSBlade/openblade-core/pull/282))
- Defer a HyperBoost restore at a power boundary only after a failure before the command was sent, then recheck safety once and publish the result immediately. ([#282](https://github.com/OSSBlade/openblade-core/pull/282))
- Fix the playback-mute key light staying red on an unmuted F1 after the service-status subscription reconnects. ([#292](https://github.com/OSSBlade/openblade-core/pull/292))
- Reject translator imports with unadmitted locales, malformed UTF-8, hidden or bidirectional format characters, altered protected identifiers and stale installer mnemonic groups, with clear blank-cell feedback. Every package now needs context screenshots, and CI restores both disconnected renderers. ([#281](https://github.com/OSSBlade/openblade-core/pull/281))
- Fix a missed function-row keyboard hook running both the media action and the underlying F-key while the Session Agent restores the hook. ([#290](https://github.com/OSSBlade/openblade-core/pull/290))
- Keep the Session Agent and media keys working when a client closes a named-pipe connection before Windows completes the accept. ([#284](https://github.com/OSSBlade/openblade-core/pull/284))
- Update every M1-M5 shortcut action, row label, validation message and accessibility name immediately when the app language changes. ([#278](https://github.com/OSSBlade/openblade-core/pull/278))
- Move VBIOS inventory into the Firmware section and show `None` only when Razer's page lists no VBIOS for the exact GPU. Failed website, parser or GPU readbacks stay unavailable, and repeated display-head aliases of one adapter collapse to a single installed VBIOS. Zero or multiple NVIDIA adapters are still not compared. ([#279](https://github.com/OSSBlade/openblade-core/pull/279))
- Place the Profiles page Import and Export actions beside the title when there is room, and wrap them cleanly in narrow or long-localized layouts. ([#280](https://github.com/OSSBlade/openblade-core/pull/280))
- Keep the App theme and App language pickers compact on desktop, and let them grow for longer localized choices. ([#277](https://github.com/OSSBlade/openblade-core/pull/277))
- Wait for every Session Agent loop to finish during coordinated shutdown, so microphone mute indication cannot overlap teardown or lose a late failure. ([#268](https://github.com/OSSBlade/openblade-core/pull/268))
- Fix the fan-mode selector briefly reverting while an older in-flight status arrives after a manual fan selection. ([#267](https://github.com/OSSBlade/openblade-core/pull/267))
- Refresh the fan-mode selector immediately when HyperBoost activates or exits, so its enabled state stays correct. ([#247](https://github.com/OSSBlade/openblade-core/pull/247))
- Localize the HyperBoost performance choice and cooling-pad guidance in Simplified Chinese and Russian instead of showing English. ([#282](https://github.com/OSSBlade/openblade-core/pull/282))
- Record a confirmed HyperBoost activation in the ownership journal before lifecycle recovery is armed. ([#245](https://github.com/OSSBlade/openblade-core/pull/245))
- Fix coordinated Session Agent cancellation turning into a named-pipe error that stops function-row and other per-user features. ([#308](https://github.com/OSSBlade/openblade-core/pull/308))
- Fix the FirmwareSmoke lifecycle bootstrap so it can exercise padless HyperBoost while the low-power and unexpected-exit evidence gates are still closed. ([#308](https://github.com/OSSBlade/openblade-core/pull/308))
- Rework the Simplified Chinese and Russian wording to use natural terminology across the tray, installer and setup. ([#308](https://github.com/OSSBlade/openblade-core/pull/308))
- Fix localized profile-draft captions overlapping adjacent columns at narrow window widths. ([#308](https://github.com/OSSBlade/openblade-core/pull/308))
- Keep keyboard-lighting effect choices in the same order in every supported display language. ([#308](https://github.com/OSSBlade/openblade-core/pull/308))
- Fix live language changes mixing strings from the previous language into the rebuilt tray when background work held an older UI culture. ([#308](https://github.com/OSSBlade/openblade-core/pull/308))
- Fix nonzero HID firmware metadata blocking device admission on RZ09-0581, and correct the platform warning shown while controls are unavailable. ([#308](https://github.com/OSSBlade/openblade-core/pull/308))
- Fix the laptop-performance dropdown going blank or failing to expand into horizontal choices after Dashboard edits when switching between AC and battery. ([#308](https://github.com/OSSBlade/openblade-core/pull/308))
- Fix malformed current-schema profile files, which now go through safe recovery and import errors. Stray legacy Windows power modes are cleared when migrating schema 1 profiles. ([#308](https://github.com/OSSBlade/openblade-core/pull/308))
- Remove the redundant **Double-click** hint from the tray context menu. Double-click still opens the main window. ([#308](https://github.com/OSSBlade/openblade-core/pull/308))
- Remove redundant troubleshooting status dots, clarify the service-start action and remove its idle leading gap. ([#308](https://github.com/OSSBlade/openblade-core/pull/308))
- Decode the RZ09-0528 `0011` no-adapter response. Windows Offline still decides battery power, and other Blade models are unaffected. ([#308](https://github.com/OSSBlade/openblade-core/pull/308))
- Retry transient MCU firmware-version read failures on later observations, and stop once a version is confirmed. ([#308](https://github.com/OSSBlade/openblade-core/pull/308))
- Show unclassified external power consistently in control access, active profile, tray hover and troubleshooting, instead of showing a finished classification attempt as still checking. ([#308](https://github.com/OSSBlade/openblade-core/pull/308))
- Keep the conditional profile Save and **Use profile settings** actions beside Customize when they fit. Otherwise the action group moves below the title, and long localized actions stack instead of overlapping or clipping. ([#308](https://github.com/OSSBlade/openblade-core/pull/308))
- Keep page customization buttons aligned with their titles, and let longer localized and profile actions use a separate row. ([#308](https://github.com/OSSBlade/openblade-core/pull/308))
- Fix the **Random colors** switch track alignment, and align compact switches consistently across the main window and quick controls. ([#308](https://github.com/OSSBlade/openblade-core/pull/308))
- Expand the graphical installer's progress details pane to use the available window height instead of a few log lines. ([#308](https://github.com/OSSBlade/openblade-core/pull/308))
- Offer **Repair** and **Uninstall** when the interactive installer finds one cached OpenBlade bundle of the same version. Repair overwrites the installed files. Uninstall keeps saved profiles; to remove profiles too, use the Windows Installed apps entry. ([#308](https://github.com/OSSBlade/openblade-core/pull/308))
- Advance the service IPC contract to version 46, so peers without the expanded firmware inventory are no longer accepted. ([#308](https://github.com/OSSBlade/openblade-core/pull/308))
- Reject imported lighting sequences for a different keyboard layout before touching the lighting device. ([#308](https://github.com/OSSBlade/openblade-core/pull/308))
- Keep the Lab features row aligned with neighboring Settings rows when it shows its information button. ([#308](https://github.com/OSSBlade/openblade-core/pull/308))
- Validate imported lighting timelines without rebuilding their decoded frames on every status check. ([#308](https://github.com/OSSBlade/openblade-core/pull/308))
- Label imported animations consistently in profile details and summaries instead of showing `Sequence` and `#000000`. ([#308](https://github.com/OSSBlade/openblade-core/pull/308))
- Keep configured Windows power modes when migrating schema 12 profiles to the keyboard-animation profile schema. ([#308](https://github.com/OSSBlade/openblade-core/pull/308))
- Fix a finished non-looping RZ09-0528 lighting sequence faulting or giving up the renderer. It now stays on its final frame. ([#308](https://github.com/OSSBlade/openblade-core/pull/308))
- Fix brief firmware lighting-off readings during AC and battery transitions being mistaken for external changes. A zero reading is now confirmed first, and a stable external lighting change is kept. If confirmation is unavailable, only lighting is skipped and the rest of the profile still applies. ([#308](https://github.com/OSSBlade/openblade-core/pull/308))

### Added

- Add an Excel-compatible translator package workflow for all four localization surfaces, with sanitized context screenshots, source and target catalog hashes, shortcut and mnemonic validation, and a plain-language contributor guide. ([#281](https://github.com/OSSBlade/openblade-core/pull/281))
- Add padless HyperBoost on RZ09-0581, so it works without the cooling pad. ([#245](https://github.com/OSSBlade/openblade-core/pull/245))
- Show padless HyperBoost as a temporary Laptop performance choice on the main dashboard and in Quick Controls. It is offered only at full power with no cooling pad, when the service reports reviewed write access. Saved profiles are unchanged. ([#282](https://github.com/OSSBlade/openblade-core/pull/282))
- Add complete Simplified Chinese and Russian translations for the tray, graphical installer, and native setup and uninstall. Settings lets the tray follow the Windows UI language or use either translation, with English as the fallback. ([#234](https://github.com/OSSBlade/openblade-core/pull/234))
- Add GPU-specific VBIOS update comparison for supported Blade models. OpenBlade compares only against the matching official Razer VBIOS release and skips the comparison when either side can't be identified. ([#225](https://github.com/OSSBlade/openblade-core/pull/225))
- Add RZ09-0581 Thunderbolt 5 and touchpad firmware comparison. Updates are reported only from trustworthy installed versions, and other models are not compared. ([#241](https://github.com/OSSBlade/openblade-core/pull/241))
- Add read-only diagnostics on RZ09-0581 with BIOS 3.01 for the SKU hardware-configuration byte, the Mini-LED panel-resolution byte and the thermal fan ID list. Raw values appear in diagnostics and exports. ([#224](https://github.com/OSSBlade/openblade-core/pull/224))
- Add an opt-in **60 Hz lighting** Lab mode for the RZ09-0581 keyboard. It applies to built-in, Ambient Awareness and Audio Meter animation, and persists after the tray exits. 30 Hz stays the default, and RZ09-0528 stays at 30 Hz. ([#206](https://github.com/OSSBlade/openblade-core/pull/206))
- Add en-US resource catalogs and qps-ploc validation for the tray and graphical installer, with installer fallback and startup checks. ([#207](https://github.com/OSSBlade/openblade-core/pull/207))
- Add an importable example of a cyan sweep that moves from left to right with a dim blue wake. ([#181](https://github.com/OSSBlade/openblade-core/pull/181))
- Add a versioned `.oblight` keyboard lighting preset format and a Lighting-page importer, available when Lab features are enabled in Settings. It converts Razer `.chroma` version 1 files: uniform Keyboard and KeyboardExtended files, and classic Keyboard per-key animations on RZ09-0581 and RZ09-0528. Imported animations persist with the power profile and keep running when the tray exits. Colored source cells with no matching key are reported, and per-key KeyboardExtended files are rejected. Imported effects are limited to what your laptop already supports. ([#181](https://github.com/OSSBlade/openblade-core/pull/181))

### Changed

- Replace the About-page affiliation disclaimer with the translated community tagline "For gamers. By the community." ([#276](https://github.com/OSSBlade/openblade-core/pull/276))
- Apply tray language changes immediately without restarting OpenBlade, and align the language picker with the other Appearance controls. ([#252](https://github.com/OSSBlade/openblade-core/pull/252))
- Admit supported laptops by exact model and SKU by default. Validated BIOS versions are provenance only, unless a firmware-specific capability gate requires one. On RZ09-0581 this makes the 45 W, 60 W, 65 W, 80 W, 90 W, OEM 280 W and SlimQ 330 W adapter classes available across firmware versions. ([#227](https://github.com/OSSBlade/openblade-core/pull/227))
- Advance service IPC from 47 to 48 for the model-scoped firmware compatibility state. ([#227](https://github.com/OSSBlade/openblade-core/pull/227))
- Omit firmware comparison rows whose installed version is unavailable, along with the disclaimer. ([#241](https://github.com/OSSBlade/openblade-core/pull/241))
- Hide the firmware versions table when none of its installed values are readable. Update controls and the official support link stay available. ([#249](https://github.com/OSSBlade/openblade-core/pull/249))
- Omit BIOS, EC, MCU and VBIOS rows from Device Information when the same installed version already shows in the firmware comparison table. ([#241](https://github.com/OSSBlade/openblade-core/pull/241))
- Tie each optional Windows power mode to a laptop performance preset instead of an AC, USB-C or battery profile. Dashboard changes, saved-profile transitions and Fn+P update Windows only after the laptop preset is confirmed. Profile schema 15, export schema 10 and service IPC 47 migrate unambiguous associations. AC and USB-C often shared one preset with different Windows-mode choices, so you may need to set those associations again after upgrading. ([#237](https://github.com/OSSBlade/openblade-core/pull/237))
- Advance the firmware-smoke snapshot schema from 7 to 8 for Thunderbolt and touchpad inventory metadata. ([#225](https://github.com/OSSBlade/openblade-core/pull/225))
- Expand the RZ09-0581 BIOS 4.00 USB-C profile to 45 W, 60 W, 65 W, 80 W, 90 W and 100 W adapter classes. After a brief HID failure, controls are enabled only once two replies agree. ([#215](https://github.com/OSSBlade/openblade-core/pull/215))
- Move the Lab keyboard preset importer into the Keyboard section, and replace the long Lab features description with an on-demand list of current features. ([#204](https://github.com/OSSBlade/openblade-core/pull/204))
- Advance the profile/configuration schema from 13 to 14 for the saved lighting refresh-rate preference. Migrated configurations default to 30 Hz. ([#206](https://github.com/OSSBlade/openblade-core/pull/206))

## [0.8.4-nightly.20] - 2026-08-15

### Added

- Add Windows-native display-brightness handling and flyout for the function row on the RZ09-0581. The WMI brightness path and OpenBlade flyout remain available when it is unavailable. About shows which brightness path is in use. ([#163](https://github.com/OSSBlade/openblade-core/pull/163))
- Add an optional Windows power mode (Best power efficiency, Balanced or Best performance) to each AC, USB-C and battery profile. It applies when the active power profile changes. ([#170](https://github.com/OSSBlade/openblade-core/pull/170))
- Add a **Close GPU apps** action to Power mode. It lists your apps that are actively using the NVIDIA GPU, asks them to close, and offers a force-close for any that stay open. It is hidden when no apps are using the GPU. ([#166](https://github.com/OSSBlade/openblade-core/pull/166))
- Add an administrator-approved Troubleshooting action that stops the known Synapse and Chroma controller services and reports their current state. ([#167](https://github.com/OSSBlade/openblade-core/pull/167))

### Changed

- Replace dropdown-only settings with horizontal choices in performance, fan, function-row, lighting, display and app settings. A compact dropdown is used when the window or row is too narrow. ([#182](https://github.com/OSSBlade/openblade-core/pull/182))
- Replace the tray-dashboard customization dialog with a Settings page that shows active controls and adjustments, keeps inactive choices in **Add** menus, and limits adjustments to two. The pinned tray popup updates live as you reorder, add, remove or reset items. ([#95](https://github.com/OSSBlade/openblade-core/pull/95))
- Add battery protection, function-row mode, gaming mode with its key-blocking options, and an optional GPU-tuning toggle to the tray-dashboard quick controls. ([#95](https://github.com/OSSBlade/openblade-core/pull/95))
- Rename the tray-dashboard lighting adjustment to **Keyboard brightness**. ([#95](https://github.com/OSSBlade/openblade-core/pull/95))
- Match the tray dashboard to the Windows Quick Settings size (360 by 388 DIP), add power-source and fan-speed status items, and standardize its primary icon size. ([#95](https://github.com/OSSBlade/openblade-core/pull/95))

### Fixed

- Reduce extra vertical space in single-line Dashboard rows. Descriptions, editors and wrapped titles keep their spacing. ([#193](https://github.com/OSSBlade/openblade-core/pull/193))
- Fix HDR toggles made during a background display refresh being dropped. ([#191](https://github.com/OSSBlade/openblade-core/pull/191))
- Fix the Fixed speed and Manual fan curve cards looking highlighted while idle or being edited. ([#189](https://github.com/OSSBlade/openblade-core/pull/189))
- Open the main window and tray dashboard before the first display query finishes, and avoid redundant display refreshes when the Session Agent responds slowly. ([#179](https://github.com/OSSBlade/openblade-core/pull/179))
- Limit the tray-dashboard status row to three readings so icons and labels no longer clip. ([#95](https://github.com/OSSBlade/openblade-core/pull/95))
- Reduce tray-dashboard background work by sharing one telemetry loop, reading only the sensors selected for the popup, and pausing updates while the popup is hidden. Popup dismissal now completes even if animation stops early. ([#95](https://github.com/OSSBlade/openblade-core/pull/95))
- Add a startup check for the main window and every tray dashboard control, keep older builds from overwriting newer dashboard preferences, and stop GPU slider changes from applying after GPU tuning becomes unavailable or read-only. ([#95](https://github.com/OSSBlade/openblade-core/pull/95))
- Smooth tray-dashboard animation timing and limit tray-click reopen suppression to dismissals caused by the Windows taskbar. ([#95](https://github.com/OSSBlade/openblade-core/pull/95))
- Fix a tray crash at startup when loading dashboard status icon columns. ([#95](https://github.com/OSSBlade/openblade-core/pull/95))
- Use the Windows keyboard-brightness icon for the keyboard-brightness adjustment. ([#95](https://github.com/OSSBlade/openblade-core/pull/95))
- Keep selected GPU clock adjustments visible, but disabled, in the tray dashboard when GPU tuning is temporarily unavailable. ([#95](https://github.com/OSSBlade/openblade-core/pull/95))
- Keep the pinned tray-dashboard preview above a maximized OpenBlade window during Settings customization. ([#95](https://github.com/OSSBlade/openblade-core/pull/95))
- Keep profile pending-change dots next to their value text. ([#159](https://github.com/OSSBlade/openblade-core/pull/159))
- Fix the playback-mute key staying red after a rejected unmute. ([#157](https://github.com/OSSBlade/openblade-core/pull/157))
- Show the confirmed firmware value instead of `Not set` in laptop-behavior profile cells. Unconfigured profiles are left unchanged. ([#155](https://github.com/OSSBlade/openblade-core/pull/155))
- Show the global charge limit in the AC, USB-C and Battery profile columns so none appear empty. ([#155](https://github.com/OSSBlade/openblade-core/pull/155))
- Disable the fan-mode dropdown when Automatic is the only option, including on battery and USB-C power. ([#155](https://github.com/OSSBlade/openblade-core/pull/155))

## [0.5.1-nightly.16] - 2026-08-13

### Added

- Add a compact quick-controls dashboard that opens with one click on the tray icon. Customize its tiles, sliders, order, visibility and status row from Settings. Open the main window with a tray-icon double-click or the right-click **Open** command. ([#95](https://github.com/OSSBlade/openblade-core/pull/95))
- Recognize the SlimQ 330 W adapter on the Razer Blade 16 RZ09-0581 with BIOS 3.01 or 4.00. It now selects the normal AC profile, including its performance mode, fan behavior and GPU clock offsets. ([#138](https://github.com/OSSBlade/openblade-core/pull/138))
- Add per-power-profile battery protection, with pending-value indicators for AC, USB-C and battery. The former global setting migrates automatically. ([#118](https://github.com/OSSBlade/openblade-core/pull/118))
- Show the global charge limit in Profiles pending-change indicators. ([#118](https://github.com/OSSBlade/openblade-core/pull/118))
- Show a dot beside each saved Profiles value that has a pending change. Its tooltip gives the new value. ([#110](https://github.com/OSSBlade/openblade-core/pull/110))

### Changed

- Refine the dashboard into a Windows Quick Settings-style panel with a three-column tile grid. Quick actions cycle through options, chevrons open choice screens, and up to two charge-limit, lighting-brightness or GPU-clock sliders sit in a fixed adjustment section. Performance and fan controls get their own icons, and old wide-tile preferences migrate to the grid. ([#95](https://github.com/OSSBlade/openblade-core/pull/95))
- Reduce memory use in status updates. ([#147](https://github.com/OSSBlade/openblade-core/pull/147))
- Keep battery protection and charge-limit changes temporary until saved to a profile. Battery protection also follows the destination power profile when the power source changes. ([#118](https://github.com/OSSBlade/openblade-core/pull/118))
- Move the product version from the stale 0.1.x baseline to 0.2.0. Each future product-changing pull request must apply its own version bump, and CI rejects missing, stale or incorrect bumps. ([#114](https://github.com/OSSBlade/openblade-core/pull/114))

### Fixed

- Fix the quick-controls popup's blank black opening frame, raw lighting option text, missing fan icon, clipped controls and truncated status labels. The popup also opens and closes with matching 200 ms motion, dismisses after an outside click, refreshes telemetry while open, and fits its tiles at every display scale. ([#95](https://github.com/OSSBlade/openblade-core/pull/95))
- Restore the last confirmed charge limit when a tray edit cannot be applied, and keep any newer edit made meanwhile. ([#147](https://github.com/OSSBlade/openblade-core/pull/147))
- Keep special-key bindings when the preference file is briefly locked, and retry once it is available. ([#147](https://github.com/OSSBlade/openblade-core/pull/147))
- Reconnect status and lighting streams after a temporary capacity rejection, and let Ambient Awareness and Audio Meter share one frame connection. ([#147](https://github.com/OSSBlade/openblade-core/pull/147))
- Fix Core Audio shutdown waits and empty input-queue wakeups piling up. ([#147](https://github.com/OSSBlade/openblade-core/pull/147))
- Sync the microphone-mute key indicator with Windows at Session Agent startup and after mute or default capture-device changes. Playback-mute indicator updates pause while hardware writes are blocked and resume after fresh service status. ([#134](https://github.com/OSSBlade/openblade-core/pull/134))
- Show hardware writes with an unknown result as unconfirmed instead of as external changes. Retries wait until a read-only check confirms the result or finds a real mismatch. ([#128](https://github.com/OSSBlade/openblade-core/pull/128))
- Stop OpenBlade's keyboard renderer from fighting Razer Synapse when **Override external lighting changes** is off. A fresh manual lighting action stays active, and the saved toggle follows the signed-in user. ([#126](https://github.com/OSSBlade/openblade-core/pull/126))
- Prompt for administrator approval when **Shut down completely** is chosen from the normal tray, and add a troubleshooting action that starts the stopped service again. ([#122](https://github.com/OSSBlade/openblade-core/pull/122))
- Stop the first nightly after a product-version change from repeating entries already published under the previous version. ([#117](https://github.com/OSSBlade/openblade-core/pull/117))
- Stop clock-offset status reads from waking an idle NVIDIA GPU once GPU tuning is applied, disabled or left on another power profile. Managed offsets are rechecked after resume. When tuning is unmanaged, driver-held offsets may stay applied without being reported until tuning is managed again or the driver reloads. A live reading outside the admitted range now shows as unavailable instead of an older cached value. ([#113](https://github.com/OSSBlade/openblade-core/pull/113))
- Show the effective **Off** defaults for Gaming mode's Windows-key and Alt+Tab blocks instead of **Not set**. ([#106](https://github.com/OSSBlade/openblade-core/pull/106))
- Fix `IPC request body is invalid` errors and rejected profile saves after editing GPU clock tuning from a legacy profile. ([#108](https://github.com/OSSBlade/openblade-core/pull/108))
- Make dashboard Power mode subcards neutral at rest, with the standard hover highlight for GPU tuning and Custom CPU/GPU rows. ([#108](https://github.com/OSSBlade/openblade-core/pull/108))

## [0.1.0-nightly.11] - 2026-08-08

### Changed

- Simplify the official firmware version card with a clearer **Latest** column and a consistent action row for the model's Razer support page. ([#102](https://github.com/OSSBlade/openblade-core/pull/102))

### Fixed

- Fold GPU clock offsets under their tuning toggle, remove the persistent nested-card highlight, and place dashboard slider values beside their sliders. ([#57](https://github.com/OSSBlade/openblade-core/pull/57))
- Restore the function-row keyboard-brightness controls on the Razer Blade 16 RZ09-0581 and include them in diagnostic archives. ([#97](https://github.com/OSSBlade/openblade-core/pull/97))
- Keep full-power AC fan controls available in every supported performance mode, including fixed speeds, maximum fans and manual fan curves. ([#90](https://github.com/OSSBlade/openblade-core/pull/90))
- Tell intentional service stops from unexpected exits. After an unexpected restart, OpenBlade checks the laptop's state before writing anything, restores only the fan and lighting settings it owns, and starts a manual fan curve at a safe target (maximum speed if CPU temperature is unavailable). On multi-user installs it waits for the active user's saved settings. A signed-out restart on full AC or battery holds fans at maximum if firmware was left in manual, then returns them to Automatic on USB-C. Markers from an earlier Windows boot no longer block startup profiles. ([#79](https://github.com/OSSBlade/openblade-core/pull/79))
- Stop install and repair before Apply when another OpenBlade bundle has the same four-part version, and reject machines that already have several related bundle registrations. Interactive setup reports the conflict and still offers Uninstall. Exact development builds must use ordered Nightly revisions. ([#103](https://github.com/OSSBlade/openblade-core/pull/103))

## [0.1.0-nightly.10] - 2026-08-07

### Added

- Add per-user notification controls for performance and power-profile changes, app and Razer firmware updates, controller advisories and operation failures. Repeated toasts are combined, update versions notify once, and opening a notification goes to the relevant OpenBlade status. ([#78](https://github.com/OSSBlade/openblade-core/pull/78))

### Fixed

- Name the key action in function-row brightness failures, and add the function-key and keyboard Driver-mode state to diagnostic archives. ([#92](https://github.com/OSSBlade/openblade-core/pull/92))
- Stop newly applied Power mode and Custom CPU/GPU selections from briefly reverting after the progress spinner finishes. ([#88](https://github.com/OSSBlade/openblade-core/pull/88))
- Match MCU versions in the README hardware table to Razer's updater label format. ([#85](https://github.com/OSSBlade/openblade-core/pull/85))
- Close the dashboard warning flyout when its status clears, instead of leaving a mostly empty popup. ([#81](https://github.com/OSSBlade/openblade-core/pull/81))
- Offer **Use profile settings** when another app changes the keyboard effect to a value firmware can't read back, so the saved profile can be restored. ([#83](https://github.com/OSSBlade/openblade-core/pull/83))

## [0.1.0-nightly.9] - 2026-08-06

### Added

- Add Razer Blade 16 RZ09-0581 BIOS 4.00 to the tested firmware set alongside BIOS 3.01. Other BIOS versions need the untested-firmware override. ([#71](https://github.com/OSSBlade/openblade-core/pull/71))
- Add a graphical installer that follows the Windows app theme. It shows a compact progress view with expandable details, asks before replacing a newer version, and offers **Launch OpenBlade** after install or repair. It uses the **OpenBlade Setup** name and artwork throughout, and works for quiet and embedded installs. ([#69](https://github.com/OSSBlade/openblade-core/pull/69))

### Fixed

- Fix the Tray closing at startup when loading the GPU tuning card's description style. ([#57](https://github.com/OSSBlade/openblade-core/pull/57))

## [0.1.0-nightly.8] - 2026-08-06

### Changed

- Generalize the developer firmware-smoke workflow for BIOS, EC, MCU and VBIOS updates. Pre-update rehearsals declare the components under test, operator confirmation uses `<MODEL>-FIRMWARE-SMOKE`, and reports use `firmware.inventory.available` instead of `firmware.mcu.available`. ([#68](https://github.com/OSSBlade/openblade-core/pull/68))
- Simplify the tray right-click menu to **Open** and **Exit**. Exit asks whether to keep keyboard lighting, hotkeys, profiles and automation running in the background or shut OpenBlade down completely. ([#36](https://github.com/OSSBlade/openblade-core/pull/36))
- Give the OpenBlade installer its own adaptive file and taskbar icon with a Blade-green download badge. ([#69](https://github.com/OSSBlade/openblade-core/pull/69))

### Fixed

- Show the installed MCU firmware version the same way in Device information and the official firmware card. ([#64](https://github.com/OSSBlade/openblade-core/pull/64))
- Make **Start at sign-in** reflect the all-users Tray launcher and honor a per-user opt-out. The Session Agent still starts at sign-in. ([#67](https://github.com/OSSBlade/openblade-core/pull/67))
- Show **Incompatible firmware** for a recognized laptop with incompatible firmware, instead of also showing **Laptop not found**. Turning off the override now also stops any reactive or ambient lighting renderer right away. ([#55](https://github.com/OSSBlade/openblade-core/pull/55))
- Tighten the official firmware card into aligned header, interval, version and footer sections, and show MCU versions in the format Razer's updater uses. ([#59](https://github.com/OSSBlade/openblade-core/pull/59))
- Fix Custom performance controls not appearing until a mode switch when the tray starts in Custom mode. ([#31](https://github.com/OSSBlade/openblade-core/pull/31))
- Record the hardware-cleanup result on every graceful service stop so skipped cleanups can be diagnosed. ([#39](https://github.com/OSSBlade/openblade-core/pull/39))
- Return fans to firmware Automatic on a graceful service stop when the service set a fixed, maximum or curve fan mode. The saved fan setting is kept for the next start, and fan state set by Synapse is left alone. ([#16](https://github.com/OSSBlade/openblade-core/pull/16))
- Let a returning console session regain control through safe status polling. ([#14](https://github.com/OSSBlade/openblade-core/pull/14))
- Keep the service listener running when a local client connects but stalls or sends a malformed request. ([#14](https://github.com/OSSBlade/openblade-core/pull/14))
- Keep inactive and Remote Desktop sessions from running write-capable hardware observation. ([#14](https://github.com/OSSBlade/openblade-core/pull/14))
- Repair the all-users Tray startup shortcut on every install and repair, so upgrades don't leave newly signed-in users without OpenBlade. ([#14](https://github.com/OSSBlade/openblade-core/pull/14))
- Reapply the newly active user's keyboard-lighting profile during Fast User Switching instead of treating the previous user's lighting as an external change. ([#14](https://github.com/OSSBlade/openblade-core/pull/14))
- Start the visible tray for every signed-in user from the all-users Startup folder, and migrate the installing user's old startup link. ([#14](https://github.com/OSSBlade/openblade-core/pull/14))
- Fix multi-user builds failing the installer health check. ([#14](https://github.com/OSSBlade/openblade-core/pull/14))
- Keep background-user trays connected during Fast User Switching, with user-specific status hidden, and restore control without a tray restart. Remote Desktop sessions are read-only. ([#14](https://github.com/OSSBlade/openblade-core/pull/14))
- Keep the old per-user Session Agent startup link until its replacement is created, and move first-use Dynamic Lighting identity registration off the Session Agent's startup path. ([#14](https://github.com/OSSBlade/openblade-core/pull/14))
- Keep applied dashboard Power mode and Custom CPU/GPU selections stable while status catches up, restore the previous selection after real failures, and disable fixed and manual fan choices until Balanced Automatic is confirmed. ([#34](https://github.com/OSSBlade/openblade-core/pull/34))

### Added

- Add a separate GPU tuning switch and clock-offset controls for plugged-in profiles. Offsets work with Balanced and other power modes, and existing Custom-mode settings migrate automatically. Razer Blade 16 RZ09-0581 uses the Synapse-visible policy ranges on any BIOS version. ([#57](https://github.com/OSSBlade/openblade-core/pull/57))
- Add changelog snapshots to nightly prereleases. After each publication, the workflow records any unrecorded nightly boundaries in a draft documentation pull request. Changes merged after the build stay under `Unreleased`, and a malformed changelog no longer blocks publishing the installer. ([#48](https://github.com/OSSBlade/openblade-core/pull/48))
- Add optional Audio Meter smoothing for quieter moments and brief pauses. Louder responses stay immediate and existing profiles keep the current behavior. Service IPC is now version 35, stored profiles use schema 9, and profile exports use schema 5. ([#53](https://github.com/OSSBlade/openblade-core/pull/53))
- Add a per-user firmware compatibility override to the warning popup and Settings. It allows an untested firmware baseline while laptop and USB identity checks stay on. Service IPC is now version 36. ([#55](https://github.com/OSSBlade/openblade-core/pull/55))
- Add multi-user Windows support. Only the active interactive session can change hardware, each user keeps separate profiles and overrides, and the previous owner's saved configuration migrates. The Session Agent starts for every user, and background sessions lose control without seeing another user's profiles. Shutting down the service needs an administrator, and service IPC is now version 34. ([#45](https://github.com/OSSBlade/openblade-core/pull/45))

## [0.1.0-nightly.6] - 2026-08-04

### Added

- Add a read-only firmware assistant for supported Blade models. It shows installed BIOS, EC and MCU versions beside the versions on the model's Razer support page, with its own check interval. It downloads and runs nothing. ([#45](https://github.com/OSSBlade/openblade-core/pull/45))
- Add read-only resume validation records to diagnostic bundles. They compare control state before suspend and after resume and name any subsystems that changed. Diagnostics also report the existing owned-lighting restoration. ([#45](https://github.com/OSSBlade/openblade-core/pull/45))

## [0.1.0-nightly.5] - 2026-08-03

### Changed

- Reduce idle CPU wakeups: the hidden tray now refreshes live readings only on hover and keeps the last reading between hovers, and it runs on efficient processors on hybrid CPUs. ([813f0820](https://github.com/OSSBlade/openblade-core/commit/813f0820))

### Fixed

- Add a Launch action after a successful graphical install or repair, so a normally started upgrade no longer leaves the Tray and Session Agent stopped until the next sign-in. Quiet installs still relaunch the Tray explicitly. ([#24](https://github.com/OSSBlade/openblade-core/pull/24))
- Fix rapid performance-key presses queuing stale notifications. The Windows notification now shows the newest selected mode. ([#32](https://github.com/OSSBlade/openblade-core/pull/32))
- Fix update checks failing when the computer is offline. They now show a connectivity status and retry later. ([#5](https://github.com/OSSBlade/openblade-core/pull/5))
- Fix the graphical uninstaller not detecting a same-version OpenBlade install. ([#5](https://github.com/OSSBlade/openblade-core/pull/5))
- Fix tray hover waking hidden UI work after the tooltip refreshed. ([9f42387f](https://github.com/OSSBlade/openblade-core/commit/9f42387f))
- Fix the RZ09-0581 firmware smoke test not recognizing the Audio Meter lighting effect. ([99030c1f](https://github.com/OSSBlade/openblade-core/commit/99030c1f))
- Fix app accent changes in Light and Dark modes. ([#1](https://github.com/OSSBlade/openblade-core/pull/1))
- Fix Ambient Awareness staying broken after a screen-capture failure until the Session Agent restarted or the region changed. It now recovers automatically. ([#1](https://github.com/OSSBlade/openblade-core/pull/1))
- Fix M1 and M2 shortcut selection using the binding's screen-reader label. ([#1](https://github.com/OSSBlade/openblade-core/pull/1))
- Fix M5 microphone mute handling after Session Agent startup, so the Windows mute toggle and the key's red indicator both work. ([#1](https://github.com/OSSBlade/openblade-core/pull/1))
- Fix RZ09-0581 M1 and M2 custom shortcuts so each assigned action runs before the accompanying Page Up or Page Down key is suppressed. ([#1](https://github.com/OSSBlade/openblade-core/pull/1))

### Added

- License OpenBlade under the GNU General Public License version 3 or later. ([#5](https://github.com/OSSBlade/openblade-core/pull/5))
- Add an uninstall choice to keep saved profiles and settings (the default) or remove them. ([#5](https://github.com/OSSBlade/openblade-core/pull/5))

- Add a **Reapply OpenBlade settings** action for controls changed outside OpenBlade. It reapplies the active profile and manual overrides to the affected controls and clears each warning once the change is confirmed. When manual fan control blocks safe Power mode recovery, the warning stays and explains how to proceed. Service IPC is now version 33. ([bfe066ba](https://github.com/OSSBlade/openblade-core/commit/bfe066ba))
- Add daily and manual nightly installers built from `main`, published as GitHub prereleases with SHA-256 metadata. The About page can check either the Stable or Nightly release channel. ([#5](https://github.com/OSSBlade/openblade-core/pull/5))
- Add host-rendered Audio Meter keyboard lighting to the Razer Blade 16 (2026), `RZ09-0581`. ([#1](https://github.com/OSSBlade/openblade-core/pull/1))
- Add keyboard-brightness support for the Razer Blade 16 `RZ09-0528` (USB `1532:02C6`) on BIOS `2.02`, with AC presets, Custom CPU/GPU levels, fans (Automatic, fixed, maximum, curve), battery protection and 50-80 percent charge limits, temporary full charge, keyboard brightness and Off, logo Off/Static/Breathing, startup animation, function-key primary behavior, and Gaming mode. Static and Spectrum keyboard lighting work. Starlight, Ripple, Tidal, Wave, and Wheel are listed but read-only. CPU Power Limit, CPU Voltage Optimizer, and GPU overclock ranges are shown read-only. Windows Dynamic Lighting exposes the keyboard, and OpenBlade can hand keyboard lighting over to Windows; while handed over, OpenBlade's own lighting is off and the tray links to Windows lighting settings. M1-M5 shortcuts stay hidden for now. Firmware smoke, Diagnostics, About, and the tested-platform check now use the RZ09-0528 / PID `02C6` identity. ([#1](https://github.com/OSSBlade/openblade-core/pull/1))
- Add CPU-temperature support for the RZ09-0528 using its own Windows thermal-zone counter. ([#1](https://github.com/OSSBlade/openblade-core/pull/1))
- Add GitHub release checks to About with Every startup, Daily, Weekly, Monthly, and Manual only intervals. Available releases open in your browser. ([7faa39b4](https://github.com/OSSBlade/openblade-core/commit/7faa39b4))
- Add battery health to About, read from Windows' design and full-charge capacity data. ([b15e14b7](https://github.com/OSSBlade/openblade-core/commit/b15e14b7))
- Add a Synapse-style playback mute indicator for the RZ09-0581 keyboard. While Windows audio is muted, the F1 mute key stays red without replacing the selected lighting effect. ([96cfa857](https://github.com/OSSBlade/openblade-core/commit/96cfa857))
- Add an automated MCU firmware smoke-test workflow. It captures a read-only baseline before an update, compares it with the state afterward, and offers a reversible rehearsal of performance, fans, battery, lighting, startup animation, function-key behavior, and Gaming mode. ([5312a802](https://github.com/OSSBlade/openblade-core/commit/5312a802))
- Add a tested system-version catalog for the RZ09-0581, BIOS, EC, and MCU. The Dashboard warns when detected versions are unverified and links to compatibility details on About. Controls stay enabled. ([b518775f](https://github.com/OSSBlade/openblade-core/commit/b518775f))
- Add persistent, redacted logs for non-fatal Tray and Session Agent failures, included in diagnostic exports. ([c5352eaf](https://github.com/OSSBlade/openblade-core/commit/c5352eaf))
- Add a compact Windows-style flyout for built-in display brightness changes made with the function keys. ([1da4eea6](https://github.com/OSSBlade/openblade-core/commit/1da4eea6))
- Fix HDR changes on systems that report access denied after applying the requested state. OpenBlade now checks the actual display state. ([#1](https://github.com/OSSBlade/openblade-core/pull/1))
- List the installed app, Session Agent, and Service build versions and commit hashes in About. ([c9b444ba](https://github.com/OSSBlade/openblade-core/commit/c9b444ba))
- Add a function-row bridge for RZ09-0581 Driver mode. It handles volume, playback, Project, Print Screen, display brightness, and keyboard brightness keys, and respects the configured Function keys or Multimedia keys primary layer. ([07c823c7](https://github.com/OSSBlade/openblade-core/commit/07c823c7))
- Show the confirmed keyboard Driver-mode state in Troubleshooting. ([07c823c7](https://github.com/OSSBlade/openblade-core/commit/07c823c7))
- Add NVIDIA GPU core and memory clock offsets to Custom power mode. Ranges come from the installed NVIDIA driver, and offsets are saved in profiles, overrides, imports, and exports. ([5f4d4280](https://github.com/OSSBlade/openblade-core/commit/5f4d4280))
- Add persistent M1 through M5 Fn-layer bindings on the Laptop page. Keys can run keyboard shortcuts, mouse and media actions, microphone mute, app or website launch, text entry, and OpenBlade power, Gaming mode, or lighting actions. Each key keeps its default behavior until you override it. ([34ca723d](https://github.com/OSSBlade/openblade-core/commit/34ca723d))
- Add repository guidance for contributors and AI agents: codebase map, hardware-safety boundaries, deployment rules, versioning, schema and IPC migration policy, and commit format. ([34ca723d](https://github.com/OSSBlade/openblade-core/commit/34ca723d))
- Add local tray crash reports under `%LocalAppData%\OpenBlade\Crashes` with version, runtime, exception details, and stack traces. The newest 20 files are kept. ([0af9cb99](https://github.com/OSSBlade/openblade-core/commit/0af9cb99))
- Add the same crash reports to the Session Agent. ([18b8efc4](https://github.com/OSSBlade/openblade-core/commit/18b8efc4))
- Add a customizable Dashboard card picker. Pin power, fan, battery, keyboard, startup, lighting, and display cards and drag them into order. ([75389ff6](https://github.com/OSSBlade/openblade-core/commit/75389ff6))
- Add smooth Dashboard card add, remove, and reorder transitions. ([4812ebd1](https://github.com/OSSBlade/openblade-core/commit/4812ebd1))
- Add persistent section customization to Profiles, Display, Laptop, Lighting, Settings, and About. Hide sections or drag them into your preferred order. ([415de370](https://github.com/OSSBlade/openblade-core/commit/415de370))
- Add persistent left-navigation ordering and app accent choices: Razer green, the Windows accent, or a custom RGB color. ([4812ebd1](https://github.com/OSSBlade/openblade-core/commit/4812ebd1))
- Add a branded WiX Burn installer with Install, Repair, and Uninstall actions and a Windows Installed apps entry. It migrates earlier standalone installations and keeps profiles by default. The standalone setup remains for development and recovery, including `uninstall --purge`. ([7faa39b4](https://github.com/OSSBlade/openblade-core/commit/7faa39b4))
- Add Microsoft Store readiness checks for silent offline installation, Burn upgrades, supported-hardware disclosure, and signature inventory. Release publishing now writes a report that rejects unsigned third-party payloads and can verify the public privacy policy. ([7faa39b4](https://github.com/OSSBlade/openblade-core/commit/7faa39b4))
- Add JSON profile import alongside export. Profile files are versioned so later releases can migrate older ones. ([4f908de3](https://github.com/OSSBlade/openblade-core/commit/4f908de3))
- Add function-key behavior, Gaming mode, and startup-animation settings to the AC, USB-C, and Battery profiles. They apply on power-source and lifecycle changes. Older profiles leave these controls unchanged until you save them. ([0506371a](https://github.com/OSSBlade/openblade-core/commit/0506371a))
- Add per-profile Gaming mode options to block the Windows key and Alt+Tab while Gaming mode is active. ([fed7ebac](https://github.com/OSSBlade/openblade-core/commit/fed7ebac))

### Changed

- Clarify that the RZ09-0528 CPU Power Limit and CPU Voltage Optimizer cards are read-only because they need an AMD kernel driver that OpenBlade does not ship. ([#1](https://github.com/OSSBlade/openblade-core/pull/1))
- Fix graphical-installer upgrades to compare installed versions and recover from orphaned markers. The installer now requires Windows 11 version 24H2 x64. ([145f4dd1](https://github.com/OSSBlade/openblade-core/commit/145f4dd1))
- Add the signed Microsoft Visual C++ runtime to the offline installer, replacing stale 14.x runtimes, and add signing hooks for public builds. ([e685ef95](https://github.com/OSSBlade/openblade-core/commit/e685ef95))
- Ask for consent before the first GitHub update check. Checks now use success-based intervals, ETag caching, and rate-limit backoff. ([145f4dd1](https://github.com/OSSBlade/openblade-core/commit/145f4dd1))
- Advance the service IPC protocol to version 24 for the battery-health field. Upgrade the Service, Tray, and Session Agent together with the coordinated installer. ([5ed12109](https://github.com/OSSBlade/openblade-core/commit/5ed12109))
- Rapid edits to lighting, charge limit, and GPU clocks now keep only the latest value and queue at most one follow-up while a write is running. ([0d663e10](https://github.com/OSSBlade/openblade-core/commit/0d663e10))
- Setup rollback now treats restoring the previous installation as final, even if cleanup of its transaction directory fails. ([5dc5c295](https://github.com/OSSBlade/openblade-core/commit/5dc5c295))
- Function-row interception now needs a confirmed keyboard Driver-mode state. When the state is unknown, keys pass through. ([f8c6c08e](https://github.com/OSSBlade/openblade-core/commit/f8c6c08e))
- Update the light and dark tray icons for clearer taskbar visibility and add matching Custom power-mode icons. ([747db32c](https://github.com/OSSBlade/openblade-core/commit/747db32c))
- Shrink each Ambient Awareness request to one compact payload and remove a redundant buffer allocation. ([164ebece](https://github.com/OSSBlade/openblade-core/commit/164ebece))
- Detect service disconnections within two seconds, so the tray no longer shows a stale connected state. ([10509cab](https://github.com/OSSBlade/openblade-core/commit/10509cab))
- Close the Tray and Session Agent service connections cleanly on shutdown. ([04b533b2](https://github.com/OSSBlade/openblade-core/commit/04b533b2))
- Make keyboard-brightness keys match Synapse's firmware cadence: each keypress sends one brightness step instead of rereading and reapplying the whole lighting effect. ([e9ac0186](https://github.com/OSSBlade/openblade-core/commit/e9ac0186))
- Separate the Game mode, performance mode, and microphone-mute keys from the customizable Fn-layer M3, M4, and M5 keys. Their normal actions work in keyboard Driver mode without changing Fn-layer bindings. ([18b8efc4](https://github.com/OSSBlade/openblade-core/commit/18b8efc4))
- Show performance-key feedback immediately. OpenBlade shows the Windows notification for the next mode before the write finishes, and a failure notification follows if it cannot be applied. ([18b8efc4](https://github.com/OSSBlade/openblade-core/commit/18b8efc4))
- Make the tray icon follow the performance mode queued by the performance key, and return to the confirmed mode if the write fails. ([fed7ebac](https://github.com/OSSBlade/openblade-core/commit/fed7ebac))
- Make About-page action rows fully clickable with a Windows Settings-style hover and pressed highlight. The Troubleshooting expander and trailing chevrons match. ([72fb3641](https://github.com/OSSBlade/openblade-core/commit/72fb3641))
- Add a Raw Input cross-check for the function row. If Windows removes the session keyboard hook, OpenBlade finishes the current media action and reinstalls the hook. ([72fb3641](https://github.com/OSSBlade/openblade-core/commit/72fb3641))
- Keep function-row media keys working through brief firmware readback gaps by using the active profile's saved function-key behavior. ([8ef7a8d7](https://github.com/OSSBlade/openblade-core/commit/8ef7a8d7))
- Reduce foreground CPU use: the UI updates only changed bindings, disabled live readings no longer poll, unrelated Session Agent features ignore telemetry changes, and Ambient Awareness samples less often once the screen is static. ([69f5809d](https://github.com/OSSBlade/openblade-core/commit/69f5809d))
- Stop rebuilding the collapsed Dashboard telemetry while another page is open or the window is hidden. The Dashboard refreshes once when it becomes visible. ([0c072491](https://github.com/OSSBlade/openblade-core/commit/0c072491))
- Make Ambient Awareness smoother during motion with a 30 FPS renderer, dropping to 5 FPS after about half a second without a color change. ([6694bc63](https://github.com/OSSBlade/openblade-core/commit/6694bc63))
- Raise Wave, Wheel, and Tidal playback to 30 FPS by interpolating between captured frames. ([561ce055](https://github.com/OSSBlade/openblade-core/commit/561ce055))
- Keep media-key translation working through brief keyboard Driver-mode read failures, and report an unknown Driver-mode state accurately in Troubleshooting. ([c8bced82](https://github.com/OSSBlade/openblade-core/commit/c8bced82))
- Prevent periodic Driver-mode checks from colliding with animated-lighting frame writes. ([c8bced82](https://github.com/OSSBlade/openblade-core/commit/c8bced82))
- Move `Use profile settings` from an overflow menu to a page-header action on Dashboard, Laptop, Lighting, and Display whenever the page differs from its active profile, including changes made outside OpenBlade. Page header actions share one size, and healthy Troubleshooting rows show plain status text. ([0b164097](https://github.com/OSSBlade/openblade-core/commit/0b164097))
- GPU clock-only changes in Custom mode now update only the NVIDIA clock offsets and skip redundant firmware writes. ([b1dc999e](https://github.com/OSSBlade/openblade-core/commit/b1dc999e))
- Remove the redundant Reset button from the app accent color setting. OpenBlade green is still selectable from the picker. ([e62d5b4e](https://github.com/OSSBlade/openblade-core/commit/e62d5b4e))
- Rename the default accent from Razer green to OpenBlade green. Its `#47E10C` value is unchanged. ([9daeae1b](https://github.com/OSSBlade/openblade-core/commit/9daeae1b))
- Move Display above Laptop in the default left-navigation order. Custom orders are kept. ([98f5e662](https://github.com/OSSBlade/openblade-core/commit/98f5e662))
- Replace the navigation-order popup with an inline reorderable Settings card, and collapse the navigation and taskbar-hover editors by default. ([2cfe2f65](https://github.com/OSSBlade/openblade-core/commit/2cfe2f65))
- Move power mode, fan controls, and battery controls to the Laptop page. They remain available as optional Dashboard cards. Profile comparison cells say `Not set` when a profile does not manage a setting. ([587e3803](https://github.com/OSSBlade/openblade-core/commit/587e3803))
- Reduce Session Agent background work by sharing one service-status feed across display and lighting features, checking idle lighting on a deadline, and trimming per-event allocations in the Raw Input path. ([ed106d68](https://github.com/OSSBlade/openblade-core/commit/ed106d68))
- Reduce the Windows payload size by shipping only the Windows App SDK components OpenBlade uses and dropping development symbols and unused satellite assemblies, AI, ML, and Widgets components. ([ed106d68](https://github.com/OSSBlade/openblade-core/commit/ed106d68))
- Save the last applied keyboard lighting effect and restore it when the service starts. Other Razer software wins by default; an opt-in setting lets OpenBlade reapply external lighting changes. ([4f908de3](https://github.com/OSSBlade/openblade-core/commit/4f908de3))

- Move protocol capture evidence and capture tooling to the `OSSBlade/openblade-captures` repository, included as the `captures` submodule. ([a3bd6478](https://github.com/OSSBlade/openblade-core/commit/a3bd6478))
- Keep Off first in the lighting effect picker and sort the remaining effects alphabetically. ([4f908de3](https://github.com/OSSBlade/openblade-core/commit/4f908de3))
- Update project, documentation, issue, release-note, and branding links to the OSSBlade GitHub organization. ([4f908de3](https://github.com/OSSBlade/openblade-core/commit/4f908de3))
- Align the Settings header spacing with the Display and Lighting pages, and align the service and laptop status values in Troubleshooting. ([4f908de3](https://github.com/OSSBlade/openblade-core/commit/4f908de3))
- Advance the local configuration schema to version 7, profile export schema to version 3, persistent override schema to version 4, and service IPC to version 19 for GPU clock-offset settings, readback capabilities, and keyboard Driver-mode status. ([5f4d4280](https://github.com/OSSBlade/openblade-core/commit/5f4d4280))

### Fixed

- Fix shortcut action pickers that changed visually without saving the new binding. Custom RZ09-0581 M1 and M2 actions now also block the paired native Page Up or Page Down key while pressed; Default keeps the native key. ([#1](https://github.com/OSSBlade/openblade-core/pull/1))
- Harden keyboard Driver-mode handling around Windows low-power transitions. OpenBlade stops periodic keyboard-mode writes while low power is active and returns the keyboard to Normal mode first. Display-off notifications do not release Driver mode. ([#1](https://github.com/OSSBlade/openblade-core/pull/1))
- Fix Audio Meter shutdown, limit manual fan curves to each laptop's reported RPM range, and make PID `02C6` reactive-key pulses safe during shutdown. ([#1](https://github.com/OSSBlade/openblade-core/pull/1))
- Remove the redundant Windows Dynamic Lighting settings link from its ownership row. ([#1](https://github.com/OSSBlade/openblade-core/pull/1))
- Hide Gaming mode key-blocking options while Gaming mode is off. ([#1](https://github.com/OSSBlade/openblade-core/pull/1))
- Restore the pointer-over highlight on settings cards, add it to expandable Settings rows, and clear stale highlights after scrolling or pointer changes. ([#1](https://github.com/OSSBlade/openblade-core/pull/1))
- Fix long card messages widening tray pages and running past the right edge at compact window sizes. ([#1](https://github.com/OSSBlade/openblade-core/pull/1))
- Fix temporary display-off lighting replacing the saved lighting effect with Off. Unplugging and immediately closing the lid now keeps the battery profile's lighting after resume or a service restart. ([671563e1](https://github.com/OSSBlade/openblade-core/commit/671563e1))
- Fix the tray closing when a Settings, display-profile, customization, or accent color action fails. ([86ddf8bb](https://github.com/OSSBlade/openblade-core/commit/86ddf8bb))
- Fix page scrollbars staying visible instead of auto-hiding. ([1d152ac3](https://github.com/OSSBlade/openblade-core/commit/1d152ac3))
- Fix service shutdown hanging when a hardware operation or lighting-port cleanup stalls. New operations are rejected right away and the service stops after the configured deadlines. ([9b54abae](https://github.com/OSSBlade/openblade-core/commit/9b54abae))
- Save rapid preference changes with at most one write in flight plus one follow-up, and still save the final state on shutdown. ([03f60d48](https://github.com/OSSBlade/openblade-core/commit/03f60d48))
- Update the About page right away when delayed battery-health data becomes available or unavailable. ([f033aaf2](https://github.com/OSSBlade/openblade-core/commit/f033aaf2))
- Fix the playback mute indicator after a rejected write; it now reapplies the latest Windows mute state. ([2f3e78b3](https://github.com/OSSBlade/openblade-core/commit/2f3e78b3))
- Retry transient battery-state read failures before blocking controls. Writes are not retried. ([cdccdb83](https://github.com/OSSBlade/openblade-core/commit/cdccdb83))
- Use sentence case for the About page's component labels. ([8515743e](https://github.com/OSSBlade/openblade-core/commit/8515743e))
- Fix false Performance-key failure notifications when the final selected mode was applied. ([baa74173](https://github.com/OSSBlade/openblade-core/commit/baa74173))
- Save Gaming mode's Windows-key and Alt+Tab toggles to the active power profile immediately. ([6060d9b9](https://github.com/OSSBlade/openblade-core/commit/6060d9b9))
- Fix duplicate Session Agent launches consuming log entries. ([a0cb0dc7](https://github.com/OSSBlade/openblade-core/commit/a0cb0dc7))
- Wait 1.25 seconds after the last performance-key press before sending the mode to the firmware. Notifications still update immediately. ([28726e28](https://github.com/OSSBlade/openblade-core/commit/28726e28))
- Make the microphone-mute key update every Windows default capture device, not only the Multimedia one. ([2e40d531](https://github.com/OSSBlade/openblade-core/commit/2e40d531))
- Return the keyboard firmware to Normal mode when OpenBlade shuts down. ([02e259ef](https://github.com/OSSBlade/openblade-core/commit/02e259ef))
- Make F8 and F9 change the built-in display brightness by 10 percentage points, using levels Windows reports. ([c9b444ba](https://github.com/OSSBlade/openblade-core/commit/c9b444ba))
- Keep keyboard-brightness changes working when battery readback fails. ([c9b444ba](https://github.com/OSSBlade/openblade-core/commit/c9b444ba))
- Fix the performance shortcut being ignored after an external performance or fan change. The performance key and Fn+P now select the next mode, and external-change warnings stay. ([21a295f5](https://github.com/OSSBlade/openblade-core/commit/21a295f5))
- Restore the firmware Driver-mode handling that the extended Fn layer, performance key, and M1 through M5 need. OpenBlade checks the mode every 15 seconds and writes only after a confirmed fallback to Normal mode. ([3a5f7abd](https://github.com/OSSBlade/openblade-core/commit/3a5f7abd))
- Restore media and brightness-key behavior while firmware Driver mode is active. Held keys repeat normally and one-shot actions ignore auto-repeat. ([07c823c7](https://github.com/OSSBlade/openblade-core/commit/07c823c7))
- Stop treating Razer Elevation Service and Razer Game Manager Service 3 as laptop controllers. Synapse and Chroma components that compete for the same controls still show as conflicts. ([3a5f7abd](https://github.com/OSSBlade/openblade-core/commit/3a5f7abd))
- Fix performance-key notifications in the Session Agent by including the Microsoft-signed Windows App Runtime notification resource. ([415de370](https://github.com/OSSBlade/openblade-core/commit/415de370))
- Check keyboard-shortcut and website values before M1 through M5 override their normal behavior. Invalid actions show a message and the key keeps its default action. ([18ab090a](https://github.com/OSSBlade/openblade-core/commit/18ab090a))
- Fix option pickers losing their visible selection during startup and status refreshes. ([1d5d64da](https://github.com/OSSBlade/openblade-core/commit/1d5d64da))
- Treat known current values for `Not set` profile fields as saveable changes. Display, laptop behavior, and lid-logo pages now show the unsaved prompt and profile save action. ([3ed0c0b2](https://github.com/OSSBlade/openblade-core/commit/3ed0c0b2))
- Make Fn+P use the Windows P-key scan code observed on the target laptop. ([219f36f4](https://github.com/OSSBlade/openblade-core/commit/219f36f4))
- Keep tray hover telemetry updating while the main window is hidden and read only the sensors the Dashboard or hover label shows. NVIDIA GPU sensors are read only while the GPU is active, and missing or powered-down GPU temperatures are hidden. ([278d68c8](https://github.com/OSSBlade/openblade-core/commit/278d68c8))
- Fix duplicated Dashboard control cards showing stale visibility, availability, selection, or loading state. ([a2bb68e6](https://github.com/OSSBlade/openblade-core/commit/a2bb68e6))
- Keep the Power mode, Fans, and Battery controls visible and usable during a brief adapter read failure. Fix Dashboard content showing under the Settings navigation item after reordering the selected Settings page. ([7cee2e12](https://github.com/OSSBlade/openblade-core/commit/7cee2e12))
- Keep Dashboard card controls and their Laptop, Lighting, and Display page controls in sync after startup, so source-page controls no longer stay disabled. ([c99f36b9](https://github.com/OSSBlade/openblade-core/commit/c99f36b9))
- Fix the Dashboard customizer crashing when the default card is unchecked. Edits apply when you select Done, without restarting the tray, and selection controls align with the dialog's right edge. ([75389ff6](https://github.com/OSSBlade/openblade-core/commit/75389ff6))
- Restore the outlined application icon so the taskbar glyph stays legible on a dark taskbar. ([75389ff6](https://github.com/OSSBlade/openblade-core/commit/75389ff6))
- Fix the approved Tidal and Wave/Wheel oracle fixtures failing hash checks in fresh clones by checking them out with CRLF line endings. ([7a208a91](https://github.com/OSSBlade/openblade-core/commit/7a208a91))
- Fix closed information banners reserving empty space, so the first section gap matches across Dashboard, Lighting, Display, and Settings. ([4db2540a](https://github.com/OSSBlade/openblade-core/commit/4db2540a))
- Sort the Lighting page's effect picker alphabetically. ([32c1c889](https://github.com/OSSBlade/openblade-core/commit/32c1c889))
- Show one `Unavailable` label for the dashboard fan reading when neither fan sensor has a value. ([29d1ad6a](https://github.com/OSSBlade/openblade-core/commit/29d1ad6a))
- Fix the selected lighting effect being replaced by the external-effect placeholder while a write is pending. ([7a571e88](https://github.com/OSSBlade/openblade-core/commit/7a571e88))
- Use OpenBlade green (`#47E10C`) as the default and recovery color for lighting effects when no color is available. ([9daeae1b](https://github.com/OSSBlade/openblade-core/commit/9daeae1b))
- Fix Reactive and Ripple key input being blocked by detection of other Razer software. ([a4785ac0](https://github.com/OSSBlade/openblade-core/commit/a4785ac0))
- Fix dashboard telemetry columns clipping the last reading, and keep all three groups on one row at the minimum window width. ([21a64586](https://github.com/OSSBlade/openblade-core/commit/21a64586))
- Switch the profile comparison to its compact layout sooner and move pending-change labels below profile names so badges no longer overlap the next column. ([caf6d065](https://github.com/OSSBlade/openblade-core/commit/caf6d065))
- Fix legacy zero-brightness visible effects restoring at 100% in profile and override recovery. They now use the same 50% fallback as the Lighting page. ([14088b2a](https://github.com/OSSBlade/openblade-core/commit/14088b2a))
- Keep laptop behavior controls on their last known selection and disabled while readback loads, and remove a garbled function-key fallback label. ([c4bd0014](https://github.com/OSSBlade/openblade-core/commit/c4bd0014))
- Keep the last connected dashboard state during brief service reconnects so controls no longer flash unavailable. ([3d29fa87](https://github.com/OSSBlade/openblade-core/commit/3d29fa87))
- Use consistent separators in dashboard live readings, show one label for fully unavailable temperatures, and remove extra footer margins. ([24047c05](https://github.com/OSSBlade/openblade-core/commit/24047c05))
- Read telemetry between the ten-second full-state checks, so visible refreshes no longer repeat capability, firmware, and control-state reads. Settings changed elsewhere are still detected. ([c050af28](https://github.com/OSSBlade/openblade-core/commit/c050af28))
- Add separate time limits for display-agent connect, query, change, and response steps. A client disconnect or stall affects only its own connection. ([c050af28](https://github.com/OSSBlade/openblade-core/commit/c050af28))
- Enforce an 800 by 600 DIP minimum window size that fits the display's usable area at high scaling. ([c050af28](https://github.com/OSSBlade/openblade-core/commit/c050af28))
- Fix the laptop briefly showing as missing after a transient HID probe failure. ([4d7de125](https://github.com/OSSBlade/openblade-core/commit/4d7de125))
- Fix concurrent Reactive and Ripple status updates overwriting each other. ([c050af28](https://github.com/OSSBlade/openblade-core/commit/c050af28))
- Treat failed or incomplete device enumeration as an error instead of a complete device list. ([c050af28](https://github.com/OSSBlade/openblade-core/commit/c050af28))
- Reuse one connection for Reactive and Ripple key bursts, and keep the Raw Input listener alive through brief status-stream failures. ([bcdd29a5](https://github.com/OSSBlade/openblade-core/commit/bcdd29a5))
- Retry Reactive and Ripple key pulses after a brief send failure instead of dropping the first pulse. ([c3f5eac8](https://github.com/OSSBlade/openblade-core/commit/c3f5eac8))
- Keep a healthy lighting renderer running through temporary firmware-read failures, so the laptop no longer flashes unavailable and key-triggered effects continue. ([bcdd29a5](https://github.com/OSSBlade/openblade-core/commit/bcdd29a5))
- Raise host-rendered fades to 30 frames per second with unchanged effect durations, and start unknown visible brightness at 50%. ([bcdd29a5](https://github.com/OSSBlade/openblade-core/commit/bcdd29a5))
- Fix stalled or truncated display IPC frames affecting more than one client connection. ([df37652f](https://github.com/OSSBlade/openblade-core/commit/df37652f))
- Keep Reactive and Ripple key pulses through brief stalls instead of canceling them after 100 ms. ([24104b23](https://github.com/OSSBlade/openblade-core/commit/24104b23))
- Keep fixed-speed and manual-curve fan settings across service restarts. ([df37652f](https://github.com/OSSBlade/openblade-core/commit/df37652f))
- Skip invalid saved lighting overrides at startup instead of retrying them every time. ([df37652f](https://github.com/OSSBlade/openblade-core/commit/df37652f))
- Refresh the UI when only fan-control details or lighting-renderer ownership change. ([df37652f](https://github.com/OSSBlade/openblade-core/commit/df37652f))
- Fix lighting cleanup returning before the hardware was released when several callers ran at once. ([df37652f](https://github.com/OSSBlade/openblade-core/commit/df37652f))
- Fix a failed HID cancellation blocking the hardware session indefinitely. ([df37652f](https://github.com/OSSBlade/openblade-core/commit/df37652f))
- Add a two-second read deadline so incomplete local requests cannot occupy every service pipe listener. ([df37652f](https://github.com/OSSBlade/openblade-core/commit/df37652f))
- Keep exclusive ownership of the service pipe from startup so another local process cannot claim its name. ([df37652f](https://github.com/OSSBlade/openblade-core/commit/df37652f))
- Run setup service-control operations from the Windows System32 `sc.exe`. ([df37652f](https://github.com/OSSBlade/openblade-core/commit/df37652f))
- Apply the protected machine-data permissions to existing files and folders, not only the parent folder. ([df37652f](https://github.com/OSSBlade/openblade-core/commit/df37652f))
- Stop interactive keyboard pulses from waiting behind periodic firmware checks, and keep status through routine subscription rotation. ([c83fdf61](https://github.com/OSSBlade/openblade-core/commit/c83fdf61))
- Keep repeated key-triggered lighting pulses working, keep Raw Input active across brief service reconnects, and refresh cached input-device identity after device changes. ([bd598c48](https://github.com/OSSBlade/openblade-core/commit/bd598c48))
- Fix one transient device access failure disabling every control. ([bd598c48](https://github.com/OSSBlade/openblade-core/commit/bd598c48))
- Keep the last confirmed capabilities when a refresh fails, and reduce controller and external-power discovery work. ([24104b23](https://github.com/OSSBlade/openblade-core/commit/24104b23))
- Reuse one Ambient Awareness connection and the admitted lighting port instead of reconnecting for every 20 Hz frame. ([24104b23](https://github.com/OSSBlade/openblade-core/commit/24104b23))
- Make dashboard telemetry rows fit their content so unavailable temperature values wrap without clipping or extra space. ([bd598c48](https://github.com/OSSBlade/openblade-core/commit/bd598c48))
- Fix an unconfirmed visible lighting effect showing 0% brightness while the firmware used another value. It now defaults to 50%. ([bd598c48](https://github.com/OSSBlade/openblade-core/commit/bd598c48))
- Apply the OpenBlade application icon to every executable project. ([bd598c48](https://github.com/OSSBlade/openblade-core/commit/bd598c48))
- Remove the glitchy long-distance navigation selection-indicator animation. ([c83fdf61](https://github.com/OSSBlade/openblade-core/commit/c83fdf61))
- Enforce an 800 by 600 DIP minimum app-window size to protect responsive layouts. ([c83fdf61](https://github.com/OSSBlade/openblade-core/commit/c83fdf61))

### Added

- Remove the optional UIAccess input companion. Interactive lighting and performance shortcuts work in normal apps; elevated apps are a Windows input limit. ([c3a0411e](https://github.com/OSSBlade/openblade-core/commit/c3a0411e))
- Export all saved AC, USB-C, and battery profiles as JSON from the Profiles page. ([0c9881be](https://github.com/OSSBlade/openblade-core/commit/0c9881be))
- Add short transitions for conditional Power mode and fan-control subcards. ([0c9881be](https://github.com/OSSBlade/openblade-core/commit/0c9881be))
- Show green accent dots on navigation items with unsaved profile changes. ([0c9881be](https://github.com/OSSBlade/openblade-core/commit/0c9881be))
- Add adjustable Fixed Fan Speed from 2,000 to 5,400 RPM in 100 RPM steps, with a slider that reflects the active target. ([fc5401c3](https://github.com/OSSBlade/openblade-core/commit/fc5401c3))
- Add CPU and GPU manual fan-curve editing and an optional hotter-sensor policy. Curve writes stop if another app exits firmware manual mode, and missing or critical temperature data sets fans to 5,400 RPM. ([fc5401c3](https://github.com/OSSBlade/openblade-core/commit/fc5401c3))
- Manual fan curves read CPU-side temperature from the firmware thermal zone and GPU temperature from NVIDIA, with no sensor driver to install. ([8adb5439](https://github.com/OSSBlade/openblade-core/commit/8adb5439))
- Add Synapse evidence showing Manual Fan Curve is a pair of per-fan RPM writes calculated by the host. ([fc5401c3](https://github.com/OSSBlade/openblade-core/commit/fc5401c3))

- Read the current Function keys or Media keys state in Settings. ([a325aa12](https://github.com/OSSBlade/openblade-core/commit/a325aa12))
- Read the current Gaming Mode On or Off state in Settings. ([ff08de7d](https://github.com/OSSBlade/openblade-core/commit/ff08de7d))

- Add a confirmed full shutdown in Settings that stops the tray, SessionAgent, and service. ([df0858f1](https://github.com/OSSBlade/openblade-core/commit/df0858f1))
- Add a lightweight per-user SessionAgent that handles display queries and changes. ([2e8ef3fa](https://github.com/OSSBlade/openblade-core/commit/2e8ef3fa))
- Add display controls for fixed refresh rates, Windows Dynamic Refresh Rate, HDR, and the locally installed Razer color profiles. ([e8cf4a65](https://github.com/OSSBlade/openblade-core/commit/e8cf4a65))
- Add keyboard lighting on the RZ09-0581 (PID 02E0): Off, Static, Spectrum Cycling, single/dual/random Breathing, Reactive Short/Medium/Long, color, and brightness. ([438aa0ec](https://github.com/OSSBlade/openblade-core/commit/438aa0ec))
- Add Tidal keyboard lighting with fixed or random two-color palettes and leftward or rightward direction. ([9eead2da](https://github.com/OSSBlade/openblade-core/commit/9eead2da))
- Add Starlight keyboard lighting with one color, two colors, or random colors and Short, Medium, or Long twinkle durations. ([6c4a004e](https://github.com/OSSBlade/openblade-core/commit/6c4a004e))
- Add Wave and Wheel keyboard lighting. Wave moves leftward or rightward; Wheel rotates clockwise or counterclockwise. ([b7c241b0](https://github.com/OSSBlade/openblade-core/commit/b7c241b0))
- Add Ripple keyboard lighting with a selected color and expanding rings per key press. ([e2a5fb9e](https://github.com/OSSBlade/openblade-core/commit/e2a5fb9e))
- Add laptop-logo lighting controls for Static, Breathing, and Off. ([9eead2da](https://github.com/OSSBlade/openblade-core/commit/9eead2da))
- Reactive lighting reads key scan codes through Windows Raw Input and does not record characters, applications, window titles, or input packets. ([2178462f](https://github.com/OSSBlade/openblade-core/commit/2178462f))
- Add Synapse evidence for lighting brightness, the 102-slot matrix, effect timing, dual/random Breathing, and Reactive key placement and decay, and test the supported setup repeatedly on the target laptop. ([438aa0ec](https://github.com/OSSBlade/openblade-core/commit/438aa0ec))
- Add Synapse evidence for the five Ambient Awareness screen regions: Full, Left, Top, Right, and Bottom. Region selection is host-side screen sampling, not a firmware command. ([9c70267f](https://github.com/OSSBlade/openblade-core/commit/9c70267f))
- Add Ambient Awareness with Full screen, Left, Top, Right, and Bottom sampling. Sampling stops when the effect is no longer active. ([0f0ab99e](https://github.com/OSSBlade/openblade-core/commit/0f0ab99e))
- Add a five-second Tidal reference capture for both directions. Raw USB captures stay local. ([9eead2da](https://github.com/OSSBlade/openblade-core/commit/9eead2da))
- Add a one-second Wave and Wheel reference capture covering all four directions. ([b7c241b0](https://github.com/OSSBlade/openblade-core/commit/b7c241b0))
- Add keyboard lighting power policies that turn lighting off while the display is off or the user is idle for 1 to 60 minutes, then restore it when those conditions clear. ([f2caac2e](https://github.com/OSSBlade/openblade-core/commit/f2caac2e))
- Add Performance-button and Fn+P support. Plugged in, it cycles Balanced, Silent, Performance, and Custom; on battery, Balanced and Battery Saver; on USB-C, Balanced stays. ([7375cd9e](https://github.com/OSSBlade/openblade-core/commit/7375cd9e))
- Show a Windows notification after a Performance-button or Fn+P press applies a new mode. ([7733c774](https://github.com/OSSBlade/openblade-core/commit/7733c774))
- Custom mode now offers the captured Low, Medium, and High CPU and GPU choices, and blocks High/High. ([7375cd9e](https://github.com/OSSBlade/openblade-core/commit/7375cd9e))
- Add Startup animation control. Function-key behavior and the firmware Gaming Mode bit are also read from the laptop. ([ff08de7d](https://github.com/OSSBlade/openblade-core/commit/ff08de7d))
- Add a Synapse audit showing Function key behavior and Gaming Mode are shown from Synapse's own saved state, not read from firmware. ([c562dc48](https://github.com/OSSBlade/openblade-core/commit/c562dc48))
- Add developer-only probes for the Function-key and Gaming Mode readbacks on the RZ09-0581 (PID 02E0, BIOS 3.01). Both readbacks are now in the product. ([a325aa12](https://github.com/OSSBlade/openblade-core/commit/a325aa12))
- Add an audit mapping each captured target-device operation to its product control. Trackpad, Snap Tap, and uncaptured effects stay hidden. ([a0099df9](https://github.com/OSSBlade/openblade-core/commit/a0099df9))

### Changed

- Reactive and Ripple key presses no longer compete with other lighting writes, which reduces intermittent input latency and animation stutter. ([0c9881be](https://github.com/OSSBlade/openblade-core/commit/0c9881be))

- Direct Fixed Fan Speed and Manual Fan Curve changes accept the generic manual readback used by PID 02E0. Entering manual control still needs a confirmed Balanced and Automatic state. ([c2ebb52b](https://github.com/OSSBlade/openblade-core/commit/c2ebb52b))
- Reactive and Ripple keep pulses in step with key presses, and animated effects no longer drift as hardware writes take time. ([24104b23](https://github.com/OSSBlade/openblade-core/commit/24104b23))
- Firmware controls show a progress indicator while a read or write is pending, and navigation marks the page with unsaved changes. ([c2ebb52b](https://github.com/OSSBlade/openblade-core/commit/c2ebb52b))
- Lighting color rows are compact; the color picker flyout holds the channels and hex input. Tidal directions are now Outward and Inward. ([c2ebb52b](https://github.com/OSSBlade/openblade-core/commit/c2ebb52b))
- Fix a brief device-detection miss during a lighting action replacing the last verified laptop status with "unavailable". ([c2ebb52b](https://github.com/OSSBlade/openblade-core/commit/c2ebb52b))
- Show Dashboard fan telemetry as a `Fan 1 / Fan 2` pair, name lighting triggers and outcomes in profile comparisons, and let the taskbar hover list omit the OpenBlade name. ([b9342c5a](https://github.com/OSSBlade/openblade-core/commit/b9342c5a))
- Fan mode changes show a progress ring while pending, and switching from Fixed Fan Speed to Manual Fan Curve applies the new target correctly. ([b9342c5a](https://github.com/OSSBlade/openblade-core/commit/b9342c5a))

- Group Dashboard live readings into Battery, Temperatures, and Fans, with a visibility control for each item. ([6ae7b8d7](https://github.com/OSSBlade/openblade-core/commit/6ae7b8d7))
- Show Power source and Power mode as separate, reorderable taskbar hover items. ([6ae7b8d7](https://github.com/OSSBlade/openblade-core/commit/6ae7b8d7))
- Save lighting display-off and inactivity rules separately in the AC, USB-C, and Battery profiles. Schema 4 global rules move into all three profiles when first loaded. ([6ae7b8d7](https://github.com/OSSBlade/openblade-core/commit/6ae7b8d7))
- Switching directly between Fixed, Manual fan curve, and Maximum works without changing your selection. ([6ae7b8d7](https://github.com/OSSBlade/openblade-core/commit/6ae7b8d7))
- Use `#47E10C` as the Razer-green base of the accent palette. ([6ae7b8d7](https://github.com/OSSBlade/openblade-core/commit/6ae7b8d7))
- Give Dashboard colors one role each: accent for the active profile and primary action, green for ready, amber for attention, red for failure. Power-source and unsaved-edit labels are neutral, status pills share one style, and transient checks use progress indicators. ([fc7c4381](https://github.com/OSSBlade/openblade-core/commit/fc7c4381))
- Custom CPU/GPU controls and Fixed/Manual Curve fan controls expand as full-width rows under their Power mode and Fans rows, like Windows Settings. ([0e2edeb1](https://github.com/OSSBlade/openblade-core/commit/0e2edeb1))
- Add CPU-side firmware temperature and NVIDIA GPU temperature to Dashboard live readings. Settings can show or hide each temperature, fan, and battery-power item. ([0e2edeb1](https://github.com/OSSBlade/openblade-core/commit/0e2edeb1))
- Make taskbar hover content a checked, draggable list for the active profile, net battery power, temperatures, and individual fan speeds, within the Windows tooltip length limit. ([0e2edeb1](https://github.com/OSSBlade/openblade-core/commit/0e2edeb1))
- Use OpenBlade green as the WinUI accent, with green, amber, and red kept for ready, attention, and failure. ([9daeae1b](https://github.com/OSSBlade/openblade-core/commit/9daeae1b))
- Apply lighting edits automatically after a short delay, with no Apply button. A newer edit is not cleared by an older one still in progress. ([0e2edeb1](https://github.com/OSSBlade/openblade-core/commit/0e2edeb1))
- Stretch fan-curve slider tracks across their panels and update RPM labels while dragging. ([0e2edeb1](https://github.com/OSSBlade/openblade-core/commit/0e2edeb1))
- Show the full-shutdown action in the Windows critical-action color, with its confirmation dialog kept. ([0e2edeb1](https://github.com/OSSBlade/openblade-core/commit/0e2edeb1))
- Include lid-logo behavior and the display-off and inactivity lighting rules in profile comparison. ([0e2edeb1](https://github.com/OSSBlade/openblade-core/commit/0e2edeb1))
- Fixed, Manual Curve, and Maximum fan actions always send the requested RPM target, so you can change manual targets without returning to Automatic first. ([fc7c4381](https://github.com/OSSBlade/openblade-core/commit/fc7c4381))
- Laptop-logo controls start from the laptop's current state and no longer offer a Leave unchanged choice. If another app selected a keyboard effect that can't be read, OpenBlade says so instead of showing Off. ([fc7c4381](https://github.com/OSSBlade/openblade-core/commit/fc7c4381))
- Align lighting power-policy switches like other settings and center the inactivity value in its NumberBox. ([f9be96b8](https://github.com/OSSBlade/openblade-core/commit/f9be96b8))
- Use right-aligned toggles for Gaming Mode and Startup animation instead of dropdown menus. ([5768a93a](https://github.com/OSSBlade/openblade-core/commit/5768a93a))
- Fix self-contained WinUI builds missing compiled XAML resources, which could crash at startup. Incomplete payloads now fail validation. ([a325aa12](https://github.com/OSSBlade/openblade-core/commit/a325aa12))

- Give laptop-logo lighting its own section on the Lighting page. Function key behavior, Gaming Mode, and Startup animation sit under Laptop behavior with explicit Set menus. ([da88d598](https://github.com/OSSBlade/openblade-core/commit/da88d598))
- Use AC instead of Plugged in for power-source labels. ([da88d598](https://github.com/OSSBlade/openblade-core/commit/da88d598))
- Show pending profile saves beside the page title on Dashboard, Lighting, and Display. The action names the destination profile, restoration is in an overflow menu, and it moves below the title in narrow windows. ([bd79b92d](https://github.com/OSSBlade/openblade-core/commit/bd79b92d))
- Keep Lighting in navigation when its device can't be accessed. The page shows the configured values in a disabled editor and says whether the service, laptop detection, or another Razer app is the cause. ([0ad85dd6](https://github.com/OSSBlade/openblade-core/commit/0ad85dd6))
- Fix a healthy service sometimes appearing unavailable in the UI. ([f81c4965](https://github.com/OSSBlade/openblade-core/commit/f81c4965))
- Validate fixed and random Tidal in both directions, every Starlight variant, both Wave directions, both Wheel rotations, and laptop-logo Breathing, Off, and Static restoration before the lighting controls are enabled. ([b7c241b0](https://github.com/OSSBlade/openblade-core/commit/b7c241b0))

- Update About with the 256 px OpenBlade artwork, a Versions card for app and service builds, the independent-project note in the title card, and borderless action icons. ([df0858f1](https://github.com/OSSBlade/openblade-core/commit/df0858f1))
- Let profiles capture Windows' current refresh rate, HDR state, and color profile before saving the draft. ([46aacba3](https://github.com/OSSBlade/openblade-core/commit/46aacba3))
- Align the Troubleshooting header with the other About card content. ([46aacba3](https://github.com/OSSBlade/openblade-core/commit/46aacba3))
- Named-pipe protocol version 14 carries Custom CPU/GPU settings. ([7375cd9e](https://github.com/OSSBlade/openblade-core/commit/7375cd9e))
- Profile schema version 4 stores Custom CPU/GPU settings and migrates schema version 3 profiles with the captured Medium/High default. Schema version 3 stores the global lighting power policy and can still be migrated. ([7375cd9e](https://github.com/OSSBlade/openblade-core/commit/7375cd9e))
- Make the tray icon follow the active profile when firmware readback is unavailable, instead of showing a generic error. ([49cd0f76](https://github.com/OSSBlade/openblade-core/commit/49cd0f76))
- Update the app icon from the OpenBlade branding repository, including its continuous notch-free display bezel. ([49cd0f76](https://github.com/OSSBlade/openblade-core/commit/49cd0f76))
- Selecting a keyboard effect from Off starts with a visible color and nonzero brightness. Old non-Off settings saved at 0% are migrated, so Breathing can't be accepted and then vanish. ([9bba9c97](https://github.com/OSSBlade/openblade-core/commit/9bba9c97))
- Restart the keyboard renderer once after a brief failure, including while the tray is hidden. Persistent failures and settings changed elsewhere stay stopped and are reported. ([16a14299](https://github.com/OSSBlade/openblade-core/commit/16a14299))
- Use a native color picker with optional advanced RGB fields, and summarize lighting overrides as readable effect details. ([9bba9c97](https://github.com/OSSBlade/openblade-core/commit/9bba9c97))
- Fall back to the active USB-C profile when firmware reports a thermal value that is invalid for USB-C, avoiding blank or contradictory performance and fan selections. ([9bba9c97](https://github.com/OSSBlade/openblade-core/commit/9bba9c97))
- Add a per-user Start minimized option that keeps the app in the notification area on first launch. ([9bba9c97](https://github.com/OSSBlade/openblade-core/commit/9bba9c97))
- Use the same compact dot on status pills for ready and attention states. ([9bba9c97](https://github.com/OSSBlade/openblade-core/commit/9bba9c97))
- Change the external-change pill to "Changed outside OpenBlade" and note that saved profiles are unchanged. ([908ecd30](https://github.com/OSSBlade/openblade-core/commit/908ecd30))
- Show the single label "Unavailable" on Dashboard fields when the service is offline. ([e6d68fa5](https://github.com/OSSBlade/openblade-core/commit/e6d68fa5))
- Call the performance category "Power mode" in setting labels, so Performance isn't repeated when that mode is selected. ([7b4a1923](https://github.com/OSSBlade/openblade-core/commit/7b4a1923))
- Show power, cooling, lighting, and display settings on the Profiles page in an adaptive comparison matrix, with a single-profile view in narrow windows. ([7b4a1923](https://github.com/OSSBlade/openblade-core/commit/7b4a1923))
- Display status names the panel, resolution, refresh mode, color depth, HDR state, and color profile instead of a generic detection message. ([7b4a1923](https://github.com/OSSBlade/openblade-core/commit/7b4a1923))
- Keep the Active badge beside the power source in profile headers, and use AC, USB-C, Battery, and None in troubleshooting. ([121dc552](https://github.com/OSSBlade/openblade-core/commit/121dc552))
- Add an About footer page for app and service versions, supported-device and firmware details, diagnostics, help links, live troubleshooting, and attribution. Settings holds preferences only, and the Dashboard status pill opens About. ([3c3da6d6](https://github.com/OSSBlade/openblade-core/commit/3c3da6d6))
- Reactive lighting no longer pulses for the unlit space bar. ([7b52faa9](https://github.com/OSSBlade/openblade-core/commit/7b52faa9))

- Display profile automation keeps running after Tray Exit. The first confirmed power source is only observed; later AC, USB-C, or battery changes apply the destination profile. ([2e8ef3fa](https://github.com/OSSBlade/openblade-core/commit/2e8ef3fa))
- Display automation catches up on a power-source change missed while the SessionAgent restarted. A brief Windows display failure is retried once. ([53c35c90](https://github.com/OSSBlade/openblade-core/commit/53c35c90))
- Start the SessionAgent at sign-in on its own. The Start-at-sign-in setting controls only the visible tray. ([2e8ef3fa](https://github.com/OSSBlade/openblade-core/commit/2e8ef3fa))
- Replace page-level `ScrollViewer` controls with `ScrollView` for smoother scrolling on high-refresh-rate displays. ([06dbaf3a](https://github.com/OSSBlade/openblade-core/commit/06dbaf3a))
- Fix periodic jank on high-refresh-rate displays by running display polling and readback off the UI thread. Background checks no longer disable controls. ([2178462f](https://github.com/OSSBlade/openblade-core/commit/2178462f))
- Fix the dashboard briefly showing `Controls unavailable` during a brief adapter recheck. Hardware writes stay blocked until power source detection recovers. ([06dbaf3a](https://github.com/OSSBlade/openblade-core/commit/06dbaf3a))
- Refresh-rate choices match what Windows exposes for the internal panel: 60 Hz, 240 Hz, and Dynamic 48-240 Hz. ([2178462f](https://github.com/OSSBlade/openblade-core/commit/2178462f))
- Reactive decay matches Synapse timing: 0.5 seconds Short, 1.5 seconds Medium, and 2.0 seconds Long. ([2178462f](https://github.com/OSSBlade/openblade-core/commit/2178462f))
- Spectrum Cycling follows the measured 37.71-second Synapse RGB curve, including its blue hold, instead of a 40-second HSV wheel. Random Breathing picks independently from the captured 24-color palette and can repeat a color. ([acfbc0aa](https://github.com/OSSBlade/openblade-core/commit/acfbc0aa))
- Named-pipe protocol version 9 reports which side renders host lighting, in addition to the Reactive key-pulse operation from version 8. ([2178462f](https://github.com/OSSBlade/openblade-core/commit/2178462f))
- Performance, fan, lighting, and display choices form one profile per power source. Changes apply right away to the active profile, stay a compact unsaved draft until saved, and are discarded when the power source changes. ([2178462f](https://github.com/OSSBlade/openblade-core/commit/2178462f))
- Preview pending values in the affected Profiles card, mark it with an unsaved dot, and keep Save and Discard in one row. ([8dcae8be](https://github.com/OSSBlade/openblade-core/commit/8dcae8be))
- Remove the hidden manual-change-duration preference. Active-page changes apply to the current power source until saved as a profile. ([30276291](https://github.com/OSSBlade/openblade-core/commit/30276291))
- Fix profile save and discard reporting success after a temporary-override cleanup failure, or leaving a previously unconfigured display profile in the wrong state. ([2178462f](https://github.com/OSSBlade/openblade-core/commit/2178462f))

### Safety

- Unknown power readings and service reconnects block display writes. Saving a profile for the same source, or changing display settings elsewhere, does not reapply settings. ([2e8ef3fa](https://github.com/OSSBlade/openblade-core/commit/2e8ef3fa))
- Display settings apply only on your action or a confirmed power-profile change, then are checked through Windows. Changes made elsewhere are reported, not overwritten. ([2178462f](https://github.com/OSSBlade/openblade-core/commit/2178462f))
- SDR color profiles are unavailable while HDR is on. ([e8cf4a65](https://github.com/OSSBlade/openblade-core/commit/e8cf4a65))
- Color-profile selection activates and verifies the current user's Windows profile before applying the panel's MHC2 calibration. ([e8cf4a65](https://github.com/OSSBlade/openblade-core/commit/e8cf4a65))
- The Display page reports changes made by Windows or other software without overwriting them, and pauses while a picker is open. ([e8cf4a65](https://github.com/OSSBlade/openblade-core/commit/e8cf4a65))
- A brief USB-C adapter detection failure keeps the last confirmed power-source label, and hardware writes stay blocked until detection recovers. ([e8cf4a65](https://github.com/OSSBlade/openblade-core/commit/e8cf4a65))
- Lighting writes are blocked unless the exact model, PID, BIOS, and effect settings match the reviewed evidence. The supported effects passed the exact-build workflow and repeated installed use on the target laptop. ([438aa0ec](https://github.com/OSSBlade/openblade-core/commit/438aa0ec))
- Lighting support is tied to the reviewed lighting code. Unrelated UI, service, and display commits keep it, and a real change to lighting code stays blocked until it is reviewed and approved. ([438aa0ec](https://github.com/OSSBlade/openblade-core/commit/438aa0ec))
- Reactive keyboard observation runs only while Reactive lighting is active. Unsupported and extended keys are ignored. ([2178462f](https://github.com/OSSBlade/openblade-core/commit/2178462f))
- Reactive input and lighting failures are reported without success notifications or per-key messages. A partial lighting rollback is reported as uncertain. ([2178462f](https://github.com/OSSBlade/openblade-core/commit/2178462f))
- Suspend, resume, removal, and reconnect restore lighting once, only when OpenBlade owned the prior effect. Automatic power transitions leave unknown external lighting alone, while a transition from an OpenBlade-owned effect applies the destination profile. ([2178462f](https://github.com/OSSBlade/openblade-core/commit/2178462f))
- OpenBlade detects brightness changes made by another controller. Effect changes can't be read from firmware, so the Razer-app coexistence warning is the only signal. ([2178462f](https://github.com/OSSBlade/openblade-core/commit/2178462f))

## [0.1.0] - 2026-07-17

### Added

- Add a native Windows 11 WinUI 3 tray app backed by a service. ([0646f172](https://github.com/OSSBlade/openblade-core/commit/0646f172))
- Add automatic profiles for the 280 W adapter, 100 W USB-C PD, and battery on the 2026 Razer Blade 16 (RZ09-0581, VID 1532, PID 02E0, BIOS 3.01). ([0646f172](https://github.com/OSSBlade/openblade-core/commit/0646f172))
- Add performance modes for each power source, automatic fan control, and the Maximum fan sequence on full-power AC. Each subsystem reports its own result. ([0646f172](https://github.com/OSSBlade/openblade-core/commit/0646f172))
- Add battery protection with a 50-80% charge-limit slider and a temporary charge-to-100% override that returns to the configured limit after unplugging. ([0646f172](https://github.com/OSSBlade/openblade-core/commit/0646f172))
- Add optional fan-speed and net battery-power readings with a configurable refresh interval. ([0646f172](https://github.com/OSSBlade/openblade-core/commit/0646f172))
- Add a Windows Settings-style interface in light, dark, or system theme, with Mica, tray shortcuts, profile editing, and a customizable hover label. ([0646f172](https://github.com/OSSBlade/openblade-core/commit/0646f172))
- Add troubleshooting and a redacted diagnostic export covering laptop identity, BIOS, EC, MCU, VBIOS, UI and service versions, connection state, capabilities, and command results. ([0646f172](https://github.com/OSSBlade/openblade-core/commit/0646f172))
- Include the source commit hash in product versions for diagnostics. ([0646f172](https://github.com/OSSBlade/openblade-core/commit/0646f172))
- Add install, update, and uninstall for the service and tray app. ([0646f172](https://github.com/OSSBlade/openblade-core/commit/0646f172))

### Changed

- Rename the project to OpenBlade to reflect its Razer Blade-only scope. ([e8cf4a65](https://github.com/OSSBlade/openblade-core/commit/e8cf4a65))
- Other Razer software shows a coexistence warning instead of being stopped or making all controls read-only. ([0646f172](https://github.com/OSSBlade/openblade-core/commit/0646f172))
- Settings changed by another controller are reported, not overwritten. ([0646f172](https://github.com/OSSBlade/openblade-core/commit/0646f172))
- The core repository includes only the app and tray icon binaries needed at runtime. Brand source files stay in the branding repository. ([0646f172](https://github.com/OSSBlade/openblade-core/commit/0646f172))

### Safety

- Hardware writes are blocked unless the laptop, product ID, command, and value are supported. ([0646f172](https://github.com/OSSBlade/openblade-core/commit/0646f172))
- The service handles hardware access one request at a time and offers typed operations only. ([0646f172](https://github.com/OSSBlade/openblade-core/commit/0646f172))
- OpenBlade uses the standard Windows HID interface, with no kernel driver and no Synapse components. ([0646f172](https://github.com/OSSBlade/openblade-core/commit/0646f172))
- Captured protocol data in the repository is sanitized, and raw packet captures are excluded. ([0646f172](https://github.com/OSSBlade/openblade-core/commit/0646f172))

### Known limitations

- Version 0.1.0 supports only the Razer Blade 16 RZ09-0581 / PID 02E0 on BIOS 3.01 and Windows 11 x64. ([0646f172](https://github.com/OSSBlade/openblade-core/commit/0646f172))
- Sensor readings cover only fan speed and net battery power. ([0646f172](https://github.com/OSSBlade/openblade-core/commit/0646f172))
- Reactive captures cover alphanumeric keys, punctuation, modifiers, arrows, F1-F12, Delete, Fn, and the Fn+F12 Print Screen chord. Installed testing confirmed the A key; other positions rely on the captured layout and automated tests. The unlit space bar is ignored, the power button was excluded, and this keyboard has no numpad. ([438aa0ec](https://github.com/OSSBlade/openblade-core/commit/438aa0ec))
- Fan control offers Automatic and Maximum modes, not custom fan curves. ([0646f172](https://github.com/OSSBlade/openblade-core/commit/0646f172))
- Unrecognized external adapters are read-only. ([0646f172](https://github.com/OSSBlade/openblade-core/commit/0646f172))
- Controls that aren't supported on your laptop are unavailable. ([0646f172](https://github.com/OSSBlade/openblade-core/commit/0646f172))

[Unreleased]: https://github.com/OpenBladeProject/openblade-core/compare/v0.77.1...HEAD
[0.77.1]: https://github.com/OpenBladeProject/openblade-core/compare/v0.65.1-nightly.117...v0.77.1
[0.65.1-nightly.117]: https://github.com/OSSBlade/openblade-core/compare/v0.64.2-nightly.116...v0.65.1-nightly.117
[0.64.2-nightly.116]: https://github.com/OSSBlade/openblade-core/compare/v0.63.2-nightly.115...v0.64.2-nightly.116
[0.63.2-nightly.115]: https://github.com/OSSBlade/openblade-core/compare/v0.61.1-nightly.114...v0.63.2-nightly.115
[0.61.1-nightly.114]: https://github.com/OSSBlade/openblade-core/compare/v0.60.0-nightly.113...v0.61.1-nightly.114
[0.60.0-nightly.113]: https://github.com/OSSBlade/openblade-core/compare/v0.58.7-nightly.112...v0.60.0-nightly.113
[0.58.7-nightly.112]: https://github.com/OSSBlade/openblade-core/compare/v0.58.5-nightly.111...v0.58.7-nightly.112
[0.58.5-nightly.111]: https://github.com/OSSBlade/openblade-core/compare/v0.58.4...v0.58.5-nightly.111
[0.58.4]: https://github.com/OSSBlade/openblade-core/compare/v0.58.2-nightly.110...v0.58.4
[0.58.2-nightly.110]: https://github.com/OSSBlade/openblade-core/compare/v0.58.1...v0.58.2-nightly.110
[0.58.1]: https://github.com/OSSBlade/openblade-core/compare/v0.57.1-nightly.109...v0.58.1
[0.57.1-nightly.109]: https://github.com/OSSBlade/openblade-core/compare/v0.51.0-nightly.107-attempt.2...v0.57.1-nightly.109
[0.51.0-nightly.107]: https://github.com/OSSBlade/openblade-core/compare/v0.50.3...v0.51.0-nightly.107-attempt.2
[0.44.1-nightly.105]: https://github.com/OSSBlade/openblade-core/compare/v0.44.0-nightly.104...v0.44.1-nightly.105
[0.44.0-nightly.104]: https://github.com/OSSBlade/openblade-core/compare/v0.43.0-nightly.103...v0.44.0-nightly.104
[0.43.0-nightly.103]: https://github.com/OSSBlade/openblade-core/compare/v0.42.2-nightly.102...v0.43.0-nightly.103
[0.42.2-nightly.102]: https://github.com/OSSBlade/openblade-core/compare/v0.42.1-nightly.101...v0.42.2-nightly.102
[0.42.1-nightly.101]: https://github.com/OSSBlade/openblade-core/compare/v0.42.0-nightly.100...v0.42.1-nightly.101
[0.42.0-nightly.100]: https://github.com/OSSBlade/openblade-core/compare/v0.41.1-nightly.99...v0.42.0-nightly.100
[0.41.1-nightly.99]: https://github.com/OSSBlade/openblade-core/compare/v0.41.0-nightly.98...v0.41.1-nightly.99
[0.41.0-nightly.98]: https://github.com/OSSBlade/openblade-core/compare/v0.40.0...v0.41.0-nightly.98
[0.40.0]: https://github.com/OpenBladeProject/openblade-core/compare/v0.30.0...v0.40.0
[0.39.0-nightly.97]: https://github.com/OSSBlade/openblade-core/compare/v0.37.3-nightly.96...v0.39.0-nightly.97
[0.37.3-nightly.96]: https://github.com/OSSBlade/openblade-core/compare/v0.37.1-nightly.95...v0.37.3-nightly.96
[0.37.1-nightly.95]: https://github.com/OSSBlade/openblade-core/compare/v0.37.0-nightly.94...v0.37.1-nightly.95
[0.37.0-nightly.94]: https://github.com/OSSBlade/openblade-core/compare/v0.36.0-nightly.93...v0.37.0-nightly.94
[0.36.0-nightly.93]: https://github.com/OSSBlade/openblade-core/compare/v0.33.1-nightly.92...v0.36.0-nightly.93
[0.33.1-nightly.92]: https://github.com/OSSBlade/openblade-core/compare/v0.30.0...v0.33.1-nightly.92
[0.30.0]: https://github.com/OpenBladeProject/openblade-core/compare/v0.29.0-nightly.91...v0.30.0
[0.29.0-nightly.91]: https://github.com/OSSBlade/openblade-core/compare/v0.28.1-nightly.90...v0.29.0-nightly.91
[0.28.1-nightly.90]: https://github.com/OSSBlade/openblade-core/compare/v0.27.5-nightly.89...v0.28.1-nightly.90
[0.27.5-nightly.89]: https://github.com/OSSBlade/openblade-core/compare/v0.26.2-nightly.87...v0.27.5-nightly.89
[0.26.2-nightly.87]: https://github.com/OSSBlade/openblade-core/compare/v0.25.2-nightly.84-attempt.2...v0.26.2-nightly.87
[0.25.2-nightly.84]: https://github.com/OSSBlade/openblade-core/compare/v0.25.1-nightly.83...v0.25.2-nightly.84-attempt.2
[0.25.1-nightly.83]: https://github.com/OSSBlade/openblade-core/compare/v0.25.0...v0.25.1-nightly.83
[0.25.0]: https://github.com/OpenBladeProject/openblade-core/compare/v0.24.5...v0.25.0
[0.24.5]: https://github.com/OpenBladeProject/openblade-core/compare/v0.24.3...v0.24.5
[0.24.3]: https://github.com/OSSBlade/openblade-core/compare/v0.22.0...v0.24.3
[0.22.0]: https://github.com/OSSBlade/openblade-core/compare/v0.21.10...v0.22.0
[0.21.10]: https://github.com/OSSBlade/openblade-core/compare/v0.21.9...v0.21.10
[0.21.9]: https://github.com/OSSBlade/openblade-core/compare/v0.21.6...v0.21.9
[0.21.6]: https://github.com/OSSBlade/openblade-core/compare/v0.21.0...v0.21.6
[0.21.0]: https://github.com/OSSBlade/openblade-core/compare/v0.20.23...v0.21.0
[0.20.23]: https://github.com/OSSBlade/openblade-core/compare/v0.20.18...v0.20.23
[0.20.18]: https://github.com/OSSBlade/openblade-core/compare/v0.20.12-nightly.49...v0.20.18
[0.20.12-nightly.49]: https://github.com/OSSBlade/openblade-core/compare/v0.20.11-nightly.47...v0.20.12-nightly.49
[0.20.11-nightly.47]: https://github.com/OSSBlade/openblade-core/compare/v0.20.8-nightly.44...v0.20.11-nightly.47
[0.20.8-nightly.44]: https://github.com/OSSBlade/openblade-core/compare/v0.20.7-nightly.41...v0.20.8-nightly.44
[0.20.7-nightly.41]: https://github.com/OSSBlade/openblade-core/compare/v0.20.5-nightly.40...v0.20.7-nightly.41
[0.20.5-nightly.40]: https://github.com/OSSBlade/openblade-core/compare/v0.19.37-nightly.39...v0.20.5-nightly.40
[0.19.37-nightly.39]: https://github.com/OSSBlade/openblade-core/compare/v0.19.15-nightly.34...v0.19.37-nightly.39
[0.19.15-nightly.34]: https://github.com/OSSBlade/openblade-core/compare/v0.8.4-nightly.20...v0.19.15-nightly.34
[0.8.4-nightly.20]: https://github.com/OSSBlade/openblade-core/compare/v0.5.1-nightly.16...v0.8.4-nightly.20
[0.5.1-nightly.16]: https://github.com/OSSBlade/openblade-core/compare/v0.1.0-nightly.11...v0.5.1-nightly.16
[0.1.0-nightly.11]: https://github.com/OSSBlade/openblade-core/compare/v0.1.0-nightly.10...v0.1.0-nightly.11
[0.1.0-nightly.10]: https://github.com/OSSBlade/openblade-core/compare/v0.1.0-nightly.9...v0.1.0-nightly.10
[0.1.0-nightly.9]: https://github.com/OSSBlade/openblade-core/compare/v0.1.0-nightly.8...v0.1.0-nightly.9
[0.1.0-nightly.8]: https://github.com/OSSBlade/openblade-core/compare/v0.1.0-nightly.6...v0.1.0-nightly.8
[0.1.0-nightly.6]: https://github.com/OSSBlade/openblade-core/compare/v0.1.0-nightly.5...v0.1.0-nightly.6
[0.1.0-nightly.5]: https://github.com/OSSBlade/openblade-core/compare/v0.1.0...v0.1.0-nightly.5
[0.1.0]: https://github.com/OSSBlade/openblade-core/releases/tag/v0.1.0

[0.50.3]: https://github.com/OSSBlade/openblade-core/compare/v0.44.1-nightly.105...v0.50.3
