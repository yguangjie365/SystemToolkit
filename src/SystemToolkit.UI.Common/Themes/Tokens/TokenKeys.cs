using System.Collections.Generic;
using System.Reflection;

namespace SystemToolkit.UI.Common.Themes.Tokens;

/// <summary>
/// 令牌契约（04-UI设计规范 §四：key 全集，无值）。
/// 主题包必须覆盖 <see cref="AllKeys"/> 全部 key，缺一即被 TokenKeysCoverageTests 拦截。
///
/// <para><b>分层（2026-09-08 重构）</b>——设计令牌与组件样式此前混在同一个 <c>AllKeys</c> 里，
/// 导致「新增一个基础令牌要手工登记两次」且层级不清。现拆为：</para>
/// <list type="number">
/// <item><see cref="BaseTokens"/>：设计令牌（颜色/画刷/字体/字号/圆角/间距），
/// 由反射从下列嵌套类的 <c>public const string</c> 自动收集——**新增令牌只要加 const，无需再登记**。</item>
/// <item><see cref="ComponentStyles"/>：组件样式与控件模板 key（定义在 XAML 资源中，无法反射，显式维护）。
/// 其中若干由具体页面提升为全局（NetManagerView / DriverManagerView / FileTransferView，2026-09-06），
/// 因其复用次数 ≥2 符合"复合模板抽象"纪律——它们属于组件层，不应与基础令牌混在一起。</item>
/// </list>
/// <see cref="AllKeys"/> = 两者之和，保持对既有校验的兼容（key 名与取值全部不变，XAML 零改动）。
/// </summary>
public static class TokenKeys
{
    public static class Colors
    {
        public const string Color_Background = "Color_Background";
        public const string Color_SurfaceAlt = "Color_SurfaceAlt";
        public const string Color_Surface = "Color_Surface";
        public const string Color_Border = "Color_Border";
        public const string Color_Track = "Color_Track";
        public const string Color_TextPrimary = "Color_TextPrimary";
        public const string Color_TextSecondary = "Color_TextSecondary";
        public const string Color_TextMuted = "Color_TextMuted";
        public const string Color_Accent = "Color_Accent";
        public const string Color_AccentHover = "Color_AccentHover";
        public const string Color_AccentPressed = "Color_AccentPressed";
        public const string Color_Success = "Color_Success";
        public const string Color_Warning = "Color_Warning";
        public const string Color_Danger = "Color_Danger";
        public const string Color_SegmentWarn = "Color_SegmentWarn";
        public const string Color_DarkSurface = "Color_DarkSurface";

        // ── 音乐完整播放器（MUSIC-7 视觉，2026-09-08 参考网易云全屏播放器）──
        public const string Color_PlayerBgFrom = "Color_PlayerBgFrom";
        public const string Color_PlayerBgTo = "Color_PlayerBgTo";
        public const string Color_DiscGroove = "Color_DiscGroove";
        public const string Brush_DiscGroove = "Brush_DiscGroove";
        public const string Color_OnDark = "Color_OnDark";
        // ── 状态语义（2026-09-08 阶段 2 补齐）：与 Success/Warning/Danger 同级，
        //    均遵循 M-UI-2「语义色走 700 档」决策（白底对比度 ≥4.5）
        public const string Color_Info = "Color_Info";
        public const string Color_Disabled = "Color_Disabled";
        public const string Color_Focus = "Color_Focus";
        public const string Color_Selection = "Color_Selection";
    }

