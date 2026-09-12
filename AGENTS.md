# AGENTS.md — SystemToolkit 开发硬约束

> 🆕 **新接入 Agent 请先读 [`Docs/00-入口/PROJECT-OVERVIEW.md`](Docs/00-入口/PROJECT-OVERVIEW.md)**（技术栈 / 目录 / 模块 / 流程 / 环境 / 契约 / 规范，8 章带溯源路径，约 8KB）。本文件是**硬约束速览**（27KB），建议按需查阅而非通读。文档全仓清单与时效评估见 [`Docs/00-入口/DOCS-INVENTORY.md`](Docs/00-入口/DOCS-INVENTORY.md)。

> 📌 **本文件是速览。** 详细规范以 **[`Docs/40-开发规范/`](Docs/40-开发规范/README.md) 为唯一权威来源**（总纲 + 6 分册 + 任务板）。两者冲突时以 `Docs/40-开发规范/` 为准。
> 🔴 动手前必须先读 [`Docs/40-开发规范/README.md`](Docs/40-开发规范/README.md) 与 [`05-多Agent协作与提交规范.md`](Docs/40-开发规范/05-多Agent协作与提交规范.md)。

本文件是 AI Agent 参与本项目时**必须遵守**的纪律。规则分三级：

- 🔴 **红线** —— 违反即视为事故，必须回退
- 🟠 **强制** —— 每次提交都要满足
- 🟡 **规范** —— 默认遵守，有理由时可偏离但需说明

---

## 一、当前阶段与协作机制

🚧 **音乐管理已交付（M5 + 在线流阶段二；MUSIC-7 已于 2026-09-12 验收通过——迷你播放条改版为独立迷你窗），下一站设置模块 + V1.0 重装助手**（已交付：概览/软件/驱动/网络/互传/游戏/备份/音乐；项目总控与路线图见 [`Docs/00-入口/00-项目总控.md`](Docs/00-入口/00-项目总控.md)）。

- 需求/设计文档的**修订**仍需与用户沟通后执行（见 04-UI 设计规范 v2 的裁定记录）。
- 上一个工程 `D:\MyProject\FileBackup.CSharp` 是知识库，不是起点。
- 模块开发顺序定稿（2026-09-05）：网络 → 备份 → 互传 → 游戏 → 音乐 → 设置 → 重装助手（压轴），依据见各模块设计文档「参考实现与复用策略」节。

🔴 **多 Agent 协作三条铁律**（详见 [`Docs/40-开发规范/05`](Docs/40-开发规范/05-多Agent协作与提交规范.md)）：

1. **先认领后动手** —— 在 [`TASKS.md`](Docs/40-开发规范/TASKS.md) 署名改状态，未认领的改动可被回滚
2. **改动必须留痕** —— 每个任务在 `.workbuddy/changes/` 写变更记录，含「没做什么」与「需要他人注意」
3. **不确定的先问** —— 规范未覆盖、文档缺失、需跨领地改动时停下来问，不要推断

🔴 **澄清优先铁律**（2026-09-04 用户明令，适用于一切任务）：
需求不明确、信息不完整、存在歧义时——**不假设、不猜测、不独自反复推理**。立即暂停任务，向用户提出**具体**澄清问题，确认后再继续。
确需基于假设推进的场合：必须**明确列出所依赖的每一条假设及其潜在影响**，**等待用户确认后**方可执行。

🔴 **同文件必须串行编辑** —— 并行编辑同一文件会互相覆盖丢失编辑（实测教训）。

---

## 二、架构分层

🔴 **物理目录必须分离，依赖只能单向向下：**

```text
Shell（WinExe 宿主，导航/主题/通知/危险操作确认）
  ↓
Modules/<模块>（View + ViewModel，模块之间零引用）
  ↓
Application（跨模块编排，如重装助手）—— 🟡 **预留未使用**，见下方 §状态
Core（域服务，纯逻辑，零 UI 依赖）
  ↓
Infrastructure（SQLite / Windows API / 网络 / 文件系统）
Abstractions（接口契约，与 Core 并列，彼此零引用）
UI.Common（共享控件与设计令牌）
```

