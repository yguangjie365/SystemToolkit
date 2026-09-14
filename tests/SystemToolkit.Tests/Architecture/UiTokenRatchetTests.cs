using System.Text.Json;
using System.Text.RegularExpressions;

namespace SystemToolkit.Tests.Architecture;

/// <summary>
/// UI 令牌棘轮守卫（P1 落地）：XAML 裸值只减不增。
/// 规则：① FontSize / CornerRadius 数字字面量 = 零容忍（已有精确值令牌，直接引用）；
/// ② Margin / Padding / Width / Height / BorderThickness / StrokeThickness 数字字面量 = 按文件基线（UiTokenRatchet/baseline.json），只许减少。
/// 基线在裸值收敛完成后生成；有意新增合法裸值 → 显式下调基线并在变更记录说明。
/// 注意：Width/Height 检测不含 Max/Min 前缀（无词边界），故 MaxHeight 等不视为裸值（审查 S-4 用 MaxHeight 收窄视口）。
/// </summary>
public class UiTokenRatchetTests
{
    /// <summary>
    /// 扫描范围 = <c>src</c> 下<b>全部</b> XAML（动态枚举）− 下方显式排除项。
    /// <para>
    /// 2026-09-14（B10）由静态白名单改为动态枚举。白名单制的致命缺陷是
    /// <b>「新 XAML 忘加名单 = 静默不受检测」</b>：本批逐个核对全仓 XAML 后实测抓到
    /// <b>3 个从未被覆盖</b>的文件（<c>SplitRoutePanel.xaml</c> 已知洞 +
    /// <c>DriverBackupWindow.xaml</c> / <c>MiniPlayerWindow.xaml</c> 两个此前无任何记录者）——
    /// 它们在名单制下永远不会变红，改坏了也没人知道。
    /// 现改为 fail-safe 默认：全扫；要排除必须在此显式登记并写明理由，否则新文件自动进入检测。
    /// </para>
    /// </summary>
    private static readonly (string Path, string Reason)[] ExcludedViews =
    {
        ("src/SystemToolkit.UI.Common/Themes/Packs/Claude/Claude.Light.xaml",
            "令牌**定义端**：本文件即 FontSize/CornerRadius 等令牌的定义处，而棘轮约束的是消费端（页面布局）；"
            + "纳入会自我矛盾（零容忍项若在定义端也零容忍，则无法定义任何令牌）。它已有更严的门禁："
            + "TokenKeys 全量覆盖检查 + ADR-005 对比度纪律"),
        ("src/SystemToolkit.UI.Common/Themes/Packs/Nvidia/Nvidia.Dark.xaml",
            "同上（深色令牌包）"),
    };

    private static readonly string[] ScannedViews = EnumerateScannedViews();

    private static readonly Regex FontSizeLiteral = new(@"FontSize=""\d", RegexOptions.Compiled);
    private static readonly Regex CornerRadiusLiteral = new(@"CornerRadius=""\d", RegexOptions.Compiled);
    private static readonly Regex MarginLiteral = new(@"\bMargin=""\d", RegexOptions.Compiled);
    private static readonly Regex PaddingLiteral = new(@"\bPadding=""\d", RegexOptions.Compiled);
    private static readonly Regex WidthLiteral = new(@"\bWidth=""\d", RegexOptions.Compiled);
    private static readonly Regex HeightLiteral = new(@"\bHeight=""\d", RegexOptions.Compiled);
    private static readonly Regex BorderThicknessLiteral = new(@"\bBorderThickness=""\d", RegexOptions.Compiled);
    private static readonly Regex StrokeThicknessLiteral = new(@"\bStrokeThickness=""\d", RegexOptions.Compiled);

    private static readonly Dictionary<string, Dictionary<string, int>> Baseline =
        JsonSerializer.Deserialize<Dictionary<string, Dictionary<string, int>>>(
            File.ReadAllText(Path.Combine(
                RepoRoot(), "tests/SystemToolkit.Tests/Architecture/UiTokenRatchet/baseline.json")))!;

    [Fact]
    public void TokenRatchet_FontSizeAndCornerRadius_ZeroLiterals()
    {
        var failures = new List<string>();
        foreach (string rel in ScannedViews)
        {
            string text = File.ReadAllText(RepoRoot() + "/" + rel);
            if (FontSizeLiteral.IsMatch(text))
            {
                failures.Add(rel + " 存在数字 FontSize（请引用 Font_Size* 令牌）");
            }

            if (CornerRadiusLiteral.IsMatch(text))
            {
                failures.Add(rel + " 存在数字 CornerRadius（请引用 Radius_* 令牌）");
            }
        }

        Assert.True(failures.Count == 0, string.Join("\n", failures));
    }

    [Fact]
    public void TokenRatchet_MarginAndPadding_NotExceedBaseline()
    {
        var failures = new List<string>();
        foreach (string rel in ScannedViews)
        {
            string text = File.ReadAllText(RepoRoot() + "/" + rel);
            int margins = MarginLiteral.Matches(text).Count;
            int paddings = PaddingLiteral.Matches(text).Count;
            Baseline.TryGetValue(rel, out Dictionary<string, int>? allowed);
            int allowedMargin = allowed?.GetValueOrDefault("margin", 0) ?? 0;
            int allowedPadding = allowed?.GetValueOrDefault("padding", 0) ?? 0;

            if (margins > allowedMargin)
            {
                failures.Add($"{rel}: Margin 裸值 {margins} > 基线 {allowedMargin}");
            }

            if (paddings > allowedPadding)
            {
                failures.Add($"{rel}: Padding 裸值 {paddings} > 基线 {allowedPadding}");
            }
        }

        Assert.True(failures.Count == 0,
            "XAML 裸值超出棘轮基线（只减不增；有意新增请下调 baseline.json 并在变更记录说明）：\n"
            + string.Join("\n", failures));
    }