    public static class Brushes
    {
        public const string Brush_Background = "Brush_Background";
        public const string Brush_SurfaceAlt = "Brush_SurfaceAlt";
        public const string Brush_Surface = "Brush_Surface";
        public const string Brush_Border = "Brush_Border";
        public const string Brush_Track = "Brush_Track";
        public const string Brush_TextPrimary = "Brush_TextPrimary";
        public const string Brush_TextSecondary = "Brush_TextSecondary";
        public const string Brush_TextMuted = "Brush_TextMuted";
        public const string Brush_Accent = "Brush_Accent";
        public const string Brush_AccentHover = "Brush_AccentHover";
        public const string Brush_AccentPressed = "Brush_AccentPressed";
        public const string Brush_Success = "Brush_Success";
        public const string Brush_Warning = "Brush_Warning";
        public const string Brush_SegmentWarn = "Brush_SegmentWarn";
        public const string Brush_Danger = "Brush_Danger";
        public const string Brush_DangerSoft = "Brush_DangerSoft";
        public const string Brush_DangerBorder = "Brush_DangerBorder";
        public const string Brush_DarkSurface = "Brush_DarkSurface";
        public const string Brush_OnDark = "Brush_OnDark";
        public const string Brush_AccentSoft = "Brush_AccentSoft";
        public const string Brush_SuccessSoft = "Brush_SuccessSoft";
        public const string Brush_WarningSoft = "Brush_WarningSoft";
        public const string Brush_NavySoft = "Brush_NavySoft";
        public const string Brush_SuccessBorder = "Brush_SuccessBorder";
        public const string Brush_OnDarkMuted = "Brush_OnDarkMuted";
        public const string Brush_CoverFade = "Brush_CoverFade";
        public const string Brush_CoverOverlay = "Brush_CoverOverlay";
        public const string Brush_SuccessOnDark = "Brush_SuccessOnDark";
        public const string Brush_DangerOnDark = "Brush_DangerOnDark";
        // ── 状态语义画刷（2026-09-08 阶段 2）
        public const string Brush_Info = "Brush_Info";
        public const string Brush_Disabled = "Brush_Disabled";
        public const string Brush_Focus = "Brush_Focus";
        public const string Brush_Selection = "Brush_Selection";

        // ── 音乐完整播放器 ──
        public const string Brush_PlayerVinyl = "Brush_PlayerVinyl";
        public const string Brush_PlayerLabel = "Brush_PlayerLabel";
    }

    public static class Fonts
    {
        public const string Font_Body = "Font_Body";
        public const string Font_Mono = "Font_Mono";
        public const string Font_TitleZh = "Font_TitleZh";
        public const string Font_TitleEn = "Font_TitleEn";
        public const string Font_WeightRegular = "Font_WeightRegular";
        public const string Font_WeightMedium = "Font_WeightMedium";
        public const string Font_WeightBold = "Font_WeightBold";
        public const string Font_LineHeightCompact = "Font_LineHeightCompact";
        public const string Font_LineHeightNormal = "Font_LineHeightNormal";
        public const string Font_LineHeightRelaxed = "Font_LineHeightRelaxed";
    }

    public static class FontSizes
    {
        public const string Font_SizeDisplay = "Font_SizeDisplay";
        public const string Font_SizeMetric = "Font_SizeMetric";
        public const string Font_SizeTitleLg = "Font_SizeTitleLg";
        public const string Font_SizeTitle = "Font_SizeTitle";
        public const string Font_SizeBodyLg = "Font_SizeBodyLg";
        public const string Font_SizeBody = "Font_SizeBody";
        public const string Font_SizeBodySm = "Font_SizeBodySm";
        public const string Font_SizeCaption = "Font_SizeCaption";
        public const string Font_SizeMono = "Font_SizeMono";
        public const string Font_SizeTiny = "Font_SizeTiny";
        public const string Font_SizeMicro = "Font_SizeMicro";
        public const string Font_SizeNano = "Font_SizeNano";
    }

    public static class Radius
    {
        public const string Radius_XS = "Radius_XS";
        public const string Radius_SM = "Radius_SM";
        public const string Radius_MD = "Radius_MD";
        public const string Radius_LG = "Radius_LG";
        public const string Radius_XL = "Radius_XL";
        public const string Radius_XLLg = "Radius_XLLg";
        public const string Radius_Pill = "Radius_Pill";
        public const string Radius_Track = "Radius_Track";
    }

