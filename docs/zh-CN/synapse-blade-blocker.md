<a id="synapse-blade-blocker-coexistence"></a>

# 通过 Synapse Blade Blocker 同时使用 Synapse

[English](../synapse-blade-blocker.md) | 简体中文 | [Русский](../ru-RU/synapse-blade-blocker.md)

[Synapse Blade Blocker](https://github.com/OpenBladeProject/synapse-blade-blocker)
是一个需要单独安装的可选补丁。它能让 Synapse 继续管理外设，同时从设备列表中排除已识别的 Blade 笔记本。
支持的型号和安装说明以该仓库为准。OpenBlade 不会自动安装这个补丁，也不依赖它运行。

<a id="setup"></a>

## 设置

1. 按照 Blocker 仓库的说明安装并应用补丁。
2. 重启 Synapse，使其加载修改后的文件。
3. 打开 OpenBlade。如果提示冲突，请检查补丁是否仍然生效，
   并确认没有其他 Razer 控制程序正在运行。

某些更新不会更换已修改的 AppEngine 文件，因此补丁可能继续生效。
如果 Synapse 替换了这些文件，请重新应用补丁。
如果补丁记录与当前安装的文件不符，OpenBlade 就不会将其视为有效补丁。

<a id="how-openblade-checks-coexistence"></a>

## OpenBlade 如何判断两者能否同时运行

OpenBlade 会检查所有正在运行的 Synapse AppEngine 实例。只有至少有一个实例正在运行，
且每个实例所在的安装目录都已确认补丁生效，才不会再将这些进程视为竞争控制程序。
满足这些条件后，两个已知的 Synapse 服务别名也不会阻止 HyperBoost 使用散热控制。
它们仍会出现在诊断信息中，并继续接受其他控制程序检查。
如果无法确认补丁有效，这两个服务仍会阻止使用 HyperBoost。

正在运行、且内部服务名恰好为 `RGS` 的 Windows 服务仍会显示在诊断中，
但不会阻止 HyperBoost 使用散热控制。如果只是进程名为 `RGS` 或
`Razer Gaming Services Updater`，或者服务只是采用了其中一个显示名称，
仍会被视为冲突。其他独立的 Razer 控制程序也仍需通过原有检查。
硬件身份、命令支持、供电要求以及其他功能的使用条件照常检查。

Synapse 也可能在界面加载后启动 `razerwdl` Windows 灯光辅助程序。
只有安装路径和产品信息符合预期、直接父进程是同一会话中已确认补丁有效的
AppEngine，且启动时间能印证这一父子关系时，OpenBlade 才会在查找冲突程序时排除它。
每个正在运行的辅助程序都必须通过这些检查。OpenBlade 每次检查时还会枚举
当前已连接并启用的 Windows LampArray 接口。只要有端点匹配有效补丁记录中的
Blade 产品 ID、无法确认端点身份，或枚举失败，这个辅助程序就仍会被视为冲突。
独立启动的辅助程序仍会被视为冲突，即使同时有已打补丁的 AppEngine 正在运行。
这里没有辅助程序版本白名单。对于已检测到的辅助程序，如果其 Razer 路径和产品信息都已核实，
就不会阻止 HyperBoost 使用散热控制；无法核实身份时则会阻止。
仍被判定为冲突的辅助程序，也会继续触发其他控制程序检查。

端点检查通过 SetupAPI 读取接口元数据，不会打开 HID 设备句柄。
它匹配 HID 接口类，以及
[Windows LampArray 选择器](https://learn.microsoft.com/en-us/uwp/api/windows.devices.lights.lamparray.getdeviceselector)
使用的用法页 `0x59` 和用法 ID `1`。
每次检查都会重新读取结果，不使用缓存，因此之后接入的设备也会按现有策略重新检查。
这项检查只根据当前快照判断是否存在访问冲突，不会阻止辅助程序发现后来接入的设备，也不会修改其本机代码。

OpenBlade 会核对补丁应用记录，以及已安装归档和可执行文件的哈希，而不是按 Synapse 版本白名单判断。
只要补丁工具能完成所需修改并写入有效记录，新版 Synapse 就有可能直接使用，无需等待 OpenBlade 更新。
如果记录缺失或格式错误、文件已被修改、已打补丁和未打补丁的实例同时运行，
或者进程在应用补丁前就已启动，OpenBlade 都会照常将其判定为冲突。
应用补丁后请重启 Synapse。启动时，短暂运行的原版启动器仍可能触发冲突提示。

请在 Synapse Blade Blocker 仓库报告 Blocker 兼容性问题。

Blocker 只改变 Synapse 管理的设备范围，不会把 Synapse 隔离在沙箱中。
以下测试仅涉及 Pro Click V2 和所列构建版本。

<a id="local-validation"></a>

## 本地验证

2026-09-19，在核心版本 466966e84c2114444684d53e2dc309ad1c7f3fc2 上，
对正式代码中的控制程序冲突检查进行只读测试，识别出了四个正在运行、已打补丁的 AppEngine 4.0.823 进程，
未返回冲突或检测错误。Blocker 版本 1261363e1c53a963f23b06d9cef554d22d5932d5
在 PowerShell 7.6.5 下成功应用并恢复补丁。
用户确认 Synapse 中能看到 Pro Click V2、看不到 Blade，且鼠标工作正常。
之后已恢复原始归档和常规安装的 OpenBlade 环境。

测试没有替换已安装的 OpenBlade 程序文件，因此只验证了冲突检查能否识别正在运行的补丁。
它没有验证新构建安装后的界面、全部外设功能，也没有证明硬件能被完全隔离。

随后安装核心版本 `7ce1b87c` 和 Blocker 版本 `50375ea6` 进行测试时，
Synapse 加载完成后又出现了延迟启动的 `razerwdl` 冲突。
已恢复原始归档并重启 OpenBlade。
判断功能是否就绪时，应以这次失败结果为准，不能再仅依据最初的快照。

后续调查确认，该辅助程序会枚举 LampArray 并执行灯光操作；
动态查找的 API 则用于文件系统、运行时支持和 Windows Runtime 激活。
未发现独立的 HID、WinUSB 或 Razer 硬件传输。
这些结果只说明了本次观察到的程序结构，不能保证 Razer 以后的版本也采用相同方式。
本机端点检查发现了 50 个已连接并启用的 HID 接口，读取属性时没有报错，
其中包括两个 Pro Click V2 LampArray 接口，未发现 Blade LampArray 接口；
Windows Runtime 查询独立返回了相同的两个鼠标接口。
最终安装验证必须等到 Synapse 界面和延迟启动的辅助程序都已运行后再进行。
