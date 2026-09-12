# DESIGN.md — SystemToolkit · NVIDIA 风格主题（设计参考）

> ⚠️ **【2026-09-13 归档标记】本节首段的裁定说明已过时，正文色板已落地。**
> 原文写「项目裁定：不开发深色模式、单 Light 主题」——**该裁定已被 ADR-005 推翻**
> （2026-09-10 放开双主题，`src/SystemToolkit.UI.Common/Themes/Packs/Nvidia/Nvidia.Dark.xaml` 已在产，
> 运行期切换不重启）。因此「若要作为可发布主题包落地需先推翻裁定」这一前置**已完成**。
> 正文 §1–§7 是 Nvidia.Dark 主题包的取色/排版/组件依据，仍有效；但**具体令牌值以
> `Docs/20-专题设计/04-UI设计规范.md` §3.1 与主题包文件为准**（有 `TokenKeysCoverage` / `DesignSpecTokenSync` 守卫），
> 本文数值仅供溯源，可能与最终值有微调。

> 本文件是 design-md 引入的 NVIDIA 设计系统参考（token 取自内置 `nvidia` 模板，未编造）。
> ⚠️ 项目裁定：**不开发深色模式、单 Light 主题**（AGENTS §四 / ADR-003）。NVIDIA 签名是黑底+绿点缀，属深色——
> 若要作为**可发布主题包**落地，需先推翻"不做深色"裁定并新增 `Packs/Nvidia/Nvidia.Dark.xaml` + 走完整令牌契约
> （TokenKeys + 覆盖测试 + 04 规范表）。当前仅作为**设计探索原型**（见 `Docs/70-原型与提示词/系统主题-Nvidia风格.html`）。

## 1. Visual Theme & Atmosphere
高对比、技术至上的力量感。纯黑 `#000000` + 纯白 `#ffffff` 为基座，NVIDIA 绿 `#76b900` 作**纯点缀**（边框/下划线/active，绝不做大面积填充）。工业、克制、精准——像把 GPU 硬件渲染成像素。

## 2. Color Palette & Roles
- **NVIDIA Green `#76b900`**：签名色，仅用于边框、下划线、CTA 描边、选中指示。
- **True Black `#000000`**：主背景。**Near Black `#1a1a1a`**：深色卡底。**Pure White `#ffffff`**：深底文字/浅底卡。
- **Green Light `#bff230`**：hover/高亮点缀。**Orange `#df6500` / Yellow `#ef9100`**：能量/featured 暖点缀。
- 语义：**Red `#e52020`**（错误/危险）、**Green 500 `#3f8500`**（成功，比品牌绿深）、**Blue `#0046a4`**（信息）。
- 中性：Gray 300 `#a7a7a7`（次要文字）、400 `#898989`、500 `#757575`（占位/脚注）、Border `#5e5e5e`。
- 交互：Link hover `#3860be`（蓝，恒定）、Button hover `#1eaedb`（青）、active `#007fff`、Focus `2px #000`。
- 阴影：`rgba(0,0,0,0.3) 0 0 5px`（唯一阴影值，克制使用）。

## 3. Typography
- 字体：`NVIDIA-EMEA`，回退 `Arial, Helvetica, sans-serif`（项目内用现有 Inter/HarmonyOS 近似其工业无衬线）。
- 层级：Display 36/700/1.25；Section 24/700/1.25；Card Title 20/700/1.25；Body 16/400/1.5；Body Small 15/400/1.67；Button 16/700/1.25；Nav Link 14/700/1.43 **大写**；Micro Label 10/700 大写。
- 原则：700 是一切交互/标题/导航的主字重，400 只留给正文；标题紧(1.25)、正文松(1.5–1.67)；导航大写=硬件规格标签感；不做装饰性字距。

## 4. Components
- **主按钮**：透明底 + `2px solid #76b900` 边框 + 2px 圆角 + 16/700；hover 底 `#1eaedb` 文字白；active `#007fff`。
- **次按钮**：透明 + `1px solid #76b900`。**卡片**：`#fff`/`#1a1a1a` 底、2px 圆角、`rgba(0,0,0,.3) 0 0 5px` 阴影、内边距 16–24；标题下绿色 2px 描边。
- **链接**：深底白字无下划线、浅底黑字 + `2px` 绿下划线；hover 一律 `#3860be`。
- **导航**：黑底、logo 左置、14/700 大写白字、hover 变色不下划线。
- **规格表**：工业网格、交替行底色、粗标签+常规值、关键指标绿色高亮。

## 5. Layout
- 基准 8px；主 padding 8/11/13/16/24/32；section 垂直 48–80。**密度优先**（比一般 SaaS 更紧，技术内容感）。
- 容器最大约 1200；深/浅区块交替（用背景色分隔，不只靠留白）；卡片间距 16–20（目录感而非画廊感）。
- 圆角：默认 **2px**（一切），50% 仅头像。

## 6. Depth
- Flat 无阴影；Subtle `rgba(0,0,0,.3) 0 0 5px`（卡片/模态）；Border `1px #5e5e5e`（分隔）；Green accent `2px #76b900`（active/CTA/选中）；Focus `2px #000`。
- 层次靠**色彩对比**（黑底邻白区、绿边压黑面），非模拟光照；无玻璃拟态/模糊。

## 7. Do & Don't
- Do：绿只作信号不作大填充；按钮默认透明+绿描边；700 主导交互/标题；2px 圆角；深底白字/浅底黑字；导航大写。
- Don't：不用绿做大面积背景填充；不用大圆角/玻璃/模糊；hover 不改成非蓝的色；正文别用 700。

## 8. 落地到 SystemToolkit 的映射（若采纳）
新增主题包 `Packs/Nvidia/Nvidia.Dark.xaml`，令牌映射：
`Color_Background=#000000`、`Color_Surface=#1a1a1a`、`Color_SurfaceAlt=#141414`、`Color_Border=#5e5e5e`、
`Color_TextPrimary=#ffffff`、`Color_TextSecondary=#a7a7a7`、`Color_TextMuted=#757575`、
`Color_Accent=#76b900`、`Color_AccentHover=#1eaedb`、`Color_Danger=#e52020`、`Color_Success=#3f8500`、
`Radius_*` 全线收到 2px、导航字重 700 大写。
> 前提：需先解除"不做深色模式"裁定；且 `TokenKeysCoverage`/`DesignSpecTokenSync`/`UiTokenRatchet` 会要求同步 04 规范表 + 全量色值登记。
