using System.Globalization;
using SystemToolkit.UI.Common.Themes.Tokens;

namespace SystemToolkit.Tests.Architecture;

/// <summary>
/// 主题对比度守卫（**03 测试规范 §三③ 的落地**；ADR-005「两包各自独立达标」的机器约束）。
/// <para>
/// <b>为什么现在才有</b>：03 §三③ 从 2026-09-05 起就写着 <c>TokenGuard</c> 必须做「对比度自动断言
/// （正文 ≥4.5:1、次要 ≥3:1、边框与选中态 ≥3:1），含深色块上的文字」，但直到 2026-09-15
/// 全仓 grep <c>Contrast</c>/<c>Luminance</c>/<c>WCAG</c> 在 <c>tests/</c> 下**零命中**——
/// 该条纪律从未落地，深色包（ADR-005 要求「单独达标」）实际只靠人工实算与注释自证。
/// 本守卫把它变成构建期机器约束：改任一主题包的色值 → 两包逐条重算 → 低于阈值即红。
/// </para>
/// <para>
/// <b>判据（03 §三③ / AGENTS §四 / ADR-005 §三，三处口径一致）</b>：
/// 正文 ≥ 4.5:1；次要文字 ≥ 3:1；边框 / 描边 / 选中指示 / 非文字控件 ≥ 3:1；
/// 深色内容容器（<c>Color_DarkSurface</c>）上的文字单独达标。
/// 公式为 WCAG 2.1 相对亮度（sRGB 线性化 + 0.2126/0.7152/0.0722），
/// 口径已与 04 规范 §3.1 既有记录值逐个对齐（浅包 <c>DangerSoft</c> 底 5.51、
/// <c>#E5E7EB</c> on 白 1.24、<c>Selection</c> 1.13；深包 <c>Brush_Shadow</c> on Surface 3.03、
/// <c>OnAccent</c> on Accent 8.71、<c>Track</c> on Surface 3.51——全部逐位复现）。
/// </para>
/// <para>
/// <b>配对表是显式的、逐条写清用途的</b>：不做「遍历全部颜色两两组合」（那会产出上千条无意义配对，
/// 且无法区分"文字压底"与"装饰色块相邻"）。表里每条都对应仓库里真实存在的叠放关系，
/// 半透明底（如 <c>Brush_DangerSoft</c>）按 alpha 合成后再比。
/// </para>
/// <para>
/// <b>低于阈值的值一律逐条登记（<see cref="Exemptions"/>），不允许宽泛跳过</b>：
/// 每条必须写明「哪个包 + 哪个键对 + 记录值 + 引用 04 规范 / ADR-005 / 审查编号的理由」，
/// 且必须**仍然低于阈值**（一旦有人改到达标，<see cref="ThemeContrastGuard_Exemptions_MustStillFailAndMatchRecordedValue"/>
/// 会红并要求删除该条）——即白名单只减不增，不存在"僵尸豁免"。
/// </para>
/// <para>
/// 反向验证（03 §4.1）：本文件内的样本注样自检见
/// <see cref="ThemeContrastGuard_DetectorFunction_SampleInjection_ReverseVerification"/>
/// （达标记样=绿 / 低于阈值样本=红）；落盘时另做了三轮真实改值演示并逐轮记下"只红了哪几条"：
/// ① 深包 <c>Color_TextSecondary</c> 达标值改坏 → 只红本类主用例（三行未达标清单，含包名与实测值）；
/// ② 浅包 <c>Color_TextMuted</c> 改坏 → 同样只红主用例（证明两包各自独立判定，未互相掩盖）；
/// ③ 把已登记的深包 <c>Color_Selection</c> 改到 3:1 以上 → 红在例外自检用例
/// （"已达标，请从 Exemptions 删除本条"）。三轮均恢复原值后复绿，两包 XAML 的 <c>git diff</c> 为空。
/// 配对表与例外登记的取舍依据（用户 2026-09-15 裁定：浅色包逐条登记、深色包 3 处保留原值）
/// 见各条目注释与 04 规范 §三/§十。
/// <para>
/// <b>2026-09-15 深色主题加固后的状态</b>：深包危险色族曾如实登记为 ⚠️ 未裁定的 3 条
/// （基色作小号文字 3.78 / 基色压危险按钮 Soft 底 3.47 / hover 实色底上的 OnDark 4.22）
/// **已全部整改达标并删除**——修复路线是「改样式接线」而非动基色：文字统一走 §3.1 既有语义键
/// <c>Brush_DangerText</c>，hover 实色底新建语义键 <c>Brush_OnDanger</c>；两包实测见配对表 Role。
/// 基色的断言随之归位到非文字档（3:1）——其"不再作文字"由 src/ 全仓 grep 零命中钉住。
/// </para>
/// </summary>
public class ThemeContrastGuardTests
{
    /// <summary>正文阈值（WCAG AA 正常字号文本）。</summary>
    private const double TextMin = 4.5;

