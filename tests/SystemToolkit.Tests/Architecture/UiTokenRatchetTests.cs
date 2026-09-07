using System.Text.Json;
using System.Text.RegularExpressions;

namespace SystemToolkit.Tests.Architecture;

/// <summary>
/// UI 令牌棘轮守卫（P1 落地）：XAML 裸值只减不增。
/// 规则：① FontSize / CornerRadius 数字字面量 = 零容忍（已有精确值令牌，直接引用）；
/// ② Margin / Padding 数字字面量 = 按文件基线（UiTokenRatchet/baseline.json），只许减少。
/// 基线在裸值收敛完成后生成；有意新增合法裸值 → 显式下调基线并在变更记录说明。
/// </summary>
public class UiTokenRatchetTests
{
    private static readonly string[] ScannedViews =
    {
        "src/SystemToolkit.Modules.Overview/OverviewView.xaml",
        "src/SystemToolkit.Modules.AppManager/AppManagerView.xaml",
        "src/SystemToolkit.Modules.AppManager/SoftwareEditWindow.xaml",
        "src/SystemToolkit.Modules.DriverManager/DriverManagerView.xaml",
        "src/SystemToolkit.Shell/MainWindow.xaml",
        "src/SystemToolkit.Modules.NetManager/NetManagerView.xaml",
        "src/SystemToolkit.Modules.FileTransfer/FileTransferView.xaml",
        "src/SystemToolkit.Modules.GameManager/GameManagerView.xaml",
    };

    private static readonly Regex FontSizeLiteral = new(@"FontSize=""\d", RegexOptions.Compiled);
    private static readonly Regex CornerRadiusLiteral = new(@"CornerRadius=""\d", RegexOptions.Compiled);
    private static readonly Regex MarginLiteral = new(@"\bMargin=""\d", RegexOptions.Compiled);
    private static readonly Regex PaddingLiteral = new(@"\bPadding=""\d", RegexOptions.Compiled);

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

    // ---------------- 反向验证自检（03 §4.1：未经反向验证的守门等于没守门） ----------------

    [Fact]
    public void TokenRatchet_DetectorFunction_SampleInjection_ReverseVerification()
    {
        Assert.Matches(FontSizeLiteral, "FontSize=\"22\"");
        Assert.Matches(CornerRadiusLiteral, "CornerRadius=\"10\"");
        Assert.Matches(MarginLiteral, "Margin=\"0,0,6,0\"");
        Assert.Matches(PaddingLiteral, "Padding=\"12\"");
        Assert.DoesNotMatch(FontSizeLiteral, "FontSize=\"{DynamicResource Font_SizeBody}\"");
        Assert.DoesNotMatch(CornerRadiusLiteral, "CornerRadius=\"{DynamicResource Radius_Chip}\"");
    }

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
