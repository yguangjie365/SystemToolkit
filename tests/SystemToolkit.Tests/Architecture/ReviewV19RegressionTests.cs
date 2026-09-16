using System.Text.RegularExpressions;

namespace SystemToolkit.Tests.Architecture;

/// <summary>
/// v19 回归锁（2026-09-16）—— 四模块单模块报告的**成立项**修复后，逐条钉死其判据。
///
/// <para>
/// <b>为什么必须补</b>：v19 六条修复做完反向验证后，逐条实测「删除修复 → 定向测试是否变红」，
/// 结果 **6 条里只有 1 条（M-1）有守卫覆盖**，其余 5 条全绿。即：这些修复一旦被后续改动
/// 悄悄回退，**构建与测试都不会报警**。按本仓纪律（规则 6：同一类问题出现第二次必须沉淀守卫；
/// 「修这类缺陷必须同时补回归测试」），在此补齐四条静态锁（O-2 为纯重构，无需锁）。
/// </para>
///
/// <para>
/// <b>为什么用静态扫描而非运行时断言</b>：① 目标判据（存在性/顺序）本可静态判定；
/// ② 运行时构造 MusicManager/Overview VM 需大量替身，成本高于收益；
/// ③ 静态锁的反向验证更干净（注入 → 红 → 恢复 → 绿），与既有守卫族一致。
/// </para>
/// </summary>
public class ReviewV19RegressionTests
{
    private static string RepoRoot()
    {
        DirectoryInfo? d = new(AppContext.BaseDirectory);
        while (d is not null && !File.Exists(Path.Combine(d.FullName, "SystemToolkit.sln")))
        {
            d = d.Parent;
        }

        return d?.FullName ?? throw new InvalidOperationException("未找到仓库根");
    }

    private static string Read(string relative)
        => File.ReadAllText(Path.Combine(RepoRoot(), relative));

    private static string Src(params string[] parts)
        => Path.Combine(["src", .. parts]);

    // ══════════════════════════════════════════════════════════════════════
    // M-4：OpenPlaylistAsync 的 catch(OCE) 必须带代际判断（try 内逐页校验、catch 不校验 = 半修）
    // ══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// 反向验证方式：删掉 catch(OperationCanceledException) 块内的 <c>if (seq == _playlistTracksSeq)</c>
    /// （还原为无条件写状态行）→ 本用例变红。
    /// <para>
    /// 缺陷场景：点歌单 A（seq=1）→ 改点歌单 B（seq=2）⇒ A 被取消抛 OCE，
    /// 其过期 catch 把 B 正在加载的状态行覆盖成「已取消」。
    /// </para>
    /// </summary>
    [Fact]
    public void Music_OpenPlaylistCancelCatch_MustCheckGenerationBeforeWritingStatus()
    {
        string text = Read(Src("SystemToolkit.Modules.MusicManager", "MusicManagerViewModel.Online.cs"));

        // 锚定 OpenPlaylistAsync 的方法体（从签名到方法结束）
        int sig = text.IndexOf("private async Task OpenPlaylistAsync(", StringComparison.Ordinal);
        Assert.True(sig >= 0, "未找到 OpenPlaylistAsync —— 方法被重命名/移除，本锁需要同步更新");

        string body = SliceBalanced(text, sig);

        // 该代际变量名在方法内为 seq（_playlistTracksSeq 的本次代）
        const string oceCatch = "catch (OperationCanceledException)";
        int ci = body.IndexOf(oceCatch, StringComparison.Ordinal);
        Assert.True(ci >= 0, "OpenPlaylistAsync 不再有 catch(OperationCanceledException) —— 本锁需要同步更新");

        // 取该 catch 块（花括号配对）
        string catchBlock = SliceBalanced(body, ci, fromBrace: true);

        Assert.True(
            catchBlock.Contains("seq != _playlistTracksSeq", StringComparison.Ordinal)
            || catchBlock.Contains("seq == _playlistTracksSeq", StringComparison.Ordinal),
            "OpenPlaylistAsync 的 catch(OCE) 必须做代际判断后再写状态行（v19 M-4）：\n"
            + "缺它则「连点两个歌单」时，前一个被取消的请求会把当前歌单的状态行覆盖成「已取消」。");
    }

