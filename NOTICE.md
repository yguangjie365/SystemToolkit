# NOTICE — 第三方资源归属与许可证

> SystemToolkit 源代码采用 **MIT** 许可证（见 [LICENSE](LICENSE)，Copyright © 2026 yguangjie365）。
> 本文件列出随软件分发的**第三方字体与图标资源**，它们遵循各自独立的许可证，**不适用 MIT**。

---

## 一、字体

### 1.1 Cormorant Garamond

| 项 | 内容 |
|---|---|
| 用途 | 西文衬线显示字体（页面标题、数值大字样） |
| 许可证 | **SIL Open Font License 1.1** |
| 版权 | Copyright © 2015-2021 Christian Thalmann |
| 可否子集化 | ✅ 可以 |
| 获取 | https://github.com/CatharsisFonts/Cormorant |

### 1.2 霞鹜文楷 LXGW WenKai

| 项 | 内容 |
|---|---|
| 用途 | **中文衬线/楷体标题** |
| 许可证 | **SIL Open Font License 1.1**（基于 Klee One） |
| 版权 | Copyright © 2021 落霞孤鹜（LXGW）；Klee One 部分 © FONTWORKS Inc. |
| 可否子集化 | ✅ 可以（本项目将做常用字子集化） |
| 获取 | https://github.com/lxgw/LxgwWenKai |

### 1.3 Inter

| 项 | 内容 |
|---|---|
| 用途 | 西文无衬线正文 |
| 许可证 | **SIL Open Font License 1.1** |
| 版权 | Copyright © 2016-2024 The Inter Project Authors |
| 可否子集化 | ✅ 可以 |
| 获取 | https://github.com/rsms/inter |

### 1.4 ⚠️ HarmonyOS Sans

| 项 | 内容 |
|---|---|
| 用途 | **中文无衬线正文** |
| 许可证 | **HarmonyOS Sans Fonts License Agreement**（华为终端有限公司） |
| 版权 | © Huawei Device Co., Ltd. |
| 可否子集化 | 🔴 **不可** —— 协议第 2 条禁止对字体或其任何组件进行修改，子集化属修改 |
| 获取 | https://developer.huawei.com/consumer/cn/design/resource-V1 |

**协议要求（本项目必须遵守）：**

1. ✅ **显著标注**：本文件与应用 About 页均已标注使用了 HarmonyOS Sans Fonts
2. 🔴 **不得修改**：不子集化、不改字形，仅原样打包分发
3. ✅ **不单独销售**：字库不单独分发，仅随本应用捆绑
4. ✅ **保留声明**：字体目录内附华为协议原文（`Resources/Fonts/HarmonyOS/LICENSE_Fonts.txt`）

> ⚠️ 该许可为**可撤销**的全球免费授权，且非开源许可证。本项目通过 `FontFamily` 回退链引用字体，不硬编码单一字体名，以便将来需要替换时只改令牌。

### 1.5 JetBrains Mono

| 项 | 内容 |
|---|---|
| 用途 | 等宽字体（日志、命令输出、硬件 ID、哈希值） |
| 许可证 | **SIL Open Font License 1.1** |
| 版权 | Copyright © 2020 JetBrains s.r.o. |
| 可否子集化 | ✅ 可以 |
| 获取 | https://github.com/JetBrains/JetBrainsMono |

---

## 二、图标

⏳ 待定。已定原则：

- 🔴 **全部本地**（SVG 或 XAML 路径资源），**禁止任何 CDN / 在线图标库引用**
- 🟠 线性风格，24×24 栅格，2px 线宽
- 🟠 图标颜色一律走 `Color_*` 令牌，不写死色值

选定后在此登记名称、许可证与来源。

---

## 三、设计参考

以下项目**未被复制代码**，仅作为设计思路参考（无许可证义务，但致谢是应有的尊重）：