    /// <summary>次要文字阈值（03 §三③「次要 ≥3:1」；对应 §3.1 的 TextMuted 档）。</summary>
    private const double SecondaryTextMin = 3.0;

    /// <summary>非文字阈值（边框/描边/选中指示/非文字控件，WCAG 1.4.11 量级）。</summary>
    private const double NonTextMin = 3.0;

    /// <summary>浅色包文件名（登记与定位的稳定标识；改名会同时打断本守卫与 DesignSpecTokenSyncTests）。</summary>
    private const string LightPack = "Claude.Light.xaml";

    /// <summary>深色包文件名。</summary>
    private const string DarkPack = "Nvidia.Dark.xaml";

    /// <summary>记录值允许的浮点/取整误差（超出即视为"值被改过"，要求复核记录）。</summary>
    private const double RecordTolerance = 0.02;

    // ────────────────────────────────────────────────────────────────
    // 配对表：两包各自逐条判定。Min 取该键对**在最严格的实际用途**上的阈值——
    // 例如 Brush_Danger 只作图标/边框（3:1 足够）时按 3:1 锁；
    // 同一枚颜色被当小号文字用的场合（FieldError / 危险菜单项 / 危险按钮常态字）则必须走
    // 语义键 Brush_DangerText 并按下限 4.5 锁——**不靠调低阈值掩盖深包的不足**。
    // 🔴 2026-09-15 深色主题加固后，全仓已无「危险基色直接作文字」的接线（grep 证据见
    // Brush_Danger 那条配对的 Role），故基色的断言归位到非文字档，文字档由语义键承担。
    // ────────────────────────────────────────────────────────────────

