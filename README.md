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

OpenBlade is a small, free app that lives in your Windows system tray and gives
you the controls Synapse offers: performance modes, fan behavior, battery charge
limits, keyboard lighting, display settings, and the special keys on your
laptop. There is no account to create, nothing is sent to the cloud, and it
only checks for updates if you turn that on.

| Dashboard | Lighting |
| :---: | :---: |
| <img src="docs/images/openblade-dashboard.png" alt="OpenBlade dashboard" width="410"> | <img src="docs/images/openblade-lighting.png" alt="OpenBlade lighting controls" width="410"> |

## Supported laptops

| Laptop | Model number |
| --- | --- |
| Razer Blade 16 (2026) | `RZ09-0581` |
| Razer Blade 16 (2025) | `RZ09-0528` |

OpenBlade only works with these models. A control appears for your laptop only
after it has been tested on that exact model, so you will not find buttons that
do nothing or do the wrong thing.

## Why people use it

You do not need to install or sign in to Razer Synapse. If Razer software is
already running, OpenBlade warns you that the two may conflict and offers to
stop it for you.

OpenBlade talks to the laptop over the same USB channel Razer uses. Performance
modes, fans, sensors, lighting, and battery settings need no driver of their
own, so that is one less thing that can crash your system or stop working
after a Windows update.

The one exception is the native Windows brightness flyout on the Blade 16
(2026). It relies on the signed brightness driver stack that Razer ships for
that laptop. OpenBlade does not write, modify, or re-sign that driver, and the
OpenBlade installer does not install it. OpenBlade exports the exact package
from a laptop that already has it and keeps it in
[`blade-driver-catalog`](https://github.com/OSSBlade/blade-driver-catalog),
which is private for now. If the driver is not present, the brightness keys
still work through Windows and the OpenBlade flyout.

You set up a profile for plugged in, for USB-C power, and for battery, and
OpenBlade switches between them as soon as you plug in or unplug. Closing the
window changes nothing: profiles, display settings, lighting, and your special
keys keep working in the background.

OpenBlade is careful with your hardware. If it cannot confirm which laptop it
is talking to, or cannot confirm that a change took effect, it stops rather
than guessing. It only accepts changes from the person signed in at the laptop
itself. A Remote Desktop session can see the current state but cannot change
anything.

OpenBlade is free software under the GPL, so you can read every line of code
that touches your laptop.

## What you can control

### Performance and cooling

- Power modes, including HyperBoost on the Blade 16 (2026) without the Razer
  cooling pad that Synapse requires
- Fan behavior: automatic, fixed speed, or your own curve
- NVIDIA GPU clock offsets for extra headroom or quieter running
- Live readouts for CPU temperature, fan RPM, GPU temperature and load, and
  battery power draw
- If another app changes your performance or fan settings behind your back,
  OpenBlade can put them back or just let you know
- A list of apps holding the NVIDIA GPU awake on an idle desktop, with a way
  to close them

### Battery

- Charge limits to keep the battery healthy when the laptop stays plugged in,
  with a one-key toggle
- Battery health and power figures on the dashboard
- Separate settings per power source, applied automatically

### Lighting

- Keyboard and lid-logo lighting with the built-in effects, plus streaming
  effects that react in real time
- The keyboard shows when your microphone or speakers are muted
- Import Razer `.chroma` keyboard files, or share your own `.oblight` presets
  with other OpenBlade users

### Display and keys

- Display profiles and brightness behavior, including the native Windows
  brightness flyout when the Razer brightness driver is installed
- Choose whether the function row acts as F-keys or media keys by default
- Bind the M1 to M5 keys on the Fn layer to the actions you want, including
  Gaming mode, mute, and charge-limit toggles

### Everyday details

- A dashboard you can rearrange. Show the sections you care about and drag
  them into the order you want
- Each Windows user on the laptop gets their own profiles and preferences.
  You can switch users without restarting anything, and only the user who is
  signed in on screen can change hardware settings
- Export and import your profiles to move them to another machine
- Optional update checks on a schedule you pick, with a Nightly channel if you
  want to try new features early
- A diagnostics package you can share when reporting a problem. Your user
  name, computer name, and personal folder paths are stripped out before it is
  written, and the laptop serial number is never included
- Available in English, Simplified Chinese, and Russian, or set it to follow
  your Windows language

## Install

1. Download `OpenBlade-Setup.exe` from the
   [latest release](https://github.com/OSSBlade/openblade-core/releases/latest).
2. Double-click it and choose **Install**. Windows will ask for permission
   once, when setup is ready to make changes. Do not use "Run as
   administrator"; the installer asks for what it needs on its own.
3. Choose **Launch OpenBlade**. The app appears in your system tray.

To remove it, use Windows Settings > Installed apps. Your saved profiles stay
on disk unless you tick the option to remove them too.

Nightly builds are unsigned test builds, so Windows may show a warning before
installing one. They are only offered inside the app when you switch the update
channel to **Nightly**.

## Good to know

- OpenBlade needs Windows 11 x64, version 24H2 or later.
- OpenBlade does not fight Razer software for control of the hardware. If
  both are running, you may see settings change unexpectedly. Stop one of them.
- Battery and firmware settings apply to the physical laptop, no matter which
  Windows user changed them.
- The full user guide and compatibility notes live in the
  [openblade-docs](https://github.com/OSSBlade/openblade-docs) repository.

## For developers

Everything technical lives in the [developer guide](docs/developer-guide.md):
architecture, the hardware safety model, build and test commands, publishing,
the capture and validation tools, and the lighting file format. Read
[AGENTS.md](AGENTS.md) before changing hardware, IPC, setup, or process
boundaries. Commit messages follow Conventional Commits, and version rules are
in [VERSIONING.md](VERSIONING.md).

## License

OpenBlade is free software licensed under the
[GNU General Public License version 3 or later](LICENSE)
(`GPL-3.0-or-later`).

## Project repositories

- [openblade-docs](https://github.com/OSSBlade/openblade-docs): user
  documentation and compatibility notes
- [openblade-captures](https://github.com/OSSBlade/openblade-captures):
  sanitized protocol evidence and capture tooling
- [openblade-branding](https://github.com/OSSBlade/openblade-branding): brand
  source files and exports