🔴 **硬规则：**

1. `Core` 与 `Abstractions` 零 UI 依赖、零互相引用。
2. 模块之间不得互相引用；跨模块调用必须经 `Application` 层。
3. 重装助手**不得重复实现**驱动/软件/网络/备份能力，只调用其公开服务。
4. 新增项目必须登记到依赖守卫白名单（见第六节）。
5. UI 层不得直接调用 `pnputil` / `netsh` / `reg` 等系统命令，必须经 Core 服务。

### Application 层状态（2026-09-06 用户裁定）

🟡 **预留（reserved）**：`src/SystemToolkit.Application/` 当前**零个 .cs 文件**，是有意为之，不是遗漏。

- **为什么现在不填**：V1.0 重装助手的需求尚未定稿。此时设计 `IAppComposition` 之类的抽象，
  大概率在真正开工时被推翻返工——空接口比没有接口更糟（它会伪装成"架构已兑现"）。
- **什么时候填**：V1.0 重装助手开工时，第一个跨模块调用（如"重装前备份驱动 + 软件清单 + 网络配置"）
  出现的那一刻，把该调用下沉到本层并同步落抽象。
- **在这之前**：跨模块逻辑**一律不放这里**。若某处确实需要组合两个模块能力，
  先提出来讨论，不要自行在本层起接口。
- **机器约束**：`tests/SystemToolkit.Tests/Architecture/ApplicationLayerReservedGuardTests.cs`
  **已实装**（2026-09-05 落地，当前全绿）：`tests/SystemToolkit.Tests/Architecture/ApplicationLayerReservedGuardTests.cs`
会拦截"往本层塞代码"的行为——测试红时按测试消息里的步骤走（改文档 + 移除守卫），不要绕过。

---

## 二·五、参考项目学习纪律（2026-09-05 事故教训，最高优先级）

🔴 **先读参考实现，再动手写代码**。用户指明参考项目（如 DriverStoreExplorer）时，第一步是**抓取并阅读该项目对应域的源码文件**（GitHub raw / 本地 clone），提取：
- P/Invoke 签名与导出名（含 W/A 后缀规律）
- 全部常量（错误码、属性 ID、数据类型枚举——**一律抄参考源码或官方头文件，禁止凭记忆写**）
- 数据结构与缓冲区处理模式（multi-sz、两段式探测等）
- 关联算法与架构分层思路

🔴 **参考 ≠ 抄袭**：学的是思路、方法、踩坑结论；代码按本项目规范（命名/分层/可序列化约束）重写。

🔴 **禁止凭记忆写 Windows 原生 API 互操作**。今晚实证：6 个坑（W 后缀、multi-sz 截断、CR_BUFFER_SMALL=0x1A、REG_SZ=1、CM_DRP 表、DEVPKEY pid 表）全部存在于参考源码中，凭记忆猜 + 试错验证烧掉整晚时间与大量 Token。

🔴 **遇不熟悉的 API/格式，先查官方文档或参考实现，再写第一行代码**。"主动学习"是硬要求，不是可选项。

## 三、安全与操作纪律

🔴 **特权操作**：一律经 `ElevatedHelper` 进程按需提权。UAC 被拒绝时流程安全终止，**无副作用**，并记录日志。

🔴 **危险操作四步齐全**：`预览 → 确认 → 执行 → 记录`。删除/覆盖类操作必须二次确认。

🔴 **禁止静默失败**：哈希校验失败、备份中断、安装失败等一律显式提示并阻断后续，不得吞异常继续。

🔴 **驱动红线**：
- 不简单复制 `C:\Windows\System32\drivers`
- 不直接删除 DriverStore 底层文件
- 不自研驱动管理格式
- 一切操作经 Windows 官方机制（Driver Store / INF / PnPUtil / DISM / SetupAPI）

🔴 **路径越界守卫**：任何解压、恢复、外部路径拼接都必须校验目标落在允许根目录内。

🔴 **敏感数据**：配对令牌、Session Secret、第三方凭据一律经 DPAPI / Credential Manager 加密，禁止明文落库。