    private static readonly Pair[] Pairs =
    [
        // ══ 正文 / 语义文字（≥4.5） ══
        new(TokenKeys.Brushes.Brush_TextPrimary, TokenKeys.Brushes.Brush_Background, TextMin, "页面底上的正文（标题、列表主文字）"),
        new(TokenKeys.Brushes.Brush_TextPrimary, TokenKeys.Brushes.Brush_Surface, TextMin, "卡片底上的正文"),
        new(TokenKeys.Brushes.Brush_TextPrimary, TokenKeys.Brushes.Brush_SurfaceAlt, TextMin, "次级表面上的正文（导航条目、行 hover 底）"),
        new(TokenKeys.Brushes.Brush_TextSecondary, TokenKeys.Brushes.Brush_Background, TextMin, "页面底上的次级文字（次级按钮文字、说明）"),
        new(TokenKeys.Brushes.Brush_TextSecondary, TokenKeys.Brushes.Brush_Surface, TextMin, "卡片底上的次级文字"),
        new(TokenKeys.Brushes.Brush_TextSecondary, TokenKeys.Brushes.Brush_SurfaceAlt, TextMin, "次级表面上的次级文字"),
        new(TokenKeys.Brushes.Brush_Accent, TokenKeys.Brushes.Brush_Surface, TextMin, "强调色作文字/图标（全仓 22 处 Foreground 直引；SecondaryButton hover）"),
        new(TokenKeys.Brushes.Brush_Accent, TokenKeys.Brushes.Brush_Background, TextMin, "强调色文字压页面底（导航选中、卡片外说明）"),
        new(TokenKeys.Brushes.Brush_Info, TokenKeys.Brushes.Brush_Surface, TextMin, "信息提示文字（Brush_Info 作 Foreground）"),
        new(TokenKeys.Brushes.Brush_Warning, TokenKeys.Brushes.Brush_Surface, TextMin, "警告文字（LanScanPanel / SplitRoutePanel / 音乐页等 6 处直引）"),
        new(TokenKeys.Brushes.Brush_Warning, TokenKeys.Brushes.Brush_SurfaceAlt, TextMin, "次级表面上的警告文字（深包 StateBadgeText 10px 就压在 SurfaceAlt 上）"),
        new(TokenKeys.Brushes.Brush_SuccessText, TokenKeys.Brushes.Brush_Surface, TextMin, "卡片底小号成功文字（§3.1 语义键，11 处消费）"),
        new(TokenKeys.Brushes.Brush_DangerText, TokenKeys.Brushes.Brush_Surface, TextMin, "卡片底小号危险文字（§3.1 语义键；2026-09-15 起 FieldError / 危险按钮常态 / 危险菜单项 / 日志错误行统一走它）"),
        new(TokenKeys.Brushes.Brush_DangerText, TokenKeys.Brushes.Brush_SurfaceAlt, TextMin, "次级表面上的危险文字（ToolkitMenuItemDanger 的 hover 底为 Brush_SurfaceAlt）"),
        new(TokenKeys.Brushes.Brush_DangerText, TokenKeys.Brushes.Brush_Surface, TextMin, "危险按钮常态：Soft 底（12% 危险色合成）上的危险色文字", TokenKeys.Brushes.Brush_DangerSoft),
        new(TokenKeys.Brushes.Brush_OnDanger, TokenKeys.Brushes.Brush_Danger, TextMin, "危险按钮 hover：实色危险底上的前景（RowDangerButton / CompactDangerButton；原沿用 Brush_OnDark 深包仅 4.22，2026-09-15 建语义键）"),
        new(TokenKeys.Brushes.Brush_Danger, TokenKeys.Brushes.Brush_Surface, NonTextMin, "危险基色的**非文字**用途：危险边框/图标/分段条填充（2026-09-15 起全仓无该键作文字色——grep `Property=\"Foreground\".*Brush_Danger` 与 `\"Brush_Danger\"` 在 src/ 零命中，仅余 BorderBrush 5 处 + SegBar.ActiveBrush 1 处）"),
        new(TokenKeys.Brushes.Brush_Danger, TokenKeys.Brushes.Brush_Surface, NonTextMin, "危险按钮常态的 1px 基色边框压在 Soft 底上（文字已改走 DangerText，此处保住基色的可辨性断言）", TokenKeys.Brushes.Brush_DangerSoft),
        new(TokenKeys.Brushes.Brush_TextPrimary, TokenKeys.Brushes.Brush_Selection, TextMin, "选中行上的主文字（两种选中底都要可读）"),

        // ── 深色内容容器（日志/命令输出/硬件 ID）：单独达标 ──
        new(TokenKeys.Brushes.Brush_OnDark, TokenKeys.Brushes.Brush_DarkSurface, TextMin, "深色容器主文字"),
        new(TokenKeys.Brushes.Brush_OnDarkMuted, TokenKeys.Brushes.Brush_DarkSurface, TextMin, "深色容器次要文字（深包 = Color_Disabled）"),
        new(TokenKeys.Brushes.Brush_SuccessOnDark, TokenKeys.Brushes.Brush_DarkSurface, TextMin, "深色容器内成功语义文字（游戏页反馈条）"),
        new(TokenKeys.Brushes.Brush_DangerOnDark, TokenKeys.Brushes.Brush_DarkSurface, TextMin, "深色容器内失败语义文字（游戏页反馈条）"),

        // ── accent 实色填充上的前景（主按钮 / 筛选药丸 / 行内主按钮的三态） ──
        new(TokenKeys.Brushes.Brush_OnAccent, TokenKeys.Brushes.Brush_Accent, TextMin, "accent 填充上的前景（PrimaryButton / FilterChip 选中 / RowPrimaryButton）"),
        new(TokenKeys.Brushes.Brush_OnAccent, TokenKeys.Brushes.Brush_AccentHover, TextMin, "同上，hover 态填充"),
        new(TokenKeys.Brushes.Brush_OnAccent, TokenKeys.Brushes.Brush_AccentPressed, TextMin, "同上，pressed 态填充"),

        // ══ 次要文字（≥3.0；AGENTS §四「次要文字 ≥3:1」） ══
        new(TokenKeys.Brushes.Brush_TextMuted, TokenKeys.Brushes.Brush_Background, SecondaryTextMin, "页面底上的次要说明"),
        new(TokenKeys.Brushes.Brush_TextMuted, TokenKeys.Brushes.Brush_Surface, SecondaryTextMin, "卡片底上的次要说明（KvLabel / MonoText / 列表副行）"),
        new(TokenKeys.Brushes.Brush_TextMuted, TokenKeys.Brushes.Brush_SurfaceAlt, SecondaryTextMin, "次级表面上的次要说明（SectionLabel）"),

        // ══ 非文字：边框 / 描边 / 选中指示 / 填充 / 装饰（≥3.0） ══
        new(TokenKeys.Brushes.Brush_Border, TokenKeys.Brushes.Brush_Surface, NonTextMin, "卡片/输入框 1px 边框与滚动条拇指（贴着卡片底）"),
        new(TokenKeys.Brushes.Brush_Border, TokenKeys.Brushes.Brush_Background, NonTextMin, "同上，边框外沿贴着页面底"),
        new(TokenKeys.Brushes.Brush_Track, TokenKeys.Brushes.Brush_Surface, NonTextMin, "行分隔线、进度条轨道（卡片底）"),
        new(TokenKeys.Brushes.Brush_Track, TokenKeys.Brushes.Brush_Background, NonTextMin, "行分隔线、进度条轨道（页面底）"),
        new(TokenKeys.Brushes.Brush_Selection, TokenKeys.Brushes.Brush_Surface, NonTextMin, "选中态底色（可辨性另由 ListItemRowBaseStyle 的 Accent 左缘条承担）"),
        new(TokenKeys.Brushes.Brush_Shadow, TokenKeys.Brushes.Brush_Surface, NonTextMin, "浮层与底面的可辨边界（浅色包靠投影，此键浅色下不承担层次）"),
        new(TokenKeys.Brushes.Brush_Focus, TokenKeys.Brushes.Brush_Surface, NonTextMin, "键盘焦点环"),
        new(TokenKeys.Brushes.Brush_SegmentWarn, TokenKeys.Brushes.Brush_Surface, NonTextMin, "分段条「60-85% 警戒段」填充（数据可视化色段）"),
        new(TokenKeys.Brushes.Brush_Disabled, TokenKeys.Brushes.Brush_Surface, NonTextMin, "禁用态文字/图标（WCAG 对 disabled 元素豁免，仅登记不断言为缺陷）"),
        new(TokenKeys.Brushes.Brush_PlayerLabel, TokenKeys.Colors.Color_PlayerBgFrom, NonTextMin, "黑胶盘心标贴底（程序化绘制的装饰块）"),
        new(TokenKeys.Colors.Brush_DiscGroove, TokenKeys.Colors.Color_PlayerBgFrom, NonTextMin, "黑胶盘面沟纹同心环描边（程序化绘制的装饰纹理）"),
    ];

