using System.Text.RegularExpressions;

namespace SystemToolkit.Tests.Review;

/// <summary>
/// v18 审查批次的回归锁（2026-09-16）。
/// <para>
/// 本批修复的共同特征：**都是「同类只修一处」的补漏**——同一缺陷模式在一处已修、另一处漏网。
/// 因此锁的形态统一为「**源码结构性断言**」：直接读源文件，断言目标代码块中
/// ① 声明存在 ② 可抛语句位于 try 之内 ③ 姊妹点同时被覆盖。
/// </para>
/// <para>
/// 🔴 本类刻意采用「位置相关断言」而非全文件 <c>Contains</c>——历史实证：
/// 断言串在文件里有多处时，删掉目标那处判据照样绿（判据太弱是"红数 &lt; 断点数"的三大成因之一）。
/// </para>
/// </summary>
public class ReviewV18RegressionTests
{
    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "SystemToolkit.sln")))
        {
            dir = dir.Parent;
        }

        Assert.NotNull(dir);
        return dir!.FullName;
    }

    private static string Src(string relative) =>
        File.ReadAllText(Path.Combine(RepoRoot(), relative.Replace('/', Path.DirectorySeparatorChar)));

    /// <summary>
    /// 取从 <paramref name="anchor"/> 起、配对花括号内的整段文本。
    /// 用于把断言限定在**目标方法体**内，避免"同名串在别处也出现"导致的假绿。
    /// </summary>
    private static string MethodBody(string source, string anchor)
    {
        int i = source.IndexOf(anchor, StringComparison.Ordinal);
        Assert.True(i >= 0, $"锚点未找到：{anchor}");
        int b = source.IndexOf('{', i);
        int depth = 0;
        for (int j = b; j < source.Length; j++)
        {
            if (source[j] == '{')
            {
                depth++;
            }
            else if (source[j] == '}')
            {
                depth--;
                if (depth == 0)
                {
                    return source[i..(j + 1)];
                }
            }
        }

        Assert.Fail($"花括号未配对：{anchor}");
        return "";
    }

    /// <summary>
    /// 断言「<paramref name="call"/> 的可执行调用」位于 <paramref name="body"/> 内第一个
    /// <c>try</c> **语句**之后。
    /// <para>
    /// 🔴 必须用 <see cref="LastIndexOf(string, StringComparison)"/> 而非 <c>IndexOf</c>——
    /// 本仓的修复注释里**会引用被修的那句调用原文**（如「`PickFiles?.Invoke()` 纳入 try」），
    /// 于是 <c>IndexOf</c> 首命中注释、得出"调用在 try 之前"的**假红**（本批实测踩到）。
    /// 真实调用永远在注释之后 ⇒ 取最后一次出现。
    /// </para>
    /// <para>
    /// 🔴 找 <c>try</c> 必须**配对花括号定位真正的 try 语句**，不能用 <c>body.IndexOf("try")</c>——
    /// 本仓注释里大量出现"try 起点""纳入 try"等字样（B4 实测：首个 "try" 落在中文注释的第 88 字符），
    /// 于是 <c>tryIdx</c> 恒小于调用位置 ⇒ **判据永真、断点注入了也不红**（反向验证当场抓到）。
    /// 正解：只认「行首缩进 + <c>try</c> + 换行 + `{`」这一形态。
    /// </para>
    /// </summary>
    private static int FirstTryStatement(string body)
    {
        // 逐行扫描：trimStart 后以 "try" 开头、且下一处非空字符是 '{' 的行，才算 try 语句
        int pos = 0;
        foreach (string raw in body.Split('\n'))
        {
            string line = raw.TrimEnd('\r');
            string trimmed = line.TrimStart();
            if (trimmed.StartsWith("try", StringComparison.Ordinal))
            {
                string rest = trimmed[3..].TrimStart();
                // 允许 "try" 后直接跟 '{'（标准写法），或跟注释后换行再 '{'
                if (rest.StartsWith('{') || rest.Length == 0 || rest.StartsWith("//", StringComparison.Ordinal))
                {
                    return pos;
                }
            }

            pos += raw.Length + 1; // +1 补回 '\n'
        }

        return -1;
    }

    private static void AssertCallAfterFirstTry(string body, string call, string context)
    {
        int tryIdx = FirstTryStatement(body);
        int callIdx = body.LastIndexOf(call, StringComparison.Ordinal);

        Assert.True(tryIdx >= 0, $"{context} 缺 try 语句");
        Assert.True(callIdx >= 0, $"{context} 未找到 {call}");
        Assert.True(tryIdx < callIdx,
            $"{context} 的 {call} 必须位于 try 之内（try@{tryIdx} call@{callIdx}）");
    }

    // ══════════════════════════════════════════════════════════════
    // ① FileBackup：EnabledToBrushConverter 主题不跟随
    // ══════════════════════════════════════════════════════════════

    /// <summary>
    /// 启停徽标必须用 `Style` + `DataTrigger` + `{DynamicResource}` 驱动颜色——
    /// 不能再用 IValueConverter。
    /// <para>
    /// 根因：转换器绑的是 <c>RuleRowVm.Enabled</c>（getter-only 派生属性、不实现 INPC）⇒
    /// 主题切换时**绑定源不变化** ⇒ <c>Convert</c> 不会重跑 ⇒ 颜色停在旧主题画刷。
    /// </para>
    /// 🔴 反向验证：把 XAML 改回 `Foreground="{Binding Enabled, Converter=...}"` → 本用例变红。
    /// </summary>
    [Fact]
    public void FileBackup_EnabledBadge_MustUseDynamicResourceDataTrigger()
    {
        string xaml = Src("src/SystemToolkit.Modules.FileBackup/FileBackupView.xaml");

        Assert.DoesNotContain("EnabledToBrushConverter", xaml);
        Assert.DoesNotContain("{StaticResource EnabledBrush}", xaml);

        // 徽标块（以 EnabledText 绑定为锚点，向后取一段）内必须出现 DataTrigger + DynamicResource
        int i = xaml.IndexOf("{Binding EnabledText}", StringComparison.Ordinal);
        Assert.True(i >= 0, "未找到启停徽标的 EnabledText 绑定");
        string block = xaml[i..Math.Min(xaml.Length, i + 1200)];

        Assert.Contains("DataTrigger", block);
        Assert.Contains("Brush_SuccessText", block);

        // 🔴 必须是 {DynamicResource} —— 只断言"含 DynamicResource 字样"太弱：
        // 把这一处改成 {StaticResource Brush_SuccessText}（正是"主题不跟随"的原病灶）时，
        // 块里别处（Font_SizeMicro）仍含 DynamicResource ⇒ 判据照样绿（反向验证当场抓到）。
        // 正解：把画刷**绑定形态**本身写成断言。
        Assert.Contains("Value=\"{DynamicResource Brush_SuccessText}\"", block);
    }

    /// <summary>
    /// 已删除的转换器类型不得死灰复燃（删除后其消费点也必须一并清除，否则 IDE0051 在 Release 报错）。
    /// </summary>
    [Fact]
    public void FileBackup_Converters_EnabledToBrushConverterMustNotExist()
    {
        string cs = Src("src/SystemToolkit.Modules.FileBackup/Converters.cs");
        Assert.DoesNotContain("class EnabledToBrushConverter", cs);
        // 姊妹转换器必须仍在（防"连带删多了"）
        Assert.Contains("class InverseBoolConverter", cs);
    }

    // ══════════════════════════════════════════════════════════════
    // ② FileTransfer：解析期守卫 + 日志折叠 + 发送命令 try 覆盖
    // ══════════════════════════════════════════════════════════════

    /// <summary>
    /// `OnTabChecked` 必须在本仓四件套守卫之内：DataContext 类型判定 + 两个命名元素判空。
    /// <para>
    /// 🔴 反向验证：删掉 `DataContext is not FileTransferViewModel vm` → 本用例变红。
    /// </para>
    /// </summary>
    [Fact]
    public void FileTransfer_OnTabChecked_MustGuardParseTimeNulls()
    {
        string cs = Src("src/SystemToolkit.Modules.FileTransfer/FileTransferView.xaml.cs");
        string body = MethodBody(cs, "private void OnTabChecked(object sender, RoutedEventArgs e)");

        Assert.Contains("DataContext is not FileTransferViewModel", body);
        Assert.Contains("DesktopPanel is null", body);
        Assert.Contains("MobilePanel is null", body);
        // 必须走 vm 局部变量，不得再用 Vm 属性（属性能为 null）
        Assert.Contains("vm.SelectedTabIndex", body);
        Assert.DoesNotContain("Vm.SelectedTabIndex", body);
    }

    /// <summary>
    /// 日志折叠 Toggle 必须有处理器且 XAML 已接线——此前是 no-op 按钮
    /// （有 ToggleButton 视觉、无 Checked/Unchecked 处理器、LogHost 无 Visibility 绑定）。
    /// <para>
    /// 🔴 反向验证：删掉 XAML 的 `Checked="OnLogToggleChanged"` → 本用例变红。
    /// </para>
    /// </summary>
    [Fact]
    public void FileTransfer_LogToggle_MustBeWiredAndHaveHandler()
    {
        string xaml = Src("src/SystemToolkit.Modules.FileTransfer/FileTransferView.xaml");
        string cs = Src("src/SystemToolkit.Modules.FileTransfer/FileTransferView.xaml.cs");

        int i = xaml.IndexOf("x:Name=\"LogToggle\"", StringComparison.Ordinal);
        Assert.True(i >= 0, "未找到 LogToggle");
        string block = xaml[i..Math.Min(xaml.Length, i + 500)];
        Assert.Contains("Checked=\"OnLogToggleChanged\"", block);
        Assert.Contains("Unchecked=\"OnLogToggleChanged\"", block);

        // 处理器存在，且含解析期判空守卫（LogHost 是后段命名元素）
        string body = MethodBody(cs, "private void OnLogToggleChanged(object sender, RoutedEventArgs e)");
        Assert.Contains("LogHost is not null", body);
        Assert.Contains("LogHost.Visibility", body);
    }

    /// <summary>
    /// 两个发送命令的 `PickFiles?.Invoke()` 必须在 try 之内。
    /// <para>
    /// 根因（V12-D2 同款）：View 注入的对话框回调可抛，裸在 try 外 ⇒ 绕过命令体 catch
    /// 直冲 `AsyncRelayCommand` 吞异常路径 ⇒ 用户零反馈、日志零记录。
    /// </para>
    /// 🔴 反向验证：把 `PickFiles?.Invoke()` 行移出 try → 本用例变红。
    /// </summary>
    [Theory]
    [InlineData("private async Task SendToSelectedDeviceAsync()")]
    [InlineData("private async Task SendToKnownPeerAsync()")]
    public void FileTransfer_SendCommands_PickFilesMustBeInsideTry(string anchor)
    {
        string cs = Src("src/SystemToolkit.Modules.FileTransfer/FileTransferDesktopViewModel.cs");
        string body = MethodBody(cs, anchor);

        AssertCallAfterFirstTry(body, "PickFiles?.Invoke()", anchor);

        // OCE 分流不得丢失
        Assert.Contains("catch (OperationCanceledException)", body);
    }

    /// <summary>Mobile VM 的启动命令必须有 OCE 分流（与 StopWebAsync / 桌面侧同口径）。</summary>
    [Fact]
    public void FileTransferMobile_StartWeb_MustSeparateOperationCanceled()
    {
        string cs = Src("src/SystemToolkit.Modules.FileTransfer/FileTransferMobileViewModel.cs");
        string body = MethodBody(cs, "private async Task StartWebAsync()");

        Assert.Contains("catch (OperationCanceledException)", body);
    }

    /// <summary>
    /// Mobile VM 的 RunGuarded 必须双通道落日志（UI 面板 + 文件日志）——与桌面侧同款。
    /// </summary>
    [Fact]
    public void FileTransferMobile_RunGuarded_MustAlsoWriteFileLog()
    {
        string cs = Src("src/SystemToolkit.Modules.FileTransfer/FileTransferMobileViewModel.cs");
        string body = MethodBody(cs, "private void RunGuarded(Action action)");

        Assert.Contains("_log(", body);
        Assert.Contains("_logger", body);
    }

    /// <summary>
    /// `StartWebAsync` 的 catch 也必须双通道落日志——`RunGuarded` 覆盖的是"UI 事件处理器"，
    /// 而启动失败走的是命令自己的 catch，此前只 `_log` 不落文件（与桌面侧双通道纪律不一致）。
    /// 🔴 反向验证：删掉 `_logger?.Error("[手机] Web 服务启动失败", ex);` → 本用例变红。
    /// </summary>
    [Fact]
    public void FileTransferMobile_StartWeb_MustAlsoWriteFileLog()
    {
        string cs = Src("src/SystemToolkit.Modules.FileTransfer/FileTransferMobileViewModel.cs");
        string body = MethodBody(cs, "private async Task StartWebAsync()");

        Assert.Contains("_logger?.Error(\"[手机] Web 服务启动失败\", ex);", body);
    }

    // ══════════════════════════════════════════════════════════════
    // ③ AppManager：导出回调进 try + 单条忽略文案对齐
    // ══════════════════════════════════════════════════════════════

    /// <summary>
    /// `ExportInstallHistory` 的 `PickReportPath?.Invoke()` 必须在 try 之内
    /// （与 `ExportList` 的 V11-A4 修复同款；同类只修一处）。
    /// 🔴 反向验证：把 Invoke 行移出 try → 本用例变红。
    /// </summary>
    [Fact]
    public void AppManager_ExportInstallHistory_PickReportPathMustBeInsideTry()
    {
        string cs = Src("src/SystemToolkit.Modules.AppManager/AppManagerViewModel.cs");
        string body = MethodBody(cs, "private void ExportInstallHistory()");

        AssertCallAfterFirstTry(body, "PickReportPath?.Invoke()", "ExportInstallHistory");
        // path 必须提前声明（否则 catch 段引用不到）
        Assert.Contains("string? path = null;", body);
    }

    /// <summary>
    /// 单条忽略/取消忽略必须消费 `SetIgnore` 的 bool 返回值并据此分文案
    /// （与 `IgnoreSelected` 对齐）；否则落盘失败时文案会误导为"已持久化"。
    /// <para>
    /// 🔴 反向验证：把 `bool saved = SetIgnore(...)` 改回 `SetIgnore(...)` → 本用例变红。
    /// </para>
    /// </summary>
    [Theory]
    [InlineData("private void IgnoreSingle(WingetPackageVm vm)")]
    [InlineData("private void UnignoreSingle(WingetPackageVm vm)")]
    public void AppManager_SingleIgnore_MustConsumePersistResult(string anchor)
    {
        string cs = Src("src/SystemToolkit.Modules.AppManager/AppManagerViewModel.cs");
        string body = MethodBody(cs, anchor);

        Assert.Contains("bool saved = SetIgnore(", body);
        Assert.Contains("未持久化，重启后失效", body);
    }

    // ══════════════════════════════════════════════════════════════
    // ④ FileBackup：命令体顶层 catch（并已从基线收缩）
    // ══════════════════════════════════════════════════════════════

    /// <summary>
    /// `BackupNowAsync` / `BackupAllAsync` 必须各自有顶层 catch；
    /// 且**不得**再出现在 `AsyncCommandCatchBaseline.json`（修复后基线应已收缩）。
    /// 🔴 反向验证：删掉任一 catch → 基线守卫会报"新命中"，本用例同时变红。
    /// </summary>
    [Theory]
    [InlineData("private async Task BackupNowAsync()")]
    [InlineData("private async Task BackupAllAsync()")]
    public void FileBackup_BackupCommands_MustHaveTopLevelCatch_AndBeRemovedFromBaseline(string anchor)
    {
        string cs = Src("src/SystemToolkit.Modules.FileBackup/FileBackupViewModel.Backup.cs");
        string body = MethodBody(cs, anchor);

        Assert.Contains("catch (Exception ex)", body);

        string baseline = Src("tests/SystemToolkit.Tests/Architecture/AsyncCommandCatchBaseline.json");
        string methodName = anchor[(anchor.LastIndexOf(' ') + 1)..].TrimEnd('(', ')');
        Assert.DoesNotContain("::" + methodName, baseline);
    }

    /// <summary>
    /// FileBackup 四处同步命令的 View 回调必须纳入 try（同族：SaveRule / DeleteRule /
    /// ImportRules / DeleteSnapshot）。逐个断言，避免"只修一处"被判成已修。
    /// </summary>
    [Theory]
    [InlineData("FileBackupViewModel.cs", "private void SaveRule()", "ConfirmRequest?.Invoke(\"保存为草稿\"")]
    [InlineData("FileBackupViewModel.cs", "private void DeleteRule()", "ConfirmRequest?.Invoke(\"删除规则\"")]
    [InlineData("FileBackupViewModel.cs", "private void ImportRules()", "ConfirmRequest?.Invoke(\"导入规则\"")]
    [InlineData("FileBackupViewModel.Restore.cs", "private void DeleteSnapshot()", "ConfirmRequest?.Invoke(\"删除快照\"")]
    public void FileBackup_SyncCommands_ViewCallbacksMustBeInsideTry(string file, string anchor, string call)
    {
        string cs = Src("src/SystemToolkit.Modules.FileBackup/" + file);
        string body = MethodBody(cs, anchor);

        AssertCallAfterFirstTry(body, call, anchor);
    }

    /// <summary>ImportRules 的选择文件回调同样纳入 try；且异常文案保留文件路径线索。</summary>
    [Fact]
    public void FileBackup_ImportRules_PickOpenPathMustBeInsideTry()
    {
        string cs = Src("src/SystemToolkit.Modules.FileBackup/FileBackupViewModel.cs");
        string body = MethodBody(cs, "private void ImportRules()");

        AssertCallAfterFirstTry(body, "PickOpenPath?.Invoke()", "ImportRules");
    }

    /// <summary>EditRule 的弹窗回调必须就地兜底（同步命令，守卫不扫）。</summary>
    [Fact]
    public void FileBackup_EditRule_MustGuardPopupCallback()
    {
        string cs = Src("src/SystemToolkit.Modules.FileBackup/FileBackupViewModel.cs");
        string body = MethodBody(cs, "private void EditRule()");

        Assert.Contains("EditRuleRequest?.Invoke()", body);
        Assert.Contains("catch (Exception ex)", body);
    }

    // ══════════════════════════════════════════════════════════════
    // ⑤ 可测性：FileTransfer csproj 必须声明 InternalsVisibleTo
    // ══════════════════════════════════════════════════════════════

    /// <summary>
    /// FileTransfer 此前缺 `InternalsVisibleTo`（同架构层 5 个模块都有），
    /// 导致其 internal 成员无法被测试直构 ⇒ 本批新增的视图级回归测试无法落地。
    /// </summary>
    [Fact]
    public void FileTransfer_Csproj_MustDeclareInternalsVisibleTo()
    {
        string csproj = Src("src/SystemToolkit.Modules.FileTransfer/SystemToolkit.Modules.FileTransfer.csproj");
        // 🔴 不能用裸 `Contains("InternalsVisibleTo")` —— 篡改成 `InternalsVisibleTo0`（无效元素、
        // MSBuild 静默忽略）时**子串仍然命中** ⇒ 判据照样绿（反向验证当场抓到）。
        // 正解：断言**完整元素形态**。
        Assert.Contains("<InternalsVisibleTo Include=\"SystemToolkit.Tests\" />", csproj);
    }
}