    /// <summary>
    /// M-1：LoadPlaylistsAsync 必须有 catch（此前只有 finally ⇒ 异常逃逸到 AsyncRelayCommand 被吞）。
    /// 反向验证：删除 catch 块 → 本用例与 <see cref="AsyncCommandCatchGuardTests"/> 同时变红。
    /// </summary>
    [Fact]
    public void Music_LoadPlaylists_MustCatchNotOnlyFinally()
    {
        string text = Read(Src("SystemToolkit.Modules.MusicManager", "MusicManagerViewModel.Online.cs"));
        int sig = text.IndexOf("private async Task LoadPlaylistsAsync(", StringComparison.Ordinal);
        Assert.True(sig >= 0, "未找到 LoadPlaylistsAsync");

        string body = SliceBalanced(text, sig);

        Assert.True(body.Contains("catch (OperationCanceledException)", StringComparison.Ordinal),
            "LoadPlaylistsAsync 缺 catch(OperationCanceledException)（v19 M-1）");
        Assert.True(Regex.IsMatch(body, @"catch\s*\(\s*Exception\b"),
            "LoadPlaylistsAsync 缺 catch(Exception)（v19 M-1）—— 原实现只有 finally，异常会被 AsyncRelayCommand 静默吞掉");
    }

    // ══════════════════════════════════════════════════════════════════════
    // G-1：GameManagerView.OnViewLoaded 必须有 _loaded 幂等闸
    // ══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// 反向验证方式：把 <c>if (_vm.IsLoading || _loaded)</c> 改回 <c>if (_vm.IsLoading)</c> → 本用例变红。
    /// <para>
    /// 机制：View 为 AddTransient、VM 为 Singleton ⇒ 主题切换重建视图时新实例的 Loaded 再次触发；
    /// 而 <c>_vm.IsLoading</c> 挂在单例上、加载完成后恒 false ⇒ 第二次 Loaded 必然重跑整轮
    /// Steam 库扫描（磁盘遍历 + 封面加载）。既有 <c>ViewLoadSmokeGuardTests</c> 不覆盖此点。
    /// </para>
    /// </summary>
    [Fact]
    public void GameManager_ViewLoaded_MustBeIdempotentViaLoadedField()
    {
        string text = Read(Src("SystemToolkit.Modules.GameManager", "GameManagerView.xaml.cs"));

        // 必须存在 _loaded 字段
        Assert.True(Regex.IsMatch(text, @"private\s+bool\s+_loaded\s*;"),
            "GameManagerView 缺 _loaded 字段（v19 G-1）：主题切换重建视图会重跑全量扫描");

        // 幂等闸必须同时判 _loaded
        int sig = text.IndexOf("private void OnViewLoaded(", StringComparison.Ordinal);
        Assert.True(sig >= 0, "未找到 OnViewLoaded");
        string body = SliceBalanced(text, sig);

        Assert.True(Regex.IsMatch(body, @"if\s*\(\s*_vm\.IsLoading\s*\|\|\s*_loaded\s*\)"),
            "OnViewLoaded 的早退条件必须含 `|| _loaded`（v19 G-1）：\n"
            + "仅判 _vm.IsLoading 时，单例 VM 加载完成后该标志恒 false ⇒ 第二次 Loaded 会重跑全量扫描。");

        // 置位必须在早退之后、加载之前
        int guard = body.IndexOf("_loaded = true", StringComparison.Ordinal);
        int load = body.IndexOf("LoadCommand.Execute", StringComparison.Ordinal);
        Assert.True(guard >= 0 && load >= 0 && guard < load,
            "OnViewLoaded 必须在早退判断之后、执行加载之前置 `_loaded = true`（v19 G-1）");
    }