    // ────────────────────────────────────────────────────────────────
    // 例外登记（逐条：包 + 键对 + 记录值 + 依据）。🔴 只允许"记录在案"的取值进这里，
    // 且每条都必须**仍低于阈值**——改到达标即红，要求删除本条（白名单只减不增）。
    // 本轮（2026-09-15）登记来源：
    //   ① 用户裁定「浅色包逐条白名单、不改浅色视觉」「深色包 3 处保留原值」；
    //   ② 上一条遗留的深包危险色族 3 条已于 2026-09-15 **整改落地后删除**（接线改走语义键
    //      Brush_DangerText / 新建 Brush_OnDanger，两包实测均 ≥4.5，详见配对表 Role 与 04 §3.1）。
    // ────────────────────────────────────────────────────────────────

    private static readonly Exemption[] Exemptions =
    [
        // ── 浅色包：弱描边气质与装饰性取值（04 §3.1 / 主题包注释已有记录） ──
        new(LightPack, TokenKeys.Brushes.Brush_Border, TokenKeys.Brushes.Brush_Surface, NonTextMin, 1.47,
            "04 §3.1 基础色阶：Color_Border `#D1D5DB` 系 2026-09-12 ①A 主题优化定稿值（可辨性提升且保持「浅描边气质」），浅色靠投影+色阶分层而非重描边"),
        new(LightPack, TokenKeys.Brushes.Brush_Border, TokenKeys.Brushes.Brush_Background, NonTextMin, 1.39,
            "04 §3.1 基础色阶：Color_Border `#D1D5DB` 属 2026-09-12 ①A 定稿的浅色弱描边体系（边框外沿贴页面底，与卡片底同源）"),
        new(LightPack, TokenKeys.Brushes.Brush_Track, TokenKeys.Brushes.Brush_Surface, NonTextMin, 1.47,
            "04 §3.1：Color_Track 与 Color_Border 同值（2026-09-12 ①A 同步），行分隔线属弱分隔语义"),
        new(LightPack, TokenKeys.Brushes.Brush_Track, TokenKeys.Brushes.Brush_Background, NonTextMin, 1.39,
            "04 §3.1：Color_Track 与 Color_Border 同值（2026-09-12 ①A 同步），行分隔线属弱分隔语义（此处压页面底）"),
        new(LightPack, TokenKeys.Brushes.Brush_Selection, TokenKeys.Brushes.Brush_Surface, NonTextMin, 1.13,
            "04 §3.1 Color_Selection 栏明标「记录在案的装饰性取值」：选中可辨性由 ListItemRowBaseStyle 的 Accent 左缘条 + 加粗承担（审查 ②A 裁定）"),
        new(LightPack, TokenKeys.Brushes.Brush_Shadow, TokenKeys.Brushes.Brush_Surface, NonTextMin, 1.39,
            "04 §3.1 Color_Shadow 栏 + 主题包注释：浅色下层次主要由投影承担，此键保持与 SurfaceAlt 同值（外观不变），真正的分离职责在深色包同名键"),
        new(LightPack, TokenKeys.Brushes.Brush_SegmentWarn, TokenKeys.Brushes.Brush_Surface, NonTextMin, 2.26,
            "04 §3.1：Color_SegmentWarn 是分段条 60-85% 警戒段专用亮橙（数据可视化色段，不与卡片底构成 UI 边界判定）"),
        new(LightPack, TokenKeys.Brushes.Brush_Disabled, TokenKeys.Brushes.Brush_Surface, NonTextMin, 2.54,
            "04 §3.1 Color_Disabled 栏：WCAG 1.4.3 对 disabled 元素豁免对比度要求（此处仅登记，不视为缺陷）"),

        // ── 深色包：用户 2026-09-15 裁定「保留原值，仅白名单记录」 ──
        new(DarkPack, TokenKeys.Brushes.Brush_Selection, TokenKeys.Brushes.Brush_Surface, NonTextMin, 1.40,
            "审查 2026-09-11 🟠-4 + 主题包注释：Brush_Selection(#243A0D) 单叠 Surface 仅 1.40:1，可辨性由 ListItemRowBaseStyle 的 Accent 左缘条（vs Surface ≈7.2:1）承担"),
        new(DarkPack, TokenKeys.Brushes.Brush_PlayerLabel, TokenKeys.Colors.Color_PlayerBgFrom, NonTextMin, 2.63,
            "04 §3.1 音乐播放器表：Brush_PlayerLabel 是盘心标贴底（接真实封面后废弃的程序化装饰块，不承担文字/边界可辨性）"),
        new(DarkPack, TokenKeys.Colors.Brush_DiscGroove, TokenKeys.Colors.Color_PlayerBgFrom, NonTextMin, 1.64,
            "04 §3.1 音乐播放器表：Color_DiscGroove 是黑胶盘面沟纹同心环描边（程序化装饰纹理，明暗由盘面渐变自身决定）"),
    ];