🔴 **第三方代码许可证红线**（本项目为 **GPL-3.0** 开源项目；2026-09-08 由 MIT 变更，见 [ADR-004](Docs/50-决策/ADR-004-开源许可证变更MIT转GPL-3.0.md)）：
无许可证 / SSPL / **GPL-2.0-only / LGPL-2.1-only** / CC-BY-SA / CC-BY-NC / 专有许可证的代码**一律禁止引入**，无论多好用。
🟢 GPL-3.0 / LGPL-3.0-or-later / AGPL-3.0 源码可吸收（并入后整体按 GPL-3.0 授权）；MIT / Apache-2.0 / BSD / ISC 可引入（保留版权声明并登记，许可证原样保留）。
🟡 GPL-2.0-or-later / LGPL-2.1-or-later / MPL-2.0 / CDDL：有版本或文件级兼容条件，引入前须逐项核实并登记。
引入任何第三方代码前必须走 [02 分册 §9.2](Docs/40-开发规范/02-架构与依赖规范.md#92-强制流程-缺一不可) 的五步流程并登记。
典型陷阱：`DriverStoreExplorer` 源码为 GPLv2——**须核实其文件头是否「version 2 or later」授权**：v2-only 不可并入 GPL-3.0 项目；核实前仍只作参考实现、禁止复制源码（详见 [ADR-002](Docs/50-决策/ADR-002-驱动中心技术来源与第三方代码引入规范.md)）。

🟠 **修改类操作**（DNS / 静态 IP / 调优 / 注册表）必须：保存快照 → 修改 → 验证 → 失败可一键回滚。

🟠 **文件写入**使用原子写（临时文件 + 替换），避免中断产生半截文件。

🟡 **多值假设**：多网卡、多磁盘、多显示器、多音乐库、多 Steam 库路径——列表展示全部，不做单值假设。不假设"第一个网卡是主网卡"。

### 3.1 变更落盘纪律（🔴 硬规则，2026-09-04 事故教训）

> 背景：V0.2-D 开发中，补丁脚本（python heredoc 批量替换）中途 assert 失败，已执行部分写入、剩余部分丢失，且 Agent 误以为全部成功——用户后续三轮看到的仍是旧 UI。

**规则 1：批量修改后必须验证落盘。** 每完成一批文件修改，立即用 grep/Read 对**关键改动点逐条复核**（改了什么就查什么），确认内容真的在磁盘上。不得以"脚本没有报错"作为成功依据——assert 失败时前面已写入的部分同样不可信。

**规则 2：优先用编辑工具，慎用补丁脚本。** 单点修改一律用 Edit/Write 工具（所见即所得，天然落盘）；只有跨多文件的机械性批量替换才允许 python/sed 脚本，且脚本内**每个替换必须 assert old in new**，一旦断言失败必须先重读文件核对当前状态再决定补救，禁止盲目重跑。

**规则 3：脚本中断 ≠ 部分成功。** 任何修改流程被打断（报错/超时/工具异常）后，第一动作是重新核对目标文件实际内容，列出"已落盘 vs 未落盘"清单，再决定续做方案。

**规则 4：构建绿 ≠ 改动生效。** 构建只证明语法正确，不证明"想改的东西都改了"。验收依据是 grep 复核 + 用户实测，不是构建退出码。

**规则 5：全文件重写必须从现文件出发。** 用 Write 整文件重写已存在的文件前，**必须先 Read 现文件**，重写内容以磁盘现版本为基线（不得凭记忆重建）；重写落盘后必须 `git diff <file>` 复查，确认此前的修复点没有被覆盖丢失。
（事故链：FileLogger 注册 → 后续重写 OverviewModule 覆盖回 NullLogger → 概览诊断两次失明。）

**规则 6：回归必须沉淀为守卫测试。** 同一类问题出现第二次，就必须有一条能在构建时自动抓捕它的测试——流程靠自觉会失效，测试不会。已有守卫：依赖白名单 / 模块零交叉 / manifest 权限 / 模块清单同步 / 令牌字典完整性 / DI 注册行为（详见 `tests/SystemToolkit.Tests/Architecture/`）。新增守卫必须做反向验证（人为破坏 → 红 → 恢复 → 绿）。

**规则 7：每轮收尾必须对账，禁止盲加。**（2026-09-04 事故：91 个追踪文件被外部进程从工作区删除，靠用户报错才暴露）
- 每轮文件操作结束后执行 `git status --porcelain` 对账：出现的 M/A/D 若非本轮预期，立即报告用户并止损（未暂存删除可 `git restore` 无损恢复，前提是发现得早）。
- 🔴 禁止 `git add -A` / `git add .` 盲加——会无声吞下删除并提交进历史。必须按明确路径 add，提交前复核 status 输出。
- 检测到 IDE 异常行为（锁构建产物、文件缓冲回写、文件无故消失）时，停止高强度文件操作，建议用户重启 IDE 后再继续。

### 3.2 提交纪律（🔴 硬规则）

🔴 **每次变更完成即本地 commit**：一个逻辑完整的变更单元（修一个 bug / 一轮反馈整改 / 一份文档）完成并通过构建+测试验证后，**立即 `git commit`**，不攒批次、不跨任务。提交信息按现有格式（type(scope): 摘要 + 要点 + 验证结果）。

🔴 **commit 是最小保护单元**：commit 之后才允许开始下一个变更。这样任何一轮改坏都可 `git diff` 精确回看，也让"改动从未落盘"类事故有历史可查。

🟠 **push 仍按用户指令执行**（当前约定），commit 不需要等用户确认。

---

## 四、UI 设计系统纪律

> **这是上一个工程最大的失败点，本项目必须根治。**
> 技术栈已定：**WPF on .NET 10 LTS**。决策依据见 [ADR-001](Docs/50-决策/ADR-001-UI技术栈选型.md)。

🔴 **唯一 UI 技术体系 = WPF**。禁止任何第二套视觉实现（Blazor / CSS / Web）。旧工程 25 XAML + 25 razor 各占一半，是视觉不一致的根因。

🔴 **禁用 `Wpf.Ui`**。其隐式样式会渗透自定义 ControlTemplate 导致控件塌缩（旧工程实测）。**控件全部自建**。

🟢 **双主题可切换**（2026-09-10 放开，见 [ADR-005](Docs/50-决策/ADR-005-双主题可切换ClaudeLight与NvidiaDark.md)）：
`Claude.Light`（浅色，默认）与 `Nvidia.Dark`（深色）运行时切换、不重启——经 `ThemeManager` 替换
`MergedDictionaries[0]`（令牌字典），全站 `{DynamicResource}` 自动刷新；偏好持久化于
`%AppData%/SystemToolkit/appearance.json`（AtomicFile 原子写，损坏回退默认）。
🔴 **新增主题包的硬门禁**：每个主题包都必须覆盖 `TokenKeys` **全量** key（含深色包），
且需单独满足对比度与层次纪律（深底浅字、层次不依赖阴影）。深海军蓝 `#181715` 仍只作内容展示容器。

🔴 **设计令牌必须全覆盖五个维度**，不能只覆盖颜色字号：

```text
颜色 · 字号 · 间距 · 圆角 · 阴影/层级
```

✅ **落地状态（2026-09-05）**：五维令牌已全量实现（`TokenKeys.cs` 契约 65 key ↔ Claude.Light.xaml 双向覆盖守卫）；
五款字体已打包（Inter/HarmonyOS Sans SC/JetBrains Mono/霞鹜文楷/Cormorant，见 04-UI设计规范 §3.2 字体族令牌）。
**唯一权威值表 = `Docs/20-专题设计/04-UI设计规范.md` §三（自实现反向校准）**。

🟠 **禁止裸值**：UI 代码中不得出现硬编码色值、字号、Margin/Padding/Width/Height、圆角。旧工程在间距维度有 **428 处裸值**完全失守，这是本次重写的重点治理对象。
✅ **强制机制（2026-09-05）**：`UiTokenRatchetTests` 棘轮守卫——四视图字号/圆角字面量零容忍、Margin/Padding 按 baseline.json 基线只减不增；`SourceSanityGuardTests` 管直写文件与 sync-over-async。

🟠 **复合模板先抽象后复用**：同一结构出现 2 次以上必须抽为共享模板/组件。旧工程三清单卡片 3 份复制 272 行、日志面板两套实现、分区标题 3 份、骨架屏 4 份。

🟠 **三态齐备**：每个列表/页面必须有正常态、空状态、失败/加载失败态。

🟠 **对比度达标**：正文 ≥ 4.5:1，次要文字 ≥ 3:1，边框/选中态 ≥ 3:1。旧工程出现过 1.16:1 的选中态，不可接受。

🟠 **层次不依赖阴影**：卡片与背景的区分不能只靠阴影（旧工程仅 1.078:1，阴影一失效结构崩塌）。

🟡 **先组件库后业务页**：V0.1 阶段先交付 Shell + Design Token + 基础组件库，再写业务页面。

🟠 **音乐管理二阶段**若接第三方平台，用**独立 WebView2 窗口**承载，进程级隔离，不并入主体系。

### 4.1 窗口与缩放（🔴 硬规则）

| 项 | 规定 |
|---|---|
| 启动 | **不得最大化、不得全屏**。启动窗口 1280×800，居中 |
| 设计基准 | 1440×900（16:10）；最小窗口 1240×640（2026-09-04 由 1024 调整：保证软件管理页最低列宽配置不裁剪，依据见 [07-WPF界面实现经验](Docs/40-开发规范/07-WPF界面实现经验.md)） |
| 缩放 | `Scale = clamp(min(W/1440, H/900), 0.75, 2.0)` |

🔴 **禁止使用 `ViewBox` 做整体缩放**。ViewBox 给子内容无限可用尺寸，会让 `VirtualizingStackPanel` 虚拟化失效——本项目有多处万级列表，会启动即卡死。
🔴 所有尺寸与字号**必须**走 `{DynamicResource}` 令牌，禁止写死像素。
🔴 布局结构不随缩放改变：**无响应式断点、无重排**。Grid 行列用 `Auto` / `*`。

### 4.2 主题包（🔴 硬规则）

- 🔴 每个主题包必须覆盖 `TokenKeys` 全量 key，缺一即构建失败（✅ 已实现：`TokenKeysCoverageTests` 双向校验）
- 🔴 控件样式内**禁止**硬编码色值/字号，只能引用令牌
- 🔴 每个主题包（Light 与 Dark 各自）都必须独立覆盖全量 `TokenKeys`（ADR-005）
- 🔴 **不使用旧工程的任何风格文件**（`Tokens.xaml` / `Theme.xaml` / `tokens.css`），全部重写
- 🔴 **资源全部本地化**：字体打包进 `Resources/Fonts/`，图标为本地 SVG/XAML 路径资源。**禁止任何 CDN 或远程引用**，应用完全断网时视觉必须与联网一致
- 🟠 主题切换不重启应用；`ThemeManager` 负责切换、缩放计算与资源合并
- 🟠 对比度断言自动化：正文 ≥4.5:1、次要 ≥3:1、边框与选中态 ≥3:1，每个主题包全跑

当前初始风格：**Claude 暖调**（奶油白 `#faf9f5` + 珊瑚橙 + 深海军蓝）。详见 [ADR-003](Docs/50-决策/ADR-003-主题系统与初始视觉风格.md) 与 [`Docs/20-专题设计/04-UI设计规范.md`](Docs/20-专题设计/04-UI设计规范.md)。

🟠 **UI 实现前必读**：[`Docs/40-开发规范/07-WPF界面实现经验.md`](Docs/40-开发规范/07-WPF界面实现经验.md)——V0.2 沉淀的 DIP/物理像素换算、字号字重基线（雅黑无 Medium）、GridView 列宽自适应方法论、WPF 默认值陷阱清单。写 XAML 前不读此文档，会重复踩已付过学费的坑。

---

## 五、编码规范

- 目标框架：`net10.0`（Core/Abstractions）、`net10.0-windows10.0.19041.0`（UI 相关）
- `Nullable=enable`、`ImplicitUsings=enable`、`LangVersion=latest`
- 命名空间 file-scoped；私有字段 `_` 前缀
- 跨平台 TFM 的 Core 内部调用 Windows API → 方法内 `OperatingSystem.IsWindows()` 运行时守卫，**不要**用 `[SupportedOSPlatform]`（会导致 CA1416）
- ⚠️ `SystemToolkit.Core.*` 下写 `Environment.GetFolderPath` 会解析到自身命名空间，必须全限定为 `System.Environment`
- 禁止 sync-over-async；异步方法必须带 `CancellationToken` 参数
- 日志统一走 `AppLog` 进程内总线（业务侧只面向 `ILogger`/`LogEntry`），落盘引擎为 **Serilog**（LOG-1，2026-09-11）；字段：Timestamp / Level / Module / Action / Result / Duration / Exception / **CorrelationId**（`LogEntry` 承载，JSONL 平铺输出；`Result` 的 C# 属性名为 `Outcome` 以规避源码守卫对 `.Result` 的误报）
- 长时间操作必须支持取消与进度上报，放后台执行，不阻塞 UI

---

## 六、测试与验收

🟠 **Release 构建零警告零错误**（`TreatWarningsAsErrors` 仅对 Release 生效）。

🟠 **每个模块交付必须包含单元测试**，覆盖：解析器 / 匹配算法 / 冲突策略 / 异常路径。

🟠 **架构守卫测试**（旧工程有 5 类 22 Fact，本次重写保留并扩展）：

| 守卫 | 职责 |
|---|---|
| `TokenGuard` | 设计令牌合规，含**间距维度棘轮**（只降不升） |
| `DependencyGuard` | 直接读 csproj 文本校验分层依赖 |
| `DependencyInjectionGuard` | DI 注册完整性 |
| `UiTemplateGuard` | UI 模板复用度 |
| `AtomicFileGuard` | 文件写入必须走原子写 |

🟠 **新增架构守卫必须反向验证**：注入违规 → 断言变红 → 恢复 → 变绿，并记录"只红了哪几条"。

🟡 合入新模块前跑全量测试，不能只跑模块自测（模块自测绿 ≠ 架构守卫绿）。

---

## 七、构建环境（本机已知坑）

⚠️ **文件锁**：本机 IDE 的 C# 语言服务会锁定 `obj/Debug` 输出，导致常规 `dotnet build` 频繁失败。

应对（按优先级）：

1. 把项目根、`~/.nuget`、dotnet/VS 目录加入 Defender 排除；把 `VBCSCompiler.exe` / `csc` / `dotnet` / `MSBuild` / `testhost` 加入进程排除（旧工程有 `tools/Defender-Exclusions.ps1` 可借鉴）
2. 兜底：`dotnet build --artifacts-path .artifactsN`（⚠️ 勿与常规构建混用，否则 `BG1002 找不到 xxx.baml`）
3. 构建前确认 IDE 未打开相关项目

⚠️ **会话 bash 缺系统变量**：`APPDATA` / `ProgramFiles(x86)` / `ProgramData` / `ALLUSERSPROFILE` 可能为空，导致 `dotnet restore` 报 `NuGet.targets: path1 null`。解法：命令前用 `env` 显式补齐。

⚠️ **并行 Edit 同一文件会互相覆盖**，同文件编辑必须串行。

⚠️ **管道吞构建退出码**：`dotnet build … | tail` 的退出码是 `tail` 的，构建失败会被 `&&` 链判为成功、后续测试跑的是旧程序集（2026-09-04 实际发生，会话内"全绿"是假象）。**会话内构建/测试命令必须以 `set -o pipefail` 开头**，或改用输出落地后检查。

🟡 **验证产物真带新代码**：二进制 grep 特征串（UTF-8 与 UTF-16-LE 都要试）。

---

## 八、旧工程资产处理

参考工程：`D:\MyProject\FileBackup.CSharp`（283 `.cs` / 约 4.4 万行 / 474 测试 / WPF + BlazorWebView）

### ✅ 功能层代码：默认可复用

用户明确：**新项目可以复用原项目功能层的代码。**

因此下表清单为**默认可复用**，无需逐项申请；搬移时只需遵守命名与架构规范（改命名空间、去 UI 依赖、补 XML doc、补单测）。

🔴 **复用纪律（用户 2026-09-04 补充）**：功能层（Core / Infrastructure / Application）凡是旧项目代码可复用的，**直接复用，禁止重新编写**；若判断必须重写，须先向用户**说明理由**（放在变更记录与任务描述中），经确认后再动手。

🔴 **主动挖掘（用户 2026-09-04 再次强调）**：用户评价"原项目功能层写得还不错"，要求**每个模块开工前主动翻找旧项目对应域的可复用实现**（含埋在弃用 UI 文件里的纯逻辑——快照缓存即如此被发现的），不许只搬"已登记清单"。搜索顺序：旧项目对应域目录 → 其测试文件（契约与用法示例）→ 旧工作日志的踩坑注记。注意：搬移后此前的修复点可能被后续重写覆盖，须遵守 §3.1 规则 5。

🔴 **UI 先审后写（用户 2026-09-04 补充）**：任何功能页面的 UI 代码开发前，必须先向用户**描述布局方案并出示意图**，**经用户确认后**才能写 UI 代码。包括 XAML 结构、区域划分、交互要素；确认记录留在任务/变更记录中。

### 可直接复用清单（纯逻辑、零 UI 沾染）

| 分类 | 资产 |
|---|---|
| 工具 | `AtomicFile`（原子写）、`PathUtil`（越界守卫）、`FormatUtil`、`DiskSpaceUtil`、`UserFolders`、`NetAdapterFilter`、`QrMatrix` |
| 哈希 | `Sha256Hasher` |
| 网络 | `NetConfigService`、`NetDiagnosticService`、`NetRepairService`、`TcpTuningService`、`NetworkInfoService`、`HostsCheckService`、`WinInetInterop`、`InterfaceTableParser`、`TcpSettingsParser`、`HostsParser`、`IpValidation`、`CommandRunner` |
| 文件传输 | `DeviceDiscoveryService`（UDP 发现）、`FileTransferService`、`TransferProtocol` 协议层 |
| Steam | `SteamService`、`SteamRegistry`、`VdfParser` |
| 音乐 | `MusicPlayerEngine`（NAudio 播放内核）、`LocalMusicScanner`（并行扫描骨架 + 文件名兜底；**标签层由 ATL 改为 TagLibSharp**，ATL 已因许可证否决，见 ADR-002 §5.4）、`LyricParser`、`MusicService`（1197 行：播放队列/循环模式/自动切歌/本地导入去重——**漏登记会导致音乐功能层缺失**，搬移时须按 500 行上限拆分）、`MusicModels`（依赖类型，缺则无法编译） |
| 概览 | `OverviewService`、`LiveUsageSampler`、`HardwareSensorProbe`、`OverviewReportBuilder` |
| 环境 | `EnvListService`、`WingetService` |
| 备份 | `RuleManager`、`SnapshotManager`、`BackupService`、`RestoreService`、`DirectoryScanner`、`ConfigService` |
| 工程化 | `Directory.Build.props`（TFM 变量化）+ `Directory.Packages.props`（CPM）、`.editorconfig`、CI 工作流、`Build.bat` 的产物防伪三防线（强删校验 / WebView2 缓存清理 / STALE 检测） |

### ⚠️ UI 层：用户判定两页可参考，其余重做

用户明确：**原项目"本机预览"与"游戏管理"这两个页面还可以，其它凌乱不堪。**

| 页面 | 文件 | 规模 | 处置 |
|---|---|---|---|
| 本机概览 | `src/SystemToolkit.UI.Blazor/Components/Pages/OverviewPage.razor` | 20 KB | 🟡 **布局与交互参考**（UI 技术栈已定 WPF，razor 无法直接复用，重写时对照其布局与交互） |
| 游戏管理 | `src/SystemToolkit.UI.Blazor/Components/Pages/GameManagerPage.razor` | 37 KB | 🟡 **布局与交互参考**（同上） |

> 🔴 ADR-001 已定 **WPF**：这两页是 Blazor 实现，**代码不可直接复用**，仅作为布局与交互设计的参照物。

其余 UI（8 个 razor 页 + 25 个 XAML 视图）一律重做，仅作参考：
`MusicPage.razor`(169KB) / `ThemeEditorPage.razor`(55KB) / `NetManagerPage.razor`(51KB) / `EnvManagerPage.razor`(48KB) / `FileBackupPage.razor`(35KB) / `FileTransferPage.razor`(34KB) / `SettingsPage.razor`(12KB) / `LogsPage.razor`(4KB)

### ❌ 一律不复用

- `Wpf.Ui` 依赖（隐式样式渗透自家 ControlTemplate，导致控件塌缩）
- `app.manifest` 的 `requireAdministrator`（与新的权限模型相反）
- BlazorWebView 宿主与 `BlazorPilotStaticAssets` 静态资源管线
- 单个超长 UI 文件的写法（`MusicPage.razor` 169KB 是反模式，本项目文件上限见 [01 分册](Docs/40-开发规范/01-编码与命名规范.md#三文件组织)）

### 📖 仅作设计输入

- `Tokens.xaml`（148 key）、`tokens.css`（156 key）—— 只取规范，不取代码
- `docs/guides/UI-DESIGN-ANTHROPIC.md` —— 设计原则

---

## 九、文档纪律

- **文档分工**（`Docs/` 于 2026-09-13 重排为 9 个主题桶，**文件名未改**）：
  - `README.md` —— 功能与架构概览
  - `AGENTS.md` —— 硬约束速览（本文件，**不重复写细节**）
  - `Docs/README.md` —— **文档总导航**（最短阅读路径 + 旧→新路径对照表）
  - `Docs/00-入口/` —— `PROJECT-OVERVIEW.md`（**新 Agent 首选**）· `00-项目总控.md` · `DOCS-INVENTORY.md`
  - `Docs/40-开发规范/` —— **开发规范唯一权威来源**（六分册 + AI 审查提示词 + `TASKS.md`）
  - `Docs/30-模块设计/` —— 模块详细设计 01~10
  - `Docs/10-产品与架构/` · `Docs/20-专题设计/` —— 上游（需求/架构/安全）与横向（UI/日志/协议）设计
  - `Docs/50-决策/`（ADR）· `Docs/60-审查/`（报告）· `Docs/70-原型与提示词/`（mockups）· `Docs/99-归档/`（被取代者，**只进不出**）
  - 🔴 文档文件名里的数字前缀是**稳定标识**，被代码与跨文档引用，**改名前先 grep**。
- 🔴 **文档不得漂移**：写"已实现"前必须 grep 实码核实。旧工程曾出现文档声称 `BlazorTheme` 已持久化、实际代码并未实现的事故。
- 🟠 文档与代码同步更新，顺手清理陈旧信息。
- 🟠 设计文档未定稿前，任何修订需与用户确认。
- 🟠 改动影响对外行为时，同步清单见 [04 分册第三节](Docs/40-开发规范/04-文件与目录规范.md#三修改文件规范)。
- 🟡 审查与待办项先查索引文档，避免重复劳动。

---

## 十、目录纪律

- 🟠 陌生目录先确认归属再操作（可能有其他 Agent 的工程）。
- 🟠 `tools/` 下的临时脚本不入库；`.gitignore` 用精确文件名，不用宽泛通配符。
- 🟠 GitHub push 仅在用户明确要求时执行。
- 🟠 不得递归删除 `Desktop` / `Downloads` / `Documents` 等个人目录；清理类操作先出报告、经确认后再执行。

---

## 十一、验收清单（每个里程碑）

- [ ] Release 构建零警告零错误
- [ ] 全量测试通过（含架构守卫）
- [ ] 三态 UI 齐备（正常 / 空 / 失败）
- [ ] 双主题视觉均达标（Claude.Light / Nvidia.Dark 各自通过 TokenKeys 全覆盖与对比度检查，见 ADR-005）
- [ ] 危险操作四步齐全 + 二次确认
- [ ] 特权操作全部经 Elevated Helper
- [ ] 日志含 CorrelationId，可追溯
- [x] 设计令牌无新增裸值（守卫棘轮只降不升——`UiTokenRatchetTests`，2026-09-05 起强制）
- [ ] 文档与实现一致