    // ══════════════════════════════════════════════════════════════════════
    // N-1：SplitRouteTabViewModel 的 IsBusy 必须挂 CanExecute 通知
    // ══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// 反向验证方式：删掉 <c>_isBusy</c> 上方的 <c>[NotifyCanExecuteChangedFor(nameof(ApplyCommand))]</c>
    /// → 本用例变红。
    /// <para>
    /// 缺陷：<c>CanApply = !IsBusy &amp;&amp; SelectedWan … &amp;&amp; SelectedLan …</c> 三个依赖项里
    /// Selected* 已挂通知、唯 IsBusy 漏挂（上轮 v11~v14 整改在其余 5 个 Tab 都补了，本 VM 漏网）。
    /// 后果：<c>RestoreAsync</c> 的两个赋值点（IsBusy=true/false）**完全无手工通知** ⇒
    /// 恢复原路由期间 Apply 按钮不变灰，可重入。
    /// </para>
    /// <para>
    /// ⚠️ 既有 <c>CommandCanExecuteRefreshGuardTests</c> **刻意排除 IsBusy 类属性**
    /// （见其注释「赋值点已手动调用 RefreshCanExecute」）——该前提在本 VM 只对 2/4 个赋值点成立，
    /// 故本锁独立存在，不依赖那条守卫。
    /// </para>
    /// </summary>
    [Fact]
    public void NetManager_SplitRouteIsBusy_MustNotifyApplyCanExecute()
    {
        string text = Read(Src("SystemToolkit.Modules.NetManager", "SplitRouteTabViewModel.cs"));

        // 定位 _isBusy 声明及其上方的特性块
        Match m = Regex.Match(text, @"(?<attrs>(?:\s*\[[^\]]+\]\s*)*)\s*private\s+bool\s+_isBusy\s*;");
        Assert.True(m.Success, "SplitRouteTabViewModel 未找到 private bool _isBusy;");

        string attrs = m.Groups["attrs"].Value;
        Assert.True(attrs.Contains("NotifyCanExecuteChangedFor(nameof(ApplyCommand))", StringComparison.Ordinal),
            "_isBusy 必须挂 [NotifyCanExecuteChangedFor(nameof(ApplyCommand))]（v19 N-1）：\n"
            + "CanApply 依赖 !IsBusy；缺通知时 RestoreAsync 的两个赋值点（无手工通知）会让 Apply 按钮不变灰。");

        // 且 CanApply 确实依赖 IsBusy（防判据漂移后本锁失效）
        Match can = Regex.Match(text, @"private\s+bool\s+CanApply\s*=>([^;]+);");
        Assert.True(can.Success, "未找到 CanApply 判据");
        Assert.True(can.Groups[1].Value.Contains("IsBusy", StringComparison.Ordinal),
            "CanApply 已不再依赖 IsBusy —— 本锁前提消失，请复核后同步调整");
    }