    // ────────────────────────────────────────────────────────────────
    // 全仓门禁
    // ────────────────────────────────────────────────────────────────

    /// <summary>
    /// 全仓门禁：**每个**主题包（Packs/**/*.xaml，自动发现——新增包不会被静默跳过）
    /// 的每一条配对都必须达标，例外仅限 <see cref="Exemptions"/> 逐条登记的那些。
    /// </summary>
    [Fact]
    public void ThemeContrastGuard_EveryThemePack_EveryPairMeetsItsThreshold()
    {
        var failures = new List<string>();

        foreach (string packPath in PackPaths())
        {
            string packFile = Path.GetFileName(packPath);
            Dictionary<string, string> tokens = ThemeContrastMath.ParseTokens(File.ReadAllText(packPath));

            foreach (Pair pair in Pairs)
            {
                string? missing = DescribeMissingToken(pair, tokens);
                if (missing is not null)
                {
                    failures.Add($"{packFile}｜{pair.Describe()} → {missing}");
                    continue;
                }

                double ratio = MeasurePair(pair, tokens);
                if (ratio >= pair.Min || IsExempted(packFile, pair))
                {
                    continue;
                }

                failures.Add(string.Format(CultureInfo.InvariantCulture,
                    "{0}｜{1} = {2:F2}:1（要求 ≥{3:F1}:1）— {4}",
                    packFile, pair.Describe(), ratio, pair.Min, pair.Role));
            }
        }

        Assert.True(failures.Count == 0, """
            主题包对比度未达标（03 §三③ / ADR-005：两包各自独立达标）。
            处置二选一，**不要**调低阈值、不要给守卫加通配跳过：
            · 多数情况应改色值——两包同步改，并按 03 §六 同步 04 规范 §3.1 与文档；
            · 确属"记录在案的装饰性取值"（如 §3.1 明标的 Color_Selection）→ 在 Exemptions 里
              逐条登记「包 + 键对 + 记录值 + 依据」，一条只覆盖一个键对。

            未达标清单：
            """ + string.Join(Environment.NewLine, failures));
    }

