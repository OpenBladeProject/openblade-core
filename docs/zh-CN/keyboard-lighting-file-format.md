<a id="openblade-keyboard-lighting-files"></a>

# OpenBlade 键盘灯光文件

[English](../keyboard-lighting-file-format.md) | 简体中文 | [Русский](../ru-RU/keyboard-lighting-file-format.md)

OpenBlade 键盘灯光文件是扩展名为 `.oblight` 的 UTF-8 JSON 文档。
版本 1 用于保存可在设备间分享的键盘灯效预设。导入文件不会让硬件增加新的功能：
OpenBlade 必须已经允许当前 Blade 型号及其固件使用该灯效。

键盘灯光文件导入属于实验性功能。在 OpenBlade 设置中启用**实验功能**，
灯光页面就会显示导入入口。之后关闭实验功能，只会隐藏这个入口，
已经应用或保存在电源配置中的灯效仍然有效。只能导入当前型号支持的灯效。
[实验性型号的控制功能](experimental-blade-support.md)会自动启用，不受“实验功能”开关影响。

媒体类型为 `application/vnd.openblade.keyboard-lighting+json`。

此格式仅包含数据，不能包含脚本、插件、外部资源、文件路径、USB 身份、
HID 报告、固件命令或设备映射。
OpenBlade 会拒绝未知字段、重复字段、无效枚举值、不支持的格式版本，
以及超过 64 KiB 的文件。

<a id="example"></a>

## 示例

```json
{
  "format": "openblade-keyboard-lighting",
  "schemaVersion": 1,
  "metadata": {
    "name": "Northern lights",
    "author": "Example",
    "description": "A two-color breathing preset.",
    "revision": 1
  },
  "effect": {
    "kind": "breathing",
    "brightnessPercent": 70,
    "colorMode": "dual",
    "primaryColor": "#47E10C",
    "secondaryColor": "#8A2BE2"
  }
}
```

完整示例见
[`docs/examples/northern-lights.oblight`](../examples/northern-lights.oblight)。
动态预设示例
[`docs/examples/slow-fade-sweep.oblight`](../examples/slow-fade-sweep.oblight)
会显示一道向右移动的亮青色光带，身后留有暗蓝色拖尾。
文件结构定义见
[`docs/schemas/openblade-keyboard-lighting-v1.schema.json`](../schemas/openblade-keyboard-lighting-v1.schema.json)。
文件能否使用，最终仍由运行时的语义检查决定。

<a id="envelope"></a>

## 外层结构

| 字段 | 要求 |
| --- | --- |
| `format` | 必须为 `openblade-keyboard-lighting`。 |
| `schemaVersion` | 必须为 `1`。不接受更新的格式版本。 |
| `metadata.name` | 必填，去除首尾空白后为 1–80 个字符。 |
| `metadata.author` | 可选；`null` 或 1–80 个字符。 |
| `metadata.description` | 可选；`null` 或 1–500 个字符。 |
| `metadata.revision` | 由预设作者指定的正整数。 |
| `effect` | 下表中的一个灯效对象。 |

颜色使用 `#RRGGBB` sRGB 表示法。
除 `off` 外，每个灯效都要求 `brightnessPercent` 为 1 至 100。

<a id="effects"></a>

## 灯效

与 Razer Chroma 行为相同的灯效沿用其名称。除此之外，还支持 OpenBlade 已允许使用、由电脑实时渲染的灯效。

| `kind` | 额外字段 |
| --- | --- |
| `off` | 无。 |
| `static` | `color`。 |
| `spectrumCycling` | 无。 |
| `breathing` | `colorMode`：`single`、`dual` 或 `random`；以及与模式相符的 `primaryColor` 和 `secondaryColor` 颜色字段，允许为空。 |
| `reactive` | `color`；`duration`：`short`、`medium` 或 `long`。 |
| `tidal` | `primaryColor`、`secondaryColor`、`randomColors` 和 `direction`。 |
| `starlight` | `colorMode`、匹配的颜色，以及 `duration`。 |
| `wave` | `direction`：`leftward` 或 `rightward`。 |
| `wheel` | `direction`：`clockwise` 或 `counterclockwise`。 |
| `ripple` | `color`。 |
| `ambientAwareness` | `region`：`full`、`left`、`top`、`right` 或 `bottom`。 |
| `audioMeter` | `colorBoost`：1 至 4，步长 0.25；`decayMilliseconds`：0 至 2000，步长 250 ms。 |
| `fire` | 无。 |

导入预设与在灯光页面手动修改设置使用同一套强类型服务操作。
确认对话框会提示预设将立即生效。这些设置会保留为当前电源配置中的未保存更改，
由你选择保存或放弃。导入文件不会改变顶盖标志灯。

<a id="razer-chroma-compatibility"></a>

## Razer `.chroma` 兼容性