    [Fact]
    public void TokenRatchet_WidthHeightBorderStroke_NotExceedBaseline()
    {
        var failures = new List<string>();
        foreach (string rel in ScannedViews)
        {
            string text = File.ReadAllText(RepoRoot() + "/" + rel);
            int widths = WidthLiteral.Matches(text).Count;
            int heights = HeightLiteral.Matches(text).Count;
            int borders = BorderThicknessLiteral.Matches(text).Count;
            int strokes = StrokeThicknessLiteral.Matches(text).Count;
            Baseline.TryGetValue(rel, out Dictionary<string, int>? allowed);
            int allowedWidth = allowed?.GetValueOrDefault("width", 0) ?? 0;
            int allowedHeight = allowed?.GetValueOrDefault("height", 0) ?? 0;
            int allowedBorder = allowed?.GetValueOrDefault("border", 0) ?? 0;
            int allowedStroke = allowed?.GetValueOrDefault("stroke", 0) ?? 0;

            if (widths > allowedWidth)
            {
                failures.Add($"{rel}: Width 裸值 {widths} > 基线 {allowedWidth}");
            }

            if (heights > allowedHeight)
            {
                failures.Add($"{rel}: Height 裸值 {heights} > 基线 {allowedHeight}");
            }

            if (borders > allowedBorder)
            {
                failures.Add($"{rel}: BorderThickness 裸值 {borders} > 基线 {allowedBorder}");
            }

            if (strokes > allowedStroke)
            {
                failures.Add($"{rel}: StrokeThickness 裸值 {strokes} > 基线 {allowedStroke}");
            }
        }

        Assert.True(failures.Count == 0,
            "XAML Width/Height/BorderThickness/StrokeThickness 裸值超出棘轮基线（只减不增；有意新增请下调 baseline.json 并在变更记录说明）：\n"
            + string.Join("\n", failures));
    }

    // ---------------- 反向验证自检（03 §4.1：未经反向验证的守门等于没守门） ----------------

    [Fact]
    public void TokenRatchet_DetectorFunction_SampleInjection_ReverseVerification()
    {
        Assert.Matches(FontSizeLiteral, "FontSize=\"22\"");
        Assert.Matches(CornerRadiusLiteral, "CornerRadius=\"10\"");
        Assert.Matches(MarginLiteral, "Margin=\"0,0,6,0\"");
        Assert.Matches(PaddingLiteral, "Padding=\"12\"");
        Assert.Matches(WidthLiteral, "Width=\"300\"");
        Assert.Matches(HeightLiteral, "Height=\"30\"");
        Assert.Matches(BorderThicknessLiteral, "BorderThickness=\"0,0,0,1\"");
        Assert.Matches(StrokeThicknessLiteral, "StrokeThickness=\"2\"");
        Assert.DoesNotMatch(FontSizeLiteral, "FontSize=\"{DynamicResource Font_SizeBody}\"");
        Assert.DoesNotMatch(CornerRadiusLiteral, "CornerRadius=\"{DynamicResource Radius_Chip}\"");
        Assert.DoesNotMatch(WidthLiteral, "Width=\"*\"");
        Assert.DoesNotMatch(WidthLiteral, "MinWidth=\"120\"");
        Assert.DoesNotMatch(HeightLiteral, "MaxHeight=\"600\"");
        Assert.DoesNotMatch(BorderThicknessLiteral, "BorderThickness=\"{DynamicResource Border_Card}\"");
    }

    /// <summary>
    /// 枚举 <c>src</c> 下全部 XAML，剔除 <c>obj/</c>、<c>bin/</c>（WPF 编译产物目录，可能含拷贝的 xaml）
    /// 与 <see cref="ExcludedViews"/>，按相对路径排序保证输出稳定。
    /// </summary>
    private static string[] EnumerateScannedViews()
    {
        var excluded = ExcludedViews.Select(e => e.Path).ToHashSet(StringComparer.Ordinal);
        return Directory
            .EnumerateFiles(Path.Combine(RepoRoot(), "src"), "*.xaml", SearchOption.AllDirectories)
            .Where(p => !ViewLoadSmokeGuardTests.IsBuildArtifactPath(p))
            .Select(Relative)
            .Where(rel => !excluded.Contains(rel))
            .OrderBy(rel => rel, StringComparer.Ordinal)
            .ToArray();
    }

    /// <summary>仓库根相对路径，统一用 <c>/</c> 分隔（Windows 上 <see cref="Path"/> 返回 <c>\</c>）。</summary>
    private static string Relative(string full) => Path.GetRelativePath(RepoRoot(), full).Replace('\\', '/');

    private static string RepoRoot()
    {
        DirectoryInfo? dir = new(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "SystemToolkit.sln")))
        {
            dir = dir.Parent;
        }

        return dir?.FullName ?? throw new InvalidOperationException("未定位到仓库根（SystemToolkit.sln）");
    }
}