    /// <summary>图标尺寸与描边（2026-09-09 建立，配合 Themes/Icons.xaml 图标体系）。</summary>
    public static class Icons
    {
        public const string Icon_SizeNav = "Icon_SizeNav";
        public const string Icon_SizeSm = "Icon_SizeSm";
        public const string Icon_SizeMd = "Icon_SizeMd";
        public const string Icon_SizeLg = "Icon_SizeLg";
        public const string Icon_Stroke = "Icon_Stroke";
        public const string Slider_VerticalWidth = "Slider_VerticalWidth";
        public const string Slider_VerticalHeight = "Slider_VerticalHeight";
        public const string Slider_HorizontalWidth = "Slider_HorizontalWidth";
    }

    public static class Spacing
    {
        public const string Space_XXS = "Space_XXS";
        public const string Space_XS = "Space_XS";
        public const string Space_SM = "Space_SM";
        public const string Space_MD = "Space_MD";
        public const string Space_LG = "Space_LG";
    }

    /// <summary>
    /// 设计令牌清单（第 1–2 层）：反射自动收集上列嵌套类的全部 <c>public const string</c>。
    /// 🔴 新增令牌只需在对应分组加 const，**不再需要手工登记到清单**——
    /// 此前「定义一次 + 再登记一次」的重复维护是失同步的主要来源。
    /// </summary>
    public static readonly IReadOnlyList<string> BaseTokens = CollectBaseTokens();

    /// <summary>
    /// 组件样式 / 控件模板 key（第 3–4 层）：定义在 XAML 资源中，反射不可达，故显式维护。
    /// 这些是 Style 与 ControlTemplate 的 x:Key，不是设计令牌——与基础令牌分列，避免层级混淆。
    /// </summary>
    public static readonly IReadOnlyList<string> ComponentStyles =
    [
        // ── 通用控件与组件 ──
        "CardBorder",
        "CardBorderInteractive",
        "MonoText",
        "SectionLabel",
        "PrimaryButton",
        "SecondaryButton",
        "LiveBadge",
        "ListItemRowBaseStyle",
        "ListItemRowStyle",
        "ListItemRowTallStyle",
        "SegmentedRadioItem",
        "ToolkitContextMenu",
        "ToolkitMenuItem",
        "ToolkitMenuItemDanger",
        "ToolkitMenuSeparator",
        // ── 自 NetManagerView 提升的全局样式（2026-09-06） ──
        "FilterChip",
        "SectionTab",
        "RowActionButton",
        "RowDangerButton",
        "RowPrimaryButton",
        "CardTitle",
        "KvLabel",
        "KvValue",
        "FieldInput",
        "FieldError",
        "CardButton",
        "ToolCard",
        "ThemedComboBox",
        "ThemedCheckBox",
        "ThemedRadioButton",
        "ThemedToggleButton",
        // ── 自 DriverManagerView 提升的全局样式（2026-09-06） ──
        "CompactActionButton",
        "CompactDangerButton",
        "ListHeaderStyle",
        "StateBadgeBorder",
        "StateBadgeText",
        "ThemedExpander",
        // ── 自 FileTransferView 提升的全局样式（2026-09-06） ──
        "ListLog",
    ];

    /// <summary>全量 key 清单（主题包覆盖校验的唯一数据源）= 基础令牌 + 组件样式。</summary>
    public static readonly IReadOnlyList<string> AllKeys = [.. BaseTokens, .. ComponentStyles];

    private static IReadOnlyList<string> CollectBaseTokens()
    {
        var keys = new List<string>();
        foreach (Type nested in typeof(TokenKeys).GetNestedTypes(BindingFlags.Public))
        {
            foreach (FieldInfo field in nested.GetFields(BindingFlags.Public | BindingFlags.Static))
            {
                if (field.IsLiteral && field.FieldType == typeof(string)
                    && field.GetRawConstantValue() is string value)
                {
                    keys.Add(value);
                }
            }
        }

        return keys;
    }
}