    /// <summary>
    /// 例外登记自检（白名单只减不增）：每条必须①指向真实存在的包与配对；
    /// ②**仍然低于阈值**（改到达标即红，要求删除该条）；③记录值与实测一致（值被改过即红，要求复核）。
    /// 没有这道闸，白名单会退化成"万能跳过"——那正是 03 §4.1「未经反向验证的守门等于没守门」要防的。
    /// </summary>
    [Fact]
    public void ThemeContrastGuard_Exemptions_MustStillFailAndMatchRecordedValue()
    {
        var packs = PackPaths()
            .ToDictionary(p => Path.GetFileName(p) ?? p, p => ThemeContrastMath.ParseTokens(File.ReadAllText(p)), StringComparer.Ordinal);
        var failures = new List<string>();

        foreach (Exemption exemption in Exemptions)
        {
            string label = $"{exemption.PackFile}｜{exemption.Describe()}";

            if (!packs.TryGetValue(exemption.PackFile, out Dictionary<string, string>? tokens))
            {
                failures.Add($"{label} → 登记的主题包不存在（包被改名/移除？）");
                continue;
            }

            Pair? pair = Array.Find(Pairs, p =>
                p.Fg == exemption.Fg && p.Bg == exemption.Bg && p.Over == exemption.Over);
            if (pair is null)
            {
                failures.Add($"{label} → 例外条目找不到对应配对（配对表已改？）");
                continue;
            }

            if (Math.Abs(pair.Min - exemption.Min) > 0.001)
            {
                failures.Add($"{label} → 记录的阈值 {exemption.Min:F1} 与配对表的 {pair.Min:F1} 不一致（配对表改了阈值，例外未同步）");
            }

            string? missing = DescribeMissingToken(pair, tokens);
            if (missing is not null)
            {
                failures.Add($"{label} → {missing}");
                continue;
            }

            double ratio = MeasurePair(pair, tokens);
            if (ratio >= exemption.Min)
            {
                failures.Add(string.Format(CultureInfo.InvariantCulture,
                    "{0} → 已达标（实测 {1:F2}:1 ≥ {2:F1}:1），请从 Exemptions 删除本条",
                    label, ratio, exemption.Min));
            }
            else if (Math.Abs(ratio - exemption.Measured) > RecordTolerance)
            {
                failures.Add(string.Format(CultureInfo.InvariantCulture,
                    "{0} → 值与记录不符（记录 {1:F2}:1，实测 {2:F2}:1，容差 ±{3:F2}）——色值被改过，请复核后更新记录",
                    label, exemption.Measured, ratio, RecordTolerance));
            }

            if (!ReasonCitesRecord(exemption.Reason))
            {
                failures.Add($"{label} → 理由未引用记录位置（须含 §/ADR/WCAG/审查 之一，写明依据出处）：{exemption.Reason}");
            }
        }

        Assert.True(failures.Count == 0, """
            例外登记（Exemptions）不合格——白名单只减不增，且每条都要有出处：

            """ + string.Join(Environment.NewLine, failures));
    }

