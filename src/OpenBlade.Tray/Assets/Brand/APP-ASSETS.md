# Application artwork

The selected Monolith artwork is generated in
[openblade-branding](https://github.com/OSSBlade/openblade-branding) by
`tools/monolith_geometry.py` and `tools/generate_brand_assets.py`.

The app icon has a 16:10 display, a deeper silver-gray keyboard deck, inset connected hinge
and vertically centered green curves. A single curve is used below 48 pixels;
keyboard detail simplifies below 64 pixels. Optical enlargement through 64
pixels preserves the display proportions. The deck has 30% more visible depth
without increasing the front lip thickness. All frames retain outer transparency.

Copy these generated assets together:

| Branding export | Application destination |
| --- | --- |
| `assets/brand/ico/OpenBlade.ico` | `OpenBlade.ico` beside this guide |
| `assets/brand/ico/OpenBlade.Installer.ico` | `OpenBlade.Installer.ico` beside this guide |
| `assets/brand/png/app/openblade-app-1024.png` | `OpenBlade.png` beside this guide |
| `assets/brand/svg/openblade-mark-dark.svg` | `OpenBlade.Mark.Dark.svg` beside this guide |
| `assets/brand/svg/openblade-mark-light.svg` | `OpenBlade.Mark.Light.svg` beside this guide |
| `assets/brand/windows/{StoreLogo,Square44x44Logo,Square150x150Logo}.png` | Matching names under `build/identity/Assets` |

The dimensional PNG is shared by README headers, the About image, installer
header, and default notification logo. README headers use this same app icon
on light and dark backgrounds. The flat marks remain available as separate
branding assets.
The installer retains its download badge and established adaptive tile at 48
pixels and below. Tray-state and performance-notification artwork are separate
and unchanged by this update.

`app-assets.json` records SHA-256 values for the copied exports. Update it only
after regenerating and visually inspecting the branding exports, then run
`ApplicationIconAssetTests` and `tests/Build.Tests/InstallerAuthoring.Tests.ps1`.
