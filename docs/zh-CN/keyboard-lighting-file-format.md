<a id="openblade-keyboard-lighting-files"></a>

# OpenBlade 键盘灯光文件

[English](../keyboard-lighting-file-format.md) | 简体中文 | [Русский](../ru-RU/keyboard-lighting-file-format.md)

OpenBlade 键盘灯光文件是扩展名为 `.oblight` 的 UTF-8 JSON 文档。
版本 1 保存一个可移植的键盘灯效预设。导入预设不会赋予硬件功能：
该灯效必须已经针对当前连接的精确 Blade 型号及其固件获得准入。

键盘灯光文件导入属于实验性功能。在 OpenBlade 设置中启用**实验功能**，
即可在灯光页面显示导入器。之后关闭实验功能会隐藏新导入入口，
但不会禁用已生效或已保存在电源配置中的灯效。导入器仅接受该型号支持的灯效。
[实验性笔记本控制](experimental-blade-support.md)会独立于实验功能自动启用。

媒体类型为 `application/vnd.openblade.keyboard-lighting+json`。

此格式仅包含数据，不能包含脚本、插件、外部资源、文件路径、USB 身份、
HID 报告、固件命令或设备映射。
OpenBlade 会拒绝未知字段、重复字段、无效枚举值、不支持的架构版本，
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
使用明亮的青色向右扫过，并留下暗蓝色尾迹。
规范结构见
[`docs/schemas/openblade-keyboard-lighting-v1.schema.json`](../schemas/openblade-keyboard-lighting-v1.schema.json)。
运行时语义验证仍具有最终决定权。

<a id="envelope"></a>

## 外层结构

| 字段 | 要求 |
| --- | --- |
| `format` | 必须为 `openblade-keyboard-lighting`。 |
| `schemaVersion` | 必须为 `1`。未来版本会被明确拒绝。 |
| `metadata.name` | 必填，去除首尾空白后为 1–80 个字符。 |
| `metadata.author` | 可选；`null` 或 1–80 个字符。 |
| `metadata.description` | 可选；`null` 或 1–500 个字符。 |
| `metadata.revision` | 由预设作者选择的正整数。 |
| `effect` | 下表中的一个灯效对象。 |

颜色使用 `#RRGGBB` sRGB 表示法。
除 `off` 外，每个灯效都要求 `brightnessPercent` 为 1 至 100。

<a id="effects"></a>

## 灯效

语义一致的灯效沿用 Razer Chroma 术语，再加入 OpenBlade 已准入的主机渲染灯效。

| `kind` | 额外字段 |
| --- | --- |
| `off` | 无。 |
| `static` | `color`。 |
| `spectrumCycling` | 无。 |
| `breathing` | `colorMode`：`single`、`dual` 或 `random`；以及与模式匹配、可为空的 `primaryColor` 和 `secondaryColor`。 |
| `reactive` | `color`；`duration`：`short`、`medium` 或 `long`。 |
| `tidal` | `primaryColor`、`secondaryColor`、`randomColors` 和 `direction`。 |
| `starlight` | `colorMode`、匹配的颜色，以及 `duration`。 |
| `wave` | `direction`：`leftward` 或 `rightward`。 |
| `wheel` | `direction`：`clockwise` 或 `counterclockwise`。 |
| `ripple` | `color`。 |
| `ambientAwareness` | `region`：`full`、`left`、`top`、`right` 或 `bottom`。 |
| `audioMeter` | `colorBoost`：1 至 4，步长 0.25；`decayMilliseconds`：0 至 2000，步长 250 ms。 |
| `fire` | 无。 |

导入的预设通过与手动编辑灯光页面相同的类型化服务操作应用。
确认对话框会说明它将立即应用；结果会作为当前电源配置的未保存更改，
直到用户保存或放弃。文件不会更改顶盖标志灯光。

<a id="razer-chroma-compatibility"></a>

## Razer `.chroma` 兼容性