    /// <summary>
    /// 扫描面自检：防止"表空了/包没了"造成的**假绿**——
    /// 配对表与主题包都必须真的有内容被测，且例外登记不得指向未被发现的包。
    /// </summary>
    [Fact]
    public void ThemeContrastGuard_ScanCoverage_NoPackOrPairSilentlySkipped()
    {
        IReadOnlyList<string> packs = PackPaths();
        var failures = new List<string>();

        if (packs.Count < 2)
        {
            failures.Add($"只发现 {packs.Count} 个主题包（ADR-005 要求 Light + Dark 两包各自达标）");
        }

        var discovered = new HashSet<string>(packs.Select(p => Path.GetFileName(p) ?? p), StringComparer.Ordinal);
        foreach (string declared in new[] { LightPack, DarkPack }.Concat(Exemptions.Select(e => e.PackFile)).Distinct(StringComparer.Ordinal))
        {
            if (!discovered.Contains(declared))
            {
                failures.Add($"本守卫登记的「{declared}」不在 Packs 目录下（改名或移走？）");
            }
        }

        foreach (string packPath in packs)
        {
            Dictionary<string, string> tokens = ThemeContrastMath.ParseTokens(File.ReadAllText(packPath));
            if (tokens.Count < 55)
            {
                failures.Add($"{Path.GetFileName(packPath)} 只解析出 {tokens.Count} 个颜色令牌（解析器失效？）");
            }
        }

        if (Pairs.Length < 30)
        {
            failures.Add($"配对表只剩 {Pairs.Length} 条（低于 30 条即视为守卫被削薄）");
        }

        Assert.True(failures.Count == 0, "主题对比度守卫的扫描面不完整：\n" + string.Join(Environment.NewLine, failures));
    }

    // ────────────────────────────────────────────────────────────────
    // 反向验证自检（03 §4.1：未经反向验证的守门等于没守门）
    // ────────────────────────────────────────────────────────────────

    /// <summary>
    /// 检测函数自检：样本注入 → 阈值判定必须真的翻转（达标记样绿 / 低于阈值样本红），
    /// 且令牌解析、半透明合成两条前置逻辑都被覆盖。
    /// </summary>
    [Theory]
    // 阈值边界：4.48 应判红、4.54 应判绿（守的是 4.5 这条线本身，不是"大概"）
    [InlineData("#777777", "#FFFFFF", 4.5, false)]
    [InlineData("#767676", "#FFFFFF", 4.5, true)]
    // 两端样本（纯黑/纯白 = 21:1；同色 = 1:1）
    [InlineData("#FFFFFF", "#000000", 4.5, true)]
    [InlineData("#1A1A1A", "#1A1A1A", 3.0, false)]
    // 深色包真实取值样本：Accent #76B900 on Surface 7.22（达标档）与 Danger #E52020 on Surface 3.78
    // （2026-09-15 起该键对按**非文字** 3:1 断言，此处仅借这对真实取值当阈值边界样本）
    [InlineData("#76B900", "#1A1A1A", 4.5, true)]
    [InlineData("#E52020", "#1A1A1A", 4.5, false)]
    public void ThemeContrastGuard_DetectorFunction_SampleInjection_ReverseVerification(
        string fgHex, string bgHex, double min, bool expectPass)
    {
        double ratio = ThemeContrastMath.ContrastRatio(fgHex, bgHex);

        Assert.Equal(expectPass, ratio >= min);
    }

    /// <summary>
    /// 半透明底合成自检：浅包 <c>Brush_DangerSoft(#1FEF4444)</c> 压白底后，
    /// <c>Color_Danger</c> 在其上的比值必须复现 04 §3.1 记录的 **5.51**
    /// （该数字是 M-UI-2 的既成结论，本守卫的合成口径以它为锚点）。
    /// </summary>
    [Fact]
    public void ThemeContrastGuard_CompositeFunction_ReproducesRecordedDangerSoftRatio()
    {
        string composited = ThemeContrastMath.Composite("#1FEF4444", "#ffffff");
        double ratio = ThemeContrastMath.ContrastRatio("#B91C1C", composited);

        Assert.Equal(5.51, ratio, 2);
    }

    /// <summary>
    /// 解析器自检：三种真实写法（<c>&lt;Color&gt;</c> 元素、SolidColorBrush 显式 hex、SolidColorBrush 别名）
    /// 都要能解析成有效 hex；别名必须跟随到被引用的 Color_*。
    /// </summary>
    [Fact]
    public void ThemeContrastGuard_TokenParser_HandlesElementLiteralAndAliasForms()
    {
        Dictionary<string, string> tokens = ThemeContrastMath.ParseTokens("""
            <Color x:Key="Color_Surface">#1A1A1A</Color>
            <SolidColorBrush x:Key="Brush_Surface" Color="{StaticResource Color_Surface}"/>
            <SolidColorBrush x:Key="Brush_Track" Color="#3D5E5E5E"/>
            <LinearGradientBrush x:Key="Brush_CoverFade" StartPoint="0,0" EndPoint="0,1">
                <GradientStop Color="#00000000" Offset="0"/>
            </LinearGradientBrush>
            """);

        Assert.Equal("#1A1A1A", tokens["Color_Surface"]);
        Assert.Equal("#1A1A1A", tokens["Brush_Surface"]);
        Assert.Equal("#3D5E5E5E", tokens["Brush_Track"]);
        Assert.False(tokens.ContainsKey("Brush_CoverFade"));
    }

