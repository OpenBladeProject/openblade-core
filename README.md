<div align="center">
  <img src="src/OpenBlade.Tray/Assets/Brand/OpenBlade.png" width="128" alt="OpenBlade">
  <h1>OpenBlade</h1>
  <p><strong>Control your Razer Blade without Razer Synapse.</strong></p>
  <p>
    <a href="https://github.com/OSSBlade/openblade-core/releases">Download</a>
    ·
    <a href="https://github.com/OSSBlade/openblade-docs">User guide</a>
    ·
    <a href="CHANGELOG.md">What's new</a>
  </p>
</div>

English | [简体中文](README.zh-CN.md) | [Русский](README.ru-RU.md)

OpenBlade is a free Windows tray app for Razer Blade laptops. Adjust performance,
fans, charging, lighting, display settings, and special keys without a Synapse
account. Profiles switch automatically between plugged-in, low-power adapter, and battery
power, and keep working when you close the window.

| Dashboard | Lighting |
| :---: | :---: |
| <img src="docs/images/openblade-dashboard.png" alt="OpenBlade dashboard" width="410"> | <img src="docs/images/openblade-lighting.png" alt="OpenBlade lighting controls" width="410"> |

## Supported laptops

OpenBlade requires Windows 11 x64, version 24H2 or later.

| Laptop | Model number |
| --- | --- |
| Razer Blade 16 (2026) | `RZ09-0581` |
| Razer Blade 16 (2025) | `RZ09-0528` |
| Razer Blade 14 (2025), experimental | `RZ09-0530` |
| Razer Blade 16 (2023), experimental | `RZ09-0483` |
| Razer Blade 14 (2023), experimental | `RZ09-0482` |
| Razer Blade 18 (2023), experimental | `RZ09-0484` |
| Razer Blade 15 Base (Early 2020), experimental | `RZ09-0328` |

Available controls vary by model. OpenBlade automatically enables
available controls on experimental models. See the [user guide](https://github.com/OSSBlade/openblade-docs)
for compatibility and [Experimental support](docs/experimental-blade-support.md)
for model features and testing instructions.

On an unlisted Razer laptop, select **Help add support** in OpenBlade. Save the
identity report, then follow the
[Synapse Observer guide](https://github.com/OpenBladeProject/synapse-observer#send-evidence)
to collect and submit evidence for your model.

## Features

- Power modes, automatic or fixed fan speeds, custom fan curves, and NVIDIA GPU
  clock offsets.
- HyperBoost on full-power AC, with or without the Razer cooling pad.
- Charge limits, battery health, and separate settings for each
  power source.
- Keyboard and lid-logo effects, reactive lighting, mute indicators,
  and support for Razer `.chroma` files and shared `.oblight` presets.
- Display profiles, brightness controls, function-row
  behavior, and M1 to M5 shortcuts on the Fn layer.
- Rearrange live sensor readouts and find apps keeping the NVIDIA
  GPU awake.

Each Windows user has their own profiles and preferences, with profile import
and export. The interface supports English, Simplified Chinese, and Russian.
Update checks are optional, with Stable and Nightly channels.

## Install

1. Download `OpenBlade-Setup.exe` from the
   [latest release](https://github.com/OSSBlade/openblade-core/releases/latest).
2. Double-click it and choose **Install**. Start it normally; setup asks for
   administrator permission when needed.
   If .NET 10 Desktop Runtime is missing, setup offers to download and install it.
3. Choose **Launch OpenBlade** to open the tray app.

Uninstall through Windows Settings > Installed apps. You can keep your saved
profiles or choose to remove them. Nightly test builds may be unsigned and
trigger a Windows warning.

## Using it with Synapse

OpenBlade works on its own. To keep Synapse for other Razer devices, see
[Synapse Blade Blocker](https://github.com/OpenBladeProject/synapse-blade-blocker)
for current model coverage and patch instructions.
OpenBlade checks the patch before allowing shared use; see the
[setup and coexistence notes](docs/synapse-blade-blocker.md).

Core controls work without an additional driver. The native Windows brightness
flyout on the Blade 16 (2025 and 2026) uses the model's own Razer driver when
already installed. Without it, brightness keys use the OpenBlade flyout. See
the [brightness driver guide](docs/razer-brightness-driver-package.md).

## Development

Start with the [developer guide](docs/developer-guide.md) for architecture,
builds, tests, and hardware tooling. Contribution rules are in
[AGENTS.md](AGENTS.md) and [VERSIONING.md](VERSIONING.md).

Related repositories: [user documentation](https://github.com/OSSBlade/openblade-docs),
[Synapse Observer](https://github.com/OpenBladeProject/synapse-observer), and
[branding](https://github.com/OSSBlade/openblade-branding).

## License

[GNU General Public License version 3 or later](LICENSE) (`GPL-3.0-or-later`).