    // ══════════════════════════════════════════════════════════════════════
    // O-1：Overview 的软件列表行样式必须 BasedOn 共享样式（保选中态）
    // ══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// 反向验证方式：把 <c>&lt;Style TargetType="ListViewItem" BasedOn="{StaticResource ListItemRowTallStyle}"/&gt;</c>
    /// 换回手写内联模板 → 本用例变红。
    /// <para>
    /// 缺陷：手写版只复刻 <c>IsMouseOver</c>，丢了共享模板的 <c>IsSelected</c> 触发器
    /// （<c>Brush_Selection</c> 底 + 左缘 3px <c>SelBar</c>）；因是全量 ControlTemplate 替换，
    /// WPF 默认主题的选中高亮也一并丢弃 ⇒ **选中行无任何视觉反馈**。
    /// 同时手写版给 Border.Padding 与 GridViewRowPresenter.Margin 都绑了 Padding ⇒ 双内边距。
    /// </para>
    /// <para>
    /// ⚠️ 既有 <c>ViewLoadSmokeGuardTests</c> 只验「页面能开」，不验样式语义 —— 故本锁独立存在。
    /// </para>
    /// </summary>
    [Fact]
    public void Overview_InstalledAppsRowStyle_MustBasedOnSharedRowStyle()
    {
        string xaml = Read(Src("SystemToolkit.Modules.Overview", "OverviewView.xaml"));

        // 取 InstalledAppsList 之后的 ItemContainerStyle 段
        int list = xaml.IndexOf("x:Name=\"InstalledAppsList\"", StringComparison.Ordinal);
        Assert.True(list >= 0, "OverviewView 未找到 InstalledAppsList");

        int styleStart = xaml.IndexOf("<ListView.ItemContainerStyle>", list, StringComparison.Ordinal);
        Assert.True(styleStart > list, "未找到 ItemContainerStyle");
        int styleEnd = xaml.IndexOf("</ListView.ItemContainerStyle>", styleStart, StringComparison.Ordinal);
        Assert.True(styleEnd > styleStart, "ItemContainerStyle 未闭合");

        string style = xaml[styleStart..styleEnd];

        Assert.True(style.Contains("BasedOn=\"{StaticResource ListItemRowTallStyle}\"", StringComparison.Ordinal),
            "Overview 软件列表的 ListViewItem 样式必须 BasedOn 共享样式 ListItemRowTallStyle（v19 O-1）：\n"
            + "手写拷贝会丢 IsSelected 触发器（选中态无视觉反馈）并造成双内边距。");

        // 反向：不得再出现手写 ControlTemplate（防"BasedOn + 又覆盖 Template"的半修形态）
        Assert.False(style.Contains("<ControlTemplate", StringComparison.Ordinal),
            "Overview 行样式不得再内联 <ControlTemplate>（v19 O-1）：共享样式已提供完整模板");

        // 共享样式必须确实存在于主题包（防「BasedOn 的 key 被改名」后静默失效）
        foreach (string pack in new[]
                 {
                     Src("SystemToolkit.UI.Common", "Themes", "Packs", "Claude", "Claude.Light.xaml"),
                     Src("SystemToolkit.UI.Common", "Themes", "Packs", "Nvidia", "Nvidia.Dark.xaml"),
                 })
        {
            string theme = Read(pack);
            Assert.True(theme.Contains("x:Key=\"ListItemRowTallStyle\"", StringComparison.Ordinal),
                $"{pack} 缺 ListItemRowTallStyle —— Overview 的 BasedOn 会静默失效");

            // 该共享样式必须仍在模板里复刻选中态（本锁的保护目标）
            int baseStyle = theme.IndexOf("x:Key=\"ListItemRowBaseStyle\"", StringComparison.Ordinal);
            Assert.True(baseStyle >= 0, $"{pack} 缺 ListItemRowBaseStyle");
            int baseEnd = theme.IndexOf("</Style>", baseStyle, StringComparison.Ordinal);
            string baseBody = theme[baseStyle..baseEnd];
            Assert.True(baseBody.Contains("Property=\"IsSelected\"", StringComparison.Ordinal),
                $"{pack} 的 ListItemRowBaseStyle 丢了 IsSelected 触发器 —— 选中态回归无视觉反馈");
        }
    }

    // ══════════════════════════════════════════════════════════════════════
    // O-3：Overview 的 IconFont 资源必须带 Fluent 备选链
    // ══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// 反向验证方式：把 IconFont 值改回 <c>Segoe MDL2 Assets</c> → 本用例变红。
    /// </summary>
    [Fact]
    public void Overview_IconFont_MustIncludeFluentFallback()
    {
        string xaml = Read(Src("SystemToolkit.Modules.Overview", "OverviewView.xaml"));

        Match m = Regex.Match(xaml, @"<FontFamily\s+x:Key=""IconFont"">([^<]+)</FontFamily>");
        Assert.True(m.Success, "OverviewView 未找到 IconFont 资源");

        string value = m.Groups[1].Value;
        Assert.True(value.Contains("Segoe Fluent Icons", StringComparison.Ordinal),
            "IconFont 必须带 `Segoe Fluent Icons` 备选（v19 O-3）：\n"
            + "Win11 起 Fluent 字形更全；MDL2 仅作降级。AppManagerView 同款写法早已如此。");
        Assert.True(value.Contains("Segoe MDL2 Assets", StringComparison.Ordinal),
            "IconFont 必须保留 `Segoe MDL2 Assets` 作降级（旧系统无 Fluent 字体）");
    }

    // ══════════════════════════════════════════════════════════════════════
    // 通用助手
    // ══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// 从 <paramref name="start"/> 处开始截取「第一个 { 到其配对 }」的整段文本。
    /// <paramref name="fromBrace"/> = true 时直接以 start 处的 { 为起点（用于截 catch 块）。
    /// </summary>
    private static string SliceBalanced(string text, int start, bool fromBrace = false)
    {
        int open = fromBrace ? text.IndexOf('{', start) : text.IndexOf('{', start);
        if (open < 0)
        {
            return text[start..];
        }

        int depth = 0;
        for (int i = open; i < text.Length; i++)
        {
            if (text[i] == '{')
            {
                depth++;
            }
            else if (text[i] == '}')
            {
                depth--;
                if (depth == 0)
                {
                    return text[start..(i + 1)];
                }
            }
        }

        return text[start..];
    }
}