    // ────────────────────────────────────────────────────────────────
    // 内部实现
    // ────────────────────────────────────────────────────────────────

    /// <summary>主题包路径（Packs 目录下全部 *.xaml，递归；新增包自动纳入）。</summary>
    private static IReadOnlyList<string> PackPaths()
    {
        string packsDir = Path.Combine(RepoRoot(), "src/SystemToolkit.UI.Common/Themes/Packs");
        return Directory.EnumerateFiles(packsDir, "*.xaml", SearchOption.AllDirectories)
            .OrderBy(p => p, StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>键对是否已被例外登记豁免（逐条精确匹配：包 + 前景 + 背景 + 合成层）。</summary>
    private static bool IsExempted(string packFile, Pair pair)
        => Array.Exists(Exemptions, e => e.PackFile == packFile && e.Fg == pair.Fg && e.Bg == pair.Bg && e.Over == pair.Over);

    /// <summary>解析键对实际参与比较的两个颜色（背景为半透明时先合成到 <see cref="Pair.Bg"/>）。</summary>
    private static double MeasurePair(Pair pair, IReadOnlyDictionary<string, string> tokens)
    {
        string background = tokens[pair.Bg];
        if (pair.Over is not null)
        {
            background = ThemeContrastMath.Composite(tokens[pair.Over], background);
        }

        return ThemeContrastMath.ContrastRatio(tokens[pair.Fg], background);
    }

    /// <summary>令牌缺失说明（守卫不得静默跳过：缺令牌即是红）。</summary>
    private static string? DescribeMissingToken(Pair pair, IReadOnlyDictionary<string, string> tokens)
    {
        if (!tokens.ContainsKey(pair.Fg))
        {
            return $"前景令牌 {pair.Fg} 在包内不存在（改名/删除？）";
        }

        if (!tokens.ContainsKey(pair.Bg))
        {
            return $"背景令牌 {pair.Bg} 在包内不存在（改名/删除？）";
        }

        if (pair.Over is not null && !tokens.ContainsKey(pair.Over))
        {
            return $"合成层令牌 {pair.Over} 在包内不存在（改名/删除？）";
        }

        return null;
    }

    /// <summary>例外理由必须指向记录位置（04 规范 §/ADR/WCAG/审查 编号），防"万能跳过"式理由。</summary>
    private static bool ReasonCitesRecord(string reason)
        => reason.Contains('§', StringComparison.Ordinal)
            || reason.Contains("ADR-", StringComparison.Ordinal)
            || reason.Contains("WCAG", StringComparison.Ordinal)
            || reason.Contains("审查", StringComparison.Ordinal);

    private static string RepoRoot()
    {
        DirectoryInfo? dir = new(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "SystemToolkit.sln")))
        {
            dir = dir.Parent;
        }

        return dir?.FullName ?? throw new InvalidOperationException("未定位到仓库根（SystemToolkit.sln）");
    }

    /// <summary>一条对比度配对：<paramref name="Fg"/> 压在 <paramref name="Bg"/>（或 <paramref name="Over"/> 合成到 <paramref name="Bg"/> 之上）时，须 ≥ <paramref name="Min"/>。</summary>
    private sealed record Pair(string Fg, string Bg, double Min, string Role, string? Over = null)
    {
        /// <summary>断言消息里的人话标识（键名 + 合成说明）。</summary>
        public string Describe()
            => Over is null ? $"{Fg} on {Bg}" : $"{Fg} on {Over}@{Bg}（合成底）";
    }

    /// <summary>一条例外登记：必须仍低于阈值，且写明依据；改到达标即应删除。</summary>
    private sealed record Exemption(string PackFile, string Fg, string Bg, double Min, double Measured, string Reason, string? Over = null)
    {
        /// <summary>断言消息里的人话标识。</summary>
        public string Describe()
            => Over is null ? $"{Fg} on {Bg}" : $"{Fg} on {Over}@{Bg}（合成底）";
    }
}
