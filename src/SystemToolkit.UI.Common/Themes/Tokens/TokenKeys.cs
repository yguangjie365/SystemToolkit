using System.Collections.Generic;

namespace SystemToolkit.UI.Common.Themes.Tokens;

/// <summary>
/// 令牌契约（04-UI设计规范 §四：key 全集，无值）。
/// 🔴 与 Claude.Light.xaml 的 x:Key 清单人工同步——主题包必须覆盖 <see cref="AllKeys"/> 全部 key，
/// 缺一即被 TokenKeysCoverageTests 拦截。
/// 结构：Colors（颜色）/ Brushes（画刷）/ Fonts（字体族）/ FontSizes（字号）/
/// Radius（圆角现值冻结词汇）/ Spacing（4px 栅格间距）。
/// </summary>
public static class TokenKeys
{
    public static class Colors
    {
        public const string Color_Bg = "Color_Bg";
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
        public const string Color_SegWarn = "Color_SegWarn";
        public const string Color_DarkSurface = "Color_DarkSurface";
        public const string Color_OnDark = "Color_OnDark";
    }

    public static class Brushes
    {
        public const string Brush_Bg = "Brush_Bg";
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
        public const string Brush_SegWarn = "Brush_SegWarn";
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
        public const string Radius_Chip = "Radius_Chip";
        public const string Radius_Control = "Radius_Control";
        public const string Radius_Badge = "Radius_Badge";
        public const string Radius_CardSm = "Radius_CardSm";
        public const string Radius_Card = "Radius_Card";
        public const string Radius_CardLg = "Radius_CardLg";
    }

    public static class Spacing
    {
        public const string Space_Xxs = "Space_Xxs";
        public const string Space_Xs = "Space_Xs";
        public const string Space_Sm = "Space_Sm";
        public const string Space_Md = "Space_Md";
        public const string Space_Lg = "Space_Lg";
    }

    /// <summary>全量 key 清单（主题包覆盖校验的唯一数据源）。</summary>
    public static readonly IReadOnlyList<string> AllKeys =
    [
            "Color_Bg",
            "Color_SurfaceAlt",
            "Color_Surface",
            "Color_Border",
            "Color_Track",
            "Color_TextPrimary",
            "Color_TextSecondary",
            "Color_TextMuted",
            "Color_Accent",
            "Color_AccentHover",
            "Color_AccentPressed",
            "Color_Success",
            "Color_Warning",
            "Color_Danger",
            "Color_SegWarn",
            "Color_DarkSurface",
            "Color_OnDark",
            "Brush_Bg",
            "Brush_SurfaceAlt",
            "Brush_Surface",
            "Brush_Border",
            "Brush_Track",
            "Brush_TextPrimary",
            "Brush_TextSecondary",
            "Brush_TextMuted",
            "Brush_Accent",
            "Brush_AccentHover",
            "Brush_AccentPressed",
            "Brush_Success",
            "Brush_Warning",
            "Brush_SegWarn",
            "Brush_Danger",
            "Brush_DangerSoft",
            "Brush_DangerBorder",
            "Brush_DarkSurface",
            "Brush_OnDark",
            "Brush_AccentSoft",
            "Brush_SuccessSoft",
            "Brush_WarningSoft",
            "Brush_NavySoft",
            "Brush_SuccessBorder",
            "Brush_OnDarkMuted",
            "Brush_CoverFade",
            "Brush_CoverOverlay",
            "Brush_SuccessOnDark",
            "Brush_DangerOnDark",
            "Font_Body",
            "Font_Mono",
            "Font_TitleZh",
            "Font_TitleEn",
            "Font_WeightRegular",
            "Font_WeightMedium",
            "Font_WeightBold",
            "Font_LineHeightCompact",
            "Font_LineHeightNormal",
            "Font_LineHeightRelaxed",
            "Font_SizeDisplay",
            "Font_SizeMetric",
            "Font_SizeTitleLg",
            "Font_SizeTitle",
            "Font_SizeBodyLg",
            "Font_SizeBody",
            "Font_SizeBodySm",
            "Font_SizeCaption",
            "Font_SizeMono",
            "Font_SizeTiny",
            "Font_SizeMicro",
            "Font_SizeNano",
            "Radius_Chip",
            "Radius_Control",
            "Radius_Badge",
            "Radius_CardSm",
            "Radius_Card",
            "Radius_CardLg",
            "Space_Xxs",
            "Space_Xs",
            "Space_Sm",
            "Space_Md",
            "Space_Lg",
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
            // NetManagerView 提升的全局样式（2026-09-06）
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
            // DriverManagerView 提升的全局样式（2026-09-06）
            "CompactActionButton",
            "CompactDangerButton",
            "ListHeaderStyle",
            "StateBadgeBorder",
            "StateBadgeText",
            "ThemedExpander",
            // FileTransferView 提升的全局样式（2026-09-06）
            "ListLog",
    ];
}
