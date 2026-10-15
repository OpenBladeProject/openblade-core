<div align="center">
  <img src="src/OpenBlade.Tray/Assets/Brand/OpenBlade.png" width="128" alt="OpenBlade">
  <h1>OpenBlade</h1>
  <p><strong>无需 Razer Synapse，即可控制你的 Razer Blade。</strong></p>
  <p>
    <a href="https://github.com/OSSBlade/openblade-core/releases">下载</a>
    ·
    <a href="https://openblade.sh/zh-CN/">使用指南</a>
    ·
    <a href="CHANGELOG.md">更新内容</a>
  </p>
</div>

[English](README.md) | 简体中文 | [Русский](README.ru-RU.md)

OpenBlade 是一款适用于 Razer Blade 笔记本的免费 Windows 托盘应用。
无需 Synapse 账户，即可调整性能、风扇、充电、灯光、显示设置和特殊按键。
配置可随交流电、USB-C 和电池供电自动切换，关闭窗口后仍会继续运行。

| 仪表盘 | 灯光 |
| :---: | :---: |
| <img src="docs/images/openblade-dashboard.png" alt="OpenBlade 仪表盘" width="410"> | <img src="docs/images/openblade-lighting.png" alt="OpenBlade 灯光设置" width="410"> |

<a id="supported-laptops"></a>

## 支持的笔记本

OpenBlade 需要 Windows 11 x64，版本 24H2 或更高。

| 笔记本 | 型号 |
| --- | --- |
| Razer Blade 16 (2026) | `RZ09-0581` |
| Razer Blade 16 (2025) | `RZ09-0528` |
| Razer Blade 14 (2025)，实验性支持 | `RZ09-0530` |
| Razer Blade 16 (2023)，实验性支持 | `RZ09-0483` |
| Razer Blade 14 (2023)，实验性支持 | `RZ09-0482` |
| Razer Blade 18 (2023)，实验性支持 | `RZ09-0484` |
| Razer Blade 15 Base (Early 2020)，实验性支持 | `RZ09-0328` |

可用功能因型号而异。OpenBlade 会自动启用实验性型号上的可用功能。
兼容性请参阅[使用指南](https://openblade.sh/zh-CN/)，
各型号功能和测试方法请参阅[实验性支持](docs/zh-CN/experimental-blade-support.md)。

如果你的 Razer 笔记本尚未列出，请在 OpenBlade 中选择**协助添加支持**。
保存设备身份报告，然后按照
[Synapse Observer 指南](https://github.com/OpenBladeProject/synapse-observer#send-evidence)
收集并提交该型号的证据。

<a id="features"></a>

## 功能

- 电源模式、自动或固定风扇转速、自定义风扇曲线，以及 NVIDIA GPU 频率偏移。
- 在全功率交流供电下使用 HyperBoost，可搭配 Razer 散热底座，也可独立使用。
  可从任何受支持的交流电模式启动，关闭后恢复启动前的设置。
- 充电上限、电池健康信息，以及针对各供电来源的独立设置。
- 键盘和顶盖标志灯效、响应式灯光、静音指示，
  并支持 Razer `.chroma` 文件和共享的 `.oblight` 预设。
- 显示配置、亮度控制、功能键行行为，以及 Fn 层的 M1 至 M5 快捷键。
- 自由排列实时传感器读数，并找出让 NVIDIA GPU 保持唤醒的应用。

每个 Windows 用户都有自己的配置和偏好设置，并可导入、导出配置。
界面支持英语、简体中文和俄语。可选择是否检查更新，支持 Stable 和 Nightly 渠道。

<a id="install"></a>

## 安装

1. 从[最新版本](https://github.com/OSSBlade/openblade-core/releases/latest)
   下载 `OpenBlade-Setup.exe`。
2. 双击运行并选择**安装**。正常启动即可；安装程序会在需要时请求管理员权限。
   如果缺少 .NET 10 Desktop Runtime，安装程序会提供下载和安装选项。
3. 选择**启动 OpenBlade**，打开托盘应用。

在 Windows 设置 > 已安装的应用中卸载。可保留已保存的配置，也可选择删除。
Nightly 测试版本可能未签名，并触发 Windows 警告。

<a id="using-it-with-synapse"></a>

## 与 Synapse 一起使用

OpenBlade 可独立使用。如果你想继续用 Synapse 控制其他 Razer 设备，
请查阅 [Synapse Blade Blocker](https://github.com/OpenBladeProject/synapse-blade-blocker)
的当前型号覆盖范围和补丁说明。
OpenBlade 会先检查补丁，再允许共同使用；详见
[设置与共存说明](docs/zh-CN/synapse-blade-blocker.md)。

核心控制功能无需额外驱动。Blade 16（2025 和 2026）的原生 Windows 亮度浮窗
使用该型号已安装的 Razer 驱动。没有该驱动时，亮度键会使用 OpenBlade 浮窗。
详见[亮度驱动指南](docs/zh-CN/razer-brightness-driver-package.md)。

<a id="development"></a>

## 开发

架构、构建、测试和硬件工具请从[开发者指南](docs/developer-guide.md)开始。
贡献规则见 [AGENTS.md](AGENTS.md) 和 [VERSIONING.md](VERSIONING.md)。

相关仓库：[用户文档](https://github.com/OSSBlade/openblade-docs)、
[协议采集](https://github.com/OSSBlade/openblade-captures)和
[品牌资源](https://github.com/OSSBlade/openblade-branding)。

<a id="license"></a>

## 许可证

[GNU 通用公共许可证第 3 版或更新版本](LICENSE)（`GPL-3.0-or-later`）。
