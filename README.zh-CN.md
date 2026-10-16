<div align="center">
  <img src="src/OpenBlade.Tray/Assets/Brand/OpenBlade.png" width="128" alt="OpenBlade">
  <h1>OpenBlade</h1>
  <p><strong>不用 Razer Synapse，也能管理你的 Razer Blade。</strong></p>
  <p>
    <a href="https://github.com/OSSBlade/openblade-core/releases">下载</a>
    ·
    <a href="https://openblade.sh/zh-CN/">使用指南</a>
    ·
    <a href="CHANGELOG.md">更新内容</a>
  </p>
</div>

[English](README.md) | 简体中文 | [Русский](README.ru-RU.md)

OpenBlade 是一款免费的 Razer Blade 笔记本管理工具，在 Windows 系统托盘中运行。
不用注册 Synapse 账户，就能调整性能、风扇、充电、灯光、显示和特殊按键设置。
配置会随供电状态自动切换，分别适用于交流电、低功率适配器和电池供电。
关闭窗口后，配置仍会自动切换。

| 仪表盘 | 灯光 |
| :---: | :---: |
| <img src="docs/images/openblade-dashboard.png" alt="OpenBlade 仪表盘" width="410"> | <img src="docs/images/openblade-lighting.png" alt="OpenBlade 灯光设置" width="410"> |

<a id="supported-laptops"></a>

## 支持的笔记本

需要 x64（64 位）Windows 11，版本为 24H2 或更新版本。

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
兼容性信息见[使用指南](https://openblade.sh/zh-CN/)，
各型号的具体功能和测试方法见[实验性支持](docs/zh-CN/experimental-blade-support.md)。

如果你的 Razer 笔记本尚未列出，请在 OpenBlade 中选择**协助添加支持**。
保存设备身份报告，然后按照
[Synapse Observer 指南](https://github.com/OpenBladeProject/synapse-observer#send-evidence)
收集并提交该型号的测试资料。

<a id="features"></a>

## 功能

- 电源模式、自动或固定风扇转速、自定义风扇曲线，以及 NVIDIA GPU 频率偏移。
- 使用全功率电源适配器供电时，可开启 HyperBoost，支持搭配 Razer 散热底座或单独使用。
- 充电上限、电池健康信息，以及按供电方式分别保存设置。
- 键盘和顶盖标志灯效、响应灯效、静音指示，
  并支持 Razer `.chroma` 文件和可分享的 `.oblight` 预设。
- 显示配置、亮度调节、功能键行的行为设置，以及通过 Fn 使用的 M1 至 M5 快捷键。
- 自由排列实时传感器读数，找出让 NVIDIA GPU 无法休眠的应用。

每个 Windows 用户都可以保存自己的配置和偏好设置，也可以导入、导出配置。
界面支持英语、简体中文和俄语。可自行决定是否检查更新，并选择 Stable 或 Nightly 更新渠道。

<a id="install"></a>

## 安装

1. 从[最新版本](https://github.com/OSSBlade/openblade-core/releases/latest)
   下载 `OpenBlade-Setup.exe`。
2. 双击运行，选择**安装**。按正常方式启动即可，安装程序会在需要时请求管理员权限。
   如果缺少 .NET 10 Desktop Runtime，安装程序会提供下载和安装选项。
3. 选择**启动 OpenBlade**，打开托盘应用。

卸载时，打开 Windows 设置 > 已安装的应用。你可以保留已保存的配置，也可以一并删除。
Nightly 测试版本可能未签名，并触发 Windows 警告。

<a id="using-it-with-synapse"></a>

## 与 Synapse 一起使用

OpenBlade 可独立使用。如果你想继续用 Synapse 控制其他 Razer 设备，
请查阅 [Synapse Blade Blocker](https://github.com/OpenBladeProject/synapse-blade-blocker)
仓库中的支持型号和补丁说明。
OpenBlade 检查并确认补丁有效后，才允许两者同时运行。详见
[设置与共存说明](docs/zh-CN/synapse-blade-blocker.md)。

核心控制功能无需额外驱动。Blade 16（2025 和 2026）的原生 Windows 亮度浮窗
使用该型号已安装的 Razer 驱动。没有该驱动时，亮度键会使用 OpenBlade 浮窗。
详见[亮度驱动指南](docs/zh-CN/razer-brightness-driver-package.md)。

<a id="development"></a>

## 开发

架构、构建、测试和硬件工具的说明见[开发者指南](docs/developer-guide.md)。
贡献规则见 [AGENTS.md](AGENTS.md) 和 [VERSIONING.md](VERSIONING.md)。

相关仓库：[用户文档](https://github.com/OSSBlade/openblade-docs)、
[协议采集](https://github.com/OSSBlade/openblade-captures)和
[品牌资源](https://github.com/OSSBlade/openblade-branding)。

<a id="license"></a>

## 许可证

[GNU 通用公共许可证第 3 版或更新版本](LICENSE)（`GPL-3.0-or-later`）。