Razer 的 [MIT 许可 Chroma 编辑器](https://github.com/razerofficial/CChromaEditor/blob/6ce8b24db8e4e00ddafe027de7589b2a0b4dc618/LICENSE)、
[二进制 `.chroma` 序列化器](https://github.com/razerofficial/CChromaEditor/blob/6ce8b24db8e4e00ddafe027de7589b2a0b4dc618/CChromaEditorLibrary/Animation2D.cpp#L370-L461)
及[对应读取器](https://github.com/razerofficial/CChromaEditor/blob/6ce8b24db8e4e00ddafe027de7589b2a0b4dc618/CChromaEditorLibrary/ChromaSDKPlugin.cpp#L1142-L1356)
是实现兼容性的主要参考。该格式使用固定网格保存动画帧，并记录每帧的显示时长，
Razer 也提供了客户端按顺序渲染这些帧的说明。
不过，这种二进制格式没有用于识别格式的魔数，也不包含网格尺寸、布局标识、元数据外层结构或格式定义。
仅凭通用键盘画布，不能确定它与某个 OpenBlade 型号的协议按键顺序之间存在经过审核的映射。

导入器接受官方二进制版本 1 文件，设备类型须为 `2D`，
设备须为 `Keyboard`（6×22）或 `KeyboardExtended`（8×24）。
它会验证完整的小端结构、帧数、有限正数时长、Win32 `COLORREF` 值、
精确长度，以及 5 MiB 大小限制。它不会加载 Chroma SDK 或 Synapse。

整幅画面颜色一致的文件不依赖键盘布局：全黑输入转换为 `off`，
其他单色转换为 100% 亮度的 `static`。
即使源文件包含多帧相同画面，这种转换也是无损的。

经典 `Keyboard` 格式的逐键灯效文件可用于 RZ09-0581 和 RZ09-0528，这两种布局已经分别验证。
转换程序以 Razer 官方的
[按键名称网格](https://github.com/razerofficial/CChromaEditor/blob/6ce8b24db8e4e00ddafe027de7589b2a0b4dc618/CChromaEditorLibrary/RzChromaSDKTypes.h#L173-L300)
为起点，只转换那些在当前型号上已有获准目标槽位的逻辑按键。
它不会把通用 22 列画布直接压成 Blade 的 17 列协议帧，
也不会套用其他型号的槽位映射。例如，这两个型号的 Right Alt 键就在不同的协议槽位上。

这种按逻辑按键转换的方法不保证与 Synapse 的效果完全相同。
如果源文件在当前笔记本没有获准按键映射的位置设置了颜色，确认对话框会说明将忽略多少个网格单元。
逐键 `KeyboardExtended` 动画不能导入，因为其 8×24 画布包含媒体键和桌面底部灯光区域，
这些区域尚无获准用于 Blade 的映射。整幅画面颜色一致的 `KeyboardExtended` 文件仍可导入。

生成的动画会转换成由服务管理的强类型序列，其中只包含数据。
它随电源配置保存，托盘程序关闭后仍会继续运行。
序列上限为 1,800 个源帧，每帧 1–60,000 ms，总时长 10 分钟，
以及 4,096 个解码后的增量字节。
服务使用现有已获准的矩阵帧操作渲染灯效；导入文件不能指定 HID 报告、命令、设备身份或映射。

严格 JSON 仍是原生预设的标准交换格式。
版本 1 `.oblight` 格式只描述内置的强类型灯效。导入 Chroma 序列不会改变这个格式，
而是将动画转换为受大小和时长限制的配置数据。
后续的 `.oblight` 格式可以支持同样的序列模型，同时保留版本 1 的原有含义。

设计时还参考了以下格式，但它们不能直接作为安全的替代格式使用：

- [Polychromatic 序列](https://docs.polychromatic.app/config/effects/sequence/)
  使用 JSON 帧和循环，但不能仅凭其中的设备名称映射，就允许 OpenBlade 操作相应硬件。
- [OpenRGB 配置](https://gitlab.com/CalcProgrammer1/OpenRGB/-/blob/release_candidate_1.0rc3/ProfileManager.cpp)
  是与控制器身份和 SDK 版本绑定的二进制设备快照。
- [QMK RGB Matrix](https://docs.qmk.fm/features/rgb_matrix)
  清晰区分矩阵、LED 和物理位置，但自定义灯效是编译后的 C 代码。
- [SignalRGB Lightscripts](https://docs.signalrgb.com/developer/)
  是可执行的 HTML 和 JavaScript，不属于本导入器允许的内容。
- [Windows 动态照明](https://learn.microsoft.com/en-us/windows-hardware/design/component-guidelines/dynamic-lighting-devices)
  标准化的是 HID LampArray 设备，而非可移植的灯效文件。

<a id="compatibility-and-versioning"></a>

## 兼容性与版本管理

文件的 `schemaVersion` 独立于 OpenBlade 配置存储版本和 IPC 版本。
版本 1 读取器不会尝试按现有规则解析未知的新版本文件。
新增可选或必填字段、灯效类型、帧模型或布局目标时，
如果无法完整保留旧版含义，就必须为新格式提供独立的读取器。
