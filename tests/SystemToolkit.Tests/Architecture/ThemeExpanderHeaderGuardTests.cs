using System.Text.RegularExpressions;

namespace SystemToolkit.Tests.Architecture;

/// <summary>
/// 主题 <c>ThemedExpander</c> 模板结构守卫（2026-09-15 建立）。
///
/// <b>背景（实机反馈）</b>：驱动管理页点开左侧过滤后，<b>分组名称整片消失</b> ——
/// 截图放大测量显示每个分组头只渲染出一个 10px 高的箭头、右侧无任何文字。
/// 回源定位为 <c>ThemedExpander</c> 自定义 <c>ControlTemplate</c> 的两个缺陷
/// （两个主题包完全相同，自主题引入 <c>5e4f0f7</c> 起一直存在）：
/// <list type="number">
/// <item>头部 <c>ToggleButton</c> 未绑 <c>Content="{TemplateBinding Header}"</c> ⇒
///   <c>Expander.Header</c> 从未传给箭头按钮，其模板里 <c>ContentPresenter</c> 的
///   Content 恒为空 ⇒ 分组名与「N 个驱动包」计数永不显示；</item>
/// <item>未复刻标准 <c>Expander</c> 的 <c>IsExpanded</c> 触发器 ⇒ 折叠语义整体失效：
///   <c>IsExpanded=False</c> 时内容照常显示（箭头指右像折叠态、行却全在）。</item>
/// </list>
///
/// <b>为什么必须加守卫</b>：这类「资源模板缺绑定」不违反任何既有判据 ——
/// <c>UiTokenRatchetTests</c> 只管内联裸数字、<c>TokenKeysCoverageTests</c> 只管
/// <c>x:Key</c> ↔ <c>TokenKeys</c> 对齐、<c>ViewLoadSmokeGuardTests</c> 只做加载冒烟。
/// 缺陷是<b>视觉静默</b>的：模板能加载、页面能开、测试全绿，只有人眼看得出。
///
/// <b>规则</b>：两个主题包的 <c>ThemedExpander</c> 样式块内，
/// ①必须存在 <c>Content="{TemplateBinding Header}"</c>；
/// ②必须存在 <c>IsExpanded</c> 为 False 时把内容区置 <c>Collapsed</c> 的触发器。
/// </summary>
public class ThemeExpanderHeaderGuardTests
{
    private static readonly Regex IsExpandedCollapseTrigger = new(
        @"Trigger\s+Property=""IsExpanded""\s+Value=""False""[\s\S]{0,400}?Value=""Collapsed""",
        RegexOptions.Compiled);

    private static readonly string[] Packs =
    {
        "Claude.Light.xaml",
        "Nvidia.Dark.xaml",
    };

    [Fact]
    public void BothThemePacks_ThemedExpander_MustRenderHeaderAndHonorCollapse()
    {
        var violations = new List<string>();
        foreach (string pack in Packs)
        {
            string? path = FindPack(pack);
            Assert.True(path is not null, $"未找到主题包：{pack}");
            violations.AddRange(Inspect(pack, File.ReadAllText(path!)));
        }

        Assert.Empty(violations);
    }

    /// <summary>
    /// 判据自证：喂入「修复前的坏写法」必须报 2 条、喂入「修复后的写法」必须报 0 条。
    /// （防「判据写空、永远绿」——本仓假测试纪律。）
    /// </summary>
    [Fact]
    public void Inspect_SelfProof()
    {
        const string broken = """
            <Style x:Key="ThemedExpander" TargetType="Expander">
                <Setter Property="Template">
                    <Setter.Value>
                        <ControlTemplate TargetType="Expander">
                            <DockPanel>
                                <ToggleButton IsChecked="{Binding IsExpanded}" Padding="8,6"/>
                                <ContentPresenter Margin="8,4,8,8"/>
                            </DockPanel>
                        </ControlTemplate>
                    </Setter.Value>
                </Setter>
            </Style>
            """;

        const string fixed_ = """
            <Style x:Key="ThemedExpander" TargetType="Expander">
                <Setter Property="Template">
                    <Setter.Value>
                        <ControlTemplate TargetType="Expander">
                            <DockPanel>
                                <ToggleButton IsChecked="{Binding IsExpanded}"
                                              Content="{TemplateBinding Header}"/>
                                <ContentPresenter x:Name="ExpandSite"/>
                            </DockPanel>
                            <ControlTemplate.Triggers>
                                <Trigger Property="IsExpanded" Value="False">
                                    <Setter TargetName="ExpandSite" Property="Visibility" Value="Collapsed"/>
                                </Trigger>
                            </ControlTemplate.Triggers>
                        </ControlTemplate>
                    </Setter.Value>
                </Setter>
            </Style>
            """;

        Assert.Equal(2, Inspect("(self-proof-broken)", broken).Count);
        Assert.Empty(Inspect("(self-proof-fixed)", fixed_));
    }

    /// <summary>在给定 XAML 文本中检查 ThemedExpander 样式块；返回违规描述。</summary>
    internal static List<string> Inspect(string packName, string xaml)
    {
        var violations = new List<string>();
        string? block = ExtractStyleBlock(xaml, "ThemedExpander");
        if (block is null)
        {
            violations.Add($"{packName}：未找到 ThemedExpander 样式块（样式被改名或删除？）");
            return violations;
        }

        if (!block.Contains("Content=\"{TemplateBinding Header}\"", StringComparison.Ordinal))
        {
            violations.Add(
                $"{packName}：ThemedExpander 头部 ToggleButton 缺 Content=\"{{TemplateBinding Header}}\"" +
                " ⇒ Expander.Header 传不到箭头按钮，分组名称与计数永不显示");
        }

        if (!IsExpandedCollapseTrigger.IsMatch(block))
        {
            violations.Add(
                $"{packName}：ThemedExpander 缺「IsExpanded=False ⇒ 内容区 Collapsed」触发器" +
                " ⇒ 折叠语义失效（折叠态下内容照常显示）");
        }

        return violations;
    }

    /// <summary>截取 <c>&lt;Style x:Key="键名"&gt;…&lt;/Style&gt;</c> 区间（以下一个 &lt;Style 为界）。</summary>
    private static string? ExtractStyleBlock(string xaml, string key)
    {
        string marker = $"<Style x:Key=\"{key}\"";
        int start = xaml.IndexOf(marker, StringComparison.Ordinal);
        if (start < 0)
        {
            return null;
        }

        int next = xaml.IndexOf("<Style x:Key=", start + marker.Length, StringComparison.Ordinal);
        return next < 0 ? xaml[start..] : xaml[start..next];
    }

    private static string? FindPack(string fileName)
    {
        DirectoryInfo? dir = new(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "SystemToolkit.sln")))
        {
            dir = dir.Parent;
        }

        string root = dir?.FullName ?? throw new InvalidOperationException("未定位到仓库根");
        return Directory
            .EnumerateFiles(Path.Combine(root, "src", "SystemToolkit.UI.Common", "Themes", "Packs"),
                fileName, SearchOption.AllDirectories)
            .FirstOrDefault();
    }
}