Razer 的 [MIT 许可 Chroma 编辑器](https://github.com/razerofficial/CChromaEditor/blob/6ce8b24db8e4e00ddafe027de7589b2a0b4dc618/LICENSE)、
[二进制 `.chroma` 序列化器](https://github.com/razerofficial/CChromaEditor/blob/6ce8b24db8e4e00ddafe027de7589b2a0b4dc618/CChromaEditorLibrary/Animation2D.cpp#L370-L461)
及[对应读取器](https://github.com/razerofficial/CChromaEditor/blob/6ce8b24db8e4e00ddafe027de7589b2a0b4dc618/CChromaEditorLibrary/ChromaSDKPlugin.cpp#L1142-L1356)
是主要的兼容性参考。它们提供有用的固定网格动画帧和逐帧时长，
Razer 也记录了客户端顺序渲染方式。
但该二进制容器没有魔数签名、内嵌网格尺寸、布局标识、元数据外层结构或架构。
通用键盘画布也不能证明它与任一 OpenBlade 型号的精确协议顺序之间存在经过审核的映射。

导入器接受官方二进制版本 1 文件，设备类型须为 `2D`，
设备须为 `Keyboard`（6×22）或 `KeyboardExtended`（8×24）。
它会验证完整的小端结构、帧数、有限正数时长、Win32 `COLORREF` 值、
精确长度，以及 5 MiB 大小限制。它不会加载 Chroma SDK 或 Synapse。

均匀颜色文件与布局无关：全黑输入转换为 `off`，
其他单色转换为 100% 亮度的 `static`。
即使源文件包含多帧相同画面，这种转换也是无损的。

经典 `Keyboard` 逐键文件可用于分别验证过的 RZ09-0581 和 RZ09-0528 布局。
适配器从 Razer 官方的
[命名按键网格](https://github.com/razerofficial/CChromaEditor/blob/6ce8b24db8e4e00ddafe027de7589b2a0b4dc618/CChromaEditorLibrary/RzChromaSDKTypes.h#L173-L300)
开始，只投影目标槽位已对精确型号准入的逻辑按键。
它不会把通用 22 列画布压缩成 Blade 的 17 列协议帧，
也不会将一个型号的槽位映射借给另一个型号。
例如 Right Alt 的准入协议槽位在这两个型号间不同，因此这一点很重要。

这种逻辑按键转换并不宣称与 Synapse 完全一致。
如果源文件为没有准入笔记本绑定的单元格着色，确认对话框会报告将忽略的单元格数量。
逐键 `KeyboardExtended` 动画会被拒绝，因为其 8×24 画布包含没有准入 Blade 投影的
媒体和桌面底部灯光区域。均匀颜色的 `KeyboardExtended` 文件仍受支持。

生成的动画会规范化为服务拥有的、仅含数据的类型化序列。
它随电源配置保存，托盘程序关闭后仍会继续运行。
序列上限为 1,800 个源帧，每帧 1–60,000 ms，总时长 10 分钟，
以及 4,096 个解码后的增量字节。
服务通过现有已准入的矩阵帧操作渲染；导入文件不能提供 HID 报告、命令、身份或映射。

严格 JSON 仍是原生预设的标准交换格式。
版本 1 `.oblight` 架构描述内置类型化灯效；导入的 Chroma 序列不改变此架构，
而是规范化为有边界限制的配置状态。
未来的 `.oblight` 架构可以提供相同序列模型，同时保留版本 1 的含义。

其他格式为设计提供了参考，但不能安全地直接替用：

- [Polychromatic 序列](https://docs.polychromatic.app/config/effects/sequence/)
  使用 JSON 帧和循环，但其设备名称映射不是 OpenBlade 准入证据。
- [OpenRGB 配置](https://gitlab.com/CalcProgrammer1/OpenRGB/-/blob/release_candidate_1.0rc3/ProfileManager.cpp)
  是与控制器身份和 SDK 版本绑定的二进制设备快照。
- [QMK RGB Matrix](https://docs.qmk.fm/features/rgb_matrix)
  清晰区分矩阵、LED 和物理位置，但自定义灯效是编译后的 C 代码。
- [SignalRGB Lightscripts](https://docs.signalrgb.com/developer/)
  是可执行的 HTML 和 JavaScript，超出此导入边界。
- [Windows 动态照明](https://learn.microsoft.com/en-us/windows-hardware/design/component-guidelines/dynamic-lighting-devices)
  标准化的是 HID LampArray 设备，而非可移植的灯效文件。

<a id="compatibility-and-versioning"></a>

## 兼容性与版本管理

文件的 `schemaVersion` 独立于 OpenBlade 配置存储版本和 IPC 版本。
版本 1 读取器不会重新解释未知的未来文档。
新增可选或必填字段、灯效类型、帧模型或布局目标时，
如果无法精确保留旧含义，就需要新的独立架构读取器。