| 项目 | 许可证 | 参考了什么 |
|---|---|---|
| [lostindark/DriverStoreExplorer](https://github.com/lostindark/DriverStoreExplorer) | GPL-2.0 | 驱动管理模块的清理分类思路、pnputil 输出多语言解析的坑。**未使用其任何源码**（本项目 MIT，GPL 不兼容） |
| Claude.com 品牌设计分析（`DESIGN.md`） | — | 初始视觉风格的色彩体系与排版气质 |

---

## 四、运行时依赖的第三方组件

见 [ADR-002 §5.3 依赖清单](Docs/decisions/ADR-002-驱动中心技术来源与第三方代码引入规范.md#53-第三方依赖清单)。该清单是应用 About 页的**唯一数据源**，新增依赖不登记即视为违规。

当前已登记：

| 组件 | 许可证 | 用途 |
|---|---|---|
| `Microsoft.Dism` | MIT | 驱动管理 DISM 后端 |
| `WatsonTcp` | MIT | 文件互传 TCP 分片传输 |
| `Net.Codecrete.QrCodeGenerator` | MIT | 互传二维码生成 |
| `AlphaVSS` | Apache-2.0 | 备份模块 VSS 卷影 |
| `LibreHardwareMonitorLib` | **MPL-2.0** | 硬件传感器采集（温度/风扇/电压）——见下方 §4.1 的 MPL 义务说明 |
| `Hardware.Info` | MIT | 硬件静态信息采集（CPU/内存/主板/磁盘） |
| `NAudio` | MIT | 音乐播放内核（WASAPI / WaveOut） |
| `TagLibSharp` | **LGPL-2.1-only** | 音频标签读取（标题/艺术家/专辑/时长/内嵌封面）——见下方 §4.2 的 LGPL 义务说明 |

### 4.1 ⚠️ LibreHardwareMonitorLib（MPL-2.0）义务说明

**MPL-2.0 不在 AGENTS.md §2 的许可证红线禁列内（禁列：GPL/AGPL/SSPL/CC-BY-SA/CC-BY-NC/无许可证），因此引入合规。但 MPL-2.0 是「文件级 copyleft」，带有以下义务，本项目必须遵守：**

| 义务 | 本项目落实情况 |
|---|---|
| ① **显著声明**该组件使用 MPL-2.0 | ✅ 本表 + About 页数据源（ADR-002 §5.3）均已登记 |
| ② 提供 **MPL-2.0 许可证全文** | 🟠 须随分发物附带（打包时把 `MPL-2.0.txt` 放进安装目录 `LICENSES/`） |
| ③ **被修改的 MPL 源文件须开源** | ✅ 本项目以 NuGet 二进制引用，**未修改其任何源文件**，故无源码披露义务 |
| ④ 与本项目 MIT 代码的**分离** | ✅ 仅通过公开 API 调用（依赖注入 + 接口隔离），未见源码级混合、未见复制片段 |

**⚠️ 衍生组件提示（非本项目直接依赖，但随 LHM 二进制分发）：**
LibreHardwareMonitor 的底层驱动封装涉及 **WinRing0 / PawnIO**。本项目走「PawnIO 路线」（见 `Docs/09-开发规范/TASKS.md` K-001 复诊），
未内置 WinRing0 驱动二进制。若将来要切换回 WinRing0 路线，**须单独核实其许可证条款**并在本表登记后再引入。

**🟠 待办（Q-015 未决）**：MPL-2.0 是否长期可接受，仍需用户拍板。若否决，替代路线为「纯 WMI + `Hardware.Info` + PawnIO 自采」，
代价是温度/风扇覆盖率下降（详见 TASKS K-001）。

### 4.2 ⚠️ TagLibSharp（LGPL-2.1-only）义务说明

**LGPL-2.1 属 `02 分册 §9.2` 许可证白名单的 🟡 级（需用户确认），不在 AGENTS.md §2 红线禁列内（禁列：GPL/AGPL/SSPL/CC-BY-SA/CC-BY-NC/无许可证）。
用户已于 2026-09-07 裁定引入，该裁定即构成步骤② 的 🟡 确认。LGPL-2.1 是「库级弱 copyleft」，义务如下：**

| 义务 | 本项目落实情况 |
|---|---|
| ① **显著声明**该组件使用 LGPL-2.1 | ✅ 本表 + About 页数据源（ADR-002 §5.3）均已登记 |
| ② 提供 **LGPL-2.1 许可证全文** | 🟠 须随分发物附带（打包时把 `LGPL-2.1.txt` 放进安装目录 `LICENSES/`，与 MPL-2.0 同批处理） |
| ③ 用户须能**替换该库**（反向工程/重新链接的权利） | ✅ 以 NuGet 独立 DLL 引用，**不合并、不 ILMerge、不嵌入单文件发布**，用户可直接替换 `taglib-sharp.dll` |
| ④ **未修改其源文件**故无源码披露义务 | ✅ 仅通过公开 API（`TagLib.File.Create` / `Tag` / `Properties` / `Pictures`）调用 |
| ⑤ 与本项目 MIT 代码的**分离** | ✅ 经 `Core/Music/Services/IMusicTagReader` 接口隔离，唯一实现类 `TagLibMusicTagReader` 封装全部 TagLib 类型，其余代码零 TagLib 依赖 |

**为什么不是 ATL（`z440.atl.core`）**：旧工程 `LocalMusicScanner` 用的是 ATL，但 ATL 7.16.0 的传递依赖 `Ude.NetStandard` 1.2.0
是 **MPL-1.1 / GPL v2+ / LGPL v2.1+ 三重授权**且包内不含许可证全文。三重授权里的 GPL 选项直接触碰本项目（MIT 开源）红线，
而「可选择 LGPL」这一判断缺乏包内文本佐证，无法自证合规。`TagLibSharp` 2.3.0 **零传递依赖**，许可证由 NuGet 明确标注为
`LGPL-2.1-only`，合规链条完整，故取代 ATL。详见 ADR-002 §5.4 音乐中心条目。

---

## 五、字体文件目录约定

```text
src/SystemToolkit.UI.Common/Resources/Fonts/　（✅ 2026-09-05 已落地：8 个字体文件 + 各许可证原文随附）

├── CormorantGaramond/
│   ├── CormorantGaramond-Regular.ttf
│   ├── CormorantGaramond-Medium.ttf
│   └── OFL.txt                    ← SIL OFL 原文，必须随附
├── LXGWWenKai/
│   ├── LXGWWenKai-Subset-Regular.ttf
│   ├── LXGWWenKai-Subset-Medium.ttf
│   └── OFL.txt
├── Inter/
│   ├── Inter-Regular.ttf
│   ├── Inter-Medium.ttf
│   └── OFL.txt
├── JetBrainsMono/
│   ├── JetBrainsMono-Regular.ttf
│   └── OFL.txt
└── HarmonyOS/
    ├── HarmonyOSSansSC-Regular.ttf
    └── LICENSE.txt                 ← 华为协议原文（实存文件名）
```

🔴 每个字体目录**必须**包含其许可证原文文件。缺一即视为违规。

🟠 字体的加载方式：`pack://application:,,,/Resources/Fonts/<目录>/#<字体名>`，全部本地，**不访问网络**。
