using SystemToolkit.Core.Backup.Models;
using SystemToolkit.Core.Backup.Services;
using SystemToolkit.Core.Network.Services;
using SystemToolkit.Modules.FileBackup;

namespace SystemToolkit.Tests.Backup;

/// <summary>
/// 文件备份 VM 命令可执行性（2026-09-08 建立）。
/// 起因：真机反馈「选中快照后『校验/恢复』仍灰死」——根因是 <c>SelectedSnapshot</c>
/// 是 ObservableProperty，setter 不会自动通知 RelayCommand 重估 CanExecute，
/// 必须由 <c>OnSelectedSnapshotChanged → RefreshCanExecute()</c> 显式刷新。
/// 该 bug 与规则侧 Bug2（选中规则后按钮灰死）同根因，此前只修不测，故复发。
/// 本文件把「选中项 → 命令可用」的联动钉死，防止再次退化。
/// </summary>
public class FileBackupViewModelCommandTests
{
    /// <summary>
    /// 🔴 2026-09-16 新增：关掉"选中规则时自动查 schtasks"的受控子类。
    /// <para>
    /// 起因：<c>OnSelectedRuleChanged</c> 会 fire-and-forget 调 <c>schtasks /query</c>，
    /// 使本文件每个 <c>vm.SelectedRule = …</c> 的用例都去启动真实外部进程。
    /// 本沙箱里 schtasks.exe 被安全策略拦截 ⇒ 整轮 <c>dotnet test</c> 被 SIGTERM 打断，
    /// 表象是"全量测试跑不完"而非某条用例红灯——定位代价极高。
    /// </para>
    /// <para>
    /// 隔离后本文件**不再依赖**外部进程；定时任务注册查询本身由
    /// <c>IsSchedulerQueryEnabled</c> 保持 production 默认 true，故生产行为零变化。
    /// </para>
    /// </summary>
    private sealed class TestableVm : FileBackupViewModel
    {
        public TestableVm(BackupConfigService config, RuleManager rules)
            : base(config, rules, new BackupService(config), new RestoreService(config),
                   new RestoreService(config), new BackupTaskSchedulerService(new CommandRunner()))
        {
        }

        protected override bool IsSchedulerQueryEnabled => false;
    }

    private static FileBackupViewModel CreateVm()
    {
        string dir = Path.Combine(Path.GetTempPath(), $"fb-vm-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        var config = new BackupConfigService(dir);
        config.Load();
        var rules = new RuleManager(Path.Combine(dir, "rules"), null);
        return CreateVm(config, rules);
    }

    /// <summary>用外部传入的 config/rules 构造 VM（供需要预置状态的用例共享实例）。</summary>
    private static FileBackupViewModel CreateVm(BackupConfigService config, RuleManager rules)
        => new TestableVm(config, rules);

    private static RuleRowVm Rule(string name = "T", bool enabled = true)
        => new(new BackupRule { RuleName = name, Enabled = enabled });

    private static SnapshotRowVm Snapshot()
        => new(new SnapshotInfo { SnapshotId = "snap-1", RuleName = "T", FileCount = 1 });

    // ════════ 快照侧（本次 bug 的直接防线） ════════

    [Fact]
    public void NoSelectedSnapshot_VerifyAndRestoreAreDisabled()
    {
        FileBackupViewModel vm = CreateVm();

        Assert.False(vm.VerifySnapshotCommand.CanExecute(null));
        Assert.False(vm.RestoreSnapshotCommand.CanExecute(null));
        Assert.False(vm.DeleteSnapshotCommand.CanExecute(null));
    }

    [Fact]
    public void SelectedSnapshot_EnablesVerifyRestoreDelete()
    {
        FileBackupViewModel vm = CreateVm();

        vm.SelectedSnapshot = Snapshot();

        Assert.True(vm.VerifySnapshotCommand.CanExecute(null));
        Assert.True(vm.RestoreSnapshotCommand.CanExecute(null));
        Assert.True(vm.DeleteSnapshotCommand.CanExecute(null));
    }

    /// <summary>
    /// 🔴 真正的抓手：WPF 按钮不会主动轮询 CanExecute——它只在收到
    /// <c>CanExecuteChanged</c> 事件后才重新求值。若只断言 <c>CanExecute()</c> 返回值
    /// （实时求值，永远正确），就抓不到「按钮一直灰着」这个真实失效路径。
    /// 故此处断言：选中快照后必须<b>触发通知事件</b>。
    /// </summary>
    [Fact]
    public void SelectedSnapshot_MustRaiseCanExecuteChanged_OrButtonsStayGrey()
    {
        FileBackupViewModel vm = CreateVm();
        int raised = 0;
        vm.VerifySnapshotCommand.CanExecuteChanged += (_, _) => raised++;

        vm.SelectedSnapshot = Snapshot();

        Assert.True(raised > 0,
            "选中快照后必须调用 NotifyCanExecuteChanged，否则界面上的「校验/恢复」按钮不会由灰变亮");
    }

    /// <summary>规则侧同类防线（Bug2 复发防线）：选中规则同样必须触发通知。</summary>
    [Fact]
    public void SelectedRule_MustRaiseCanExecuteChanged_OrButtonsStayGrey()
    {
        FileBackupViewModel vm = CreateVm();
        int raised = 0;
        vm.BackupNowCommand.CanExecuteChanged += (_, _) => raised++;

        vm.SelectedRule = Rule();

        Assert.True(raised > 0,
            "选中规则后必须调用 NotifyCanExecuteChanged，否则界面上的「开始备份/编辑/删除」按钮不会由灰变亮");
    }

    /// <summary>
    /// 🟠 V12-F3 回归锁（2026-09-14）：「打开目录」命令原先**漏在 RefreshCanExecute 名单之外**——
    /// 其 CanExecute 同为 <c>CanOperateSelected</c>（依赖 SelectedSnapshot / IsBusy），漏通知的后果
    /// 是该按钮永久灰死。🔴 断言必须是 <c>CanExecuteChanged</c> 触发：只断言 <c>CanExecute()</c>
    /// 返回值抓不到这条失效路径（实时求值恒正确）。
    /// </summary>
    [Fact]
    public void SelectedSnapshot_MustRaiseCanExecuteChanged_ForOpenSnapshotDir()
    {
        FileBackupViewModel vm = CreateVm();
        vm.SelectedRule = Rule();   // CanExecute 判据是 CanOperateSelected（规则 + 快照 + 非忙）
        int raised = 0;
        vm.OpenSnapshotDirCommand.CanExecuteChanged += (_, _) => raised++;

        vm.SelectedSnapshot = Snapshot();

        Assert.True(raised > 0,
            "选中快照后必须通知 OpenSnapshotDirCommand，否则「打开目录」按钮不会由灰变亮（V12-F3 回归）");
        Assert.True(vm.OpenSnapshotDirCommand.CanExecute(null));
    }

    [Fact]
    public void ClearingSelectedSnapshot_AlsoNotifiesOpenSnapshotDir()
    {
        FileBackupViewModel vm = CreateVm();
        vm.SelectedRule = Rule();
        vm.SelectedSnapshot = Snapshot();
        int raised = 0;
        vm.OpenSnapshotDirCommand.CanExecuteChanged += (_, _) => raised++;

        vm.SelectedSnapshot = null;

        // ⚠ 只断言"通知已发出"：CanOperateSelected 的判据是 SelectedRule（快照不是它的条件），
        // 故清空快照后按钮仍可点（点了会提示"未选中快照"）。通知本身是本次缺陷（漏通知）的正题。
        Assert.True(raised > 0, "清空选中后必须通知 OpenSnapshotDirCommand（V12-F3 漏通知回归）");
    }

    [Fact]
    public void ClearingSelectedSnapshot_DisablesThemAgain()
    {
        FileBackupViewModel vm = CreateVm();
        vm.SelectedSnapshot = Snapshot();

        vm.SelectedSnapshot = null;

        Assert.False(vm.VerifySnapshotCommand.CanExecute(null));
        Assert.False(vm.RestoreSnapshotCommand.CanExecute(null));
    }

    // ════════ 规则侧（Bug2 的防线，防二次退化） ════════

    [Fact]
    public void NoSelectedRule_RuleCommandsAreDisabled()
    {
        FileBackupViewModel vm = CreateVm();

        Assert.False(vm.BackupNowCommand.CanExecute(null));
        Assert.False(vm.EditRuleCommand.CanExecute(null));
        Assert.False(vm.DeleteRuleCommand.CanExecute(null));
        Assert.False(vm.ToggleEnableCommand.CanExecute(null));
    }

    [Fact]
    public void SelectedRule_EnablesRuleCommands()
    {
        FileBackupViewModel vm = CreateVm();

        vm.SelectedRule = Rule();

        Assert.True(vm.BackupNowCommand.CanExecute(null));
        Assert.True(vm.EditRuleCommand.CanExecute(null));
        Assert.True(vm.DeleteRuleCommand.CanExecute(null));
        Assert.True(vm.ToggleEnableCommand.CanExecute(null));
    }

    // ════════ 忙态闸门 ════════

    [Fact]
    public void Busy_DisablesAllSelectionDrivenCommands()
    {
        FileBackupViewModel vm = CreateVm();
        vm.SelectedRule = Rule();
        vm.SelectedSnapshot = Snapshot();

        vm.IsBusy = true;

        Assert.False(vm.BackupNowCommand.CanExecute(null));
        Assert.False(vm.VerifySnapshotCommand.CanExecute(null));
        Assert.False(vm.RestoreSnapshotCommand.CanExecute(null));
        Assert.False(vm.EditRuleCommand.CanExecute(null));
        Assert.False(vm.BackupAllCommand.CanExecute(null));
        Assert.False(vm.RestoreAllCommand.CanExecute(null));

        // 🔴 契约变更（🟠 C-🟠-3，v11~v14 后续批次）：「取消」的可执行判据由泛化的 `IsBusy`
        // **收窄为专用的 `IsBackupRunning`**。理由：校验 / 恢复路径同样会置 IsBusy = true，
        // 但这两条路径都**没有**把 `_backupCts.Token` 传下去（`VerifyAsync(info, snapDir, reporter)`
        // 是三参调用），于是原判据下「取消」按钮在校验 / 恢复期间**亮起可点却毫无作用**
        // —— 用户点了界面毫无变化（§六-14 状态诚实化反面）。
        // 故 IsBusy 单独为真时，取消**不再**可执行：
        Assert.False(vm.CancelBackupCommand.CanExecute(null));

        // 只有备份管线启动（同时置 IsBusy + IsBackupRunning）才允许取消：
        vm.IsBackupRunning = true;
        Assert.True(vm.CancelBackupCommand.CanExecute(null));
        Assert.False(vm.BackupNowCommand.CanExecute(null)); // 备份中其它命令仍被 IsBusy 拦住
    }

    // ════════ 批量命令依赖启用规则集合 ════════

    [Fact]
    public void BackupAll_RequiresEnabledRule()
    {
        FileBackupViewModel vm = CreateVm();
        Assert.False(vm.BackupAllCommand.CanExecute(null));

        vm.Rules.Add(Rule("启用规则"));

        Assert.True(vm.BackupAllCommand.CanExecute(null));
        Assert.True(vm.RestoreAllCommand.CanExecute(null));
    }

    [Fact]
    public void DisabledRuleOnly_DoesNotEnableBackupAll()
    {
        FileBackupViewModel vm = CreateVm();

        vm.Rules.Add(Rule("停用规则", enabled: false));

        Assert.False(vm.BackupAllCommand.CanExecute(null));
    }

    // ════════ 执行路径（2026-09-08 真机反馈「校验点击无反应」防线） ════════
    // 根因：ReadSnapshot 误传 SnapshotId → manifest 找不到 → throw 被 AsyncRelayCommand 吞掉。
    // 本用例走真实快照目录执行校验命令，断言日志产出——命令执行路径任何"无声中断"都会在此暴露。

    [Fact]
    public async Task VerifyCommand_ExecutesAgainstRealSnapshot_AndLogsResult()
    {
        string dir = Path.Combine(Path.GetTempPath(), $"fb-exec-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            var config = new BackupConfigService(dir);
            config.Load();
            var rules = new RuleManager(Path.Combine(dir, "rules"), null);
            var rule = new BackupRule { RuleName = "执行路径" };
            rules.Add(rule);
            rules.Save();

            // 造真实快照：快照目录 + files 子目录 + 一个文件 + 完整 manifest
            var manager = SnapshotManager.FromRule(rule, dir, null);
            string snapDir = manager.CreateSnapshotDir();
            string content = "hello-snapshot";
            File.WriteAllText(Path.Combine(snapDir, SnapshotManager.FilesDir, "a.txt"), content);
            var info = new SnapshotInfo
            {
                SnapshotId = "snap-exec",
                RuleId = rule.RuleId,
                RuleName = rule.RuleName,
                BackupPath = Path.Combine(snapDir, SnapshotManager.FilesDir),
                FileCount = 1,
                TotalSize = content.Length,
                ChecksumStatus = "skipped", // 初始"未校验"，校验通过应写回 passed
            };
            info.Files.Add(new FileEntry
            {
                SourcePath = Path.Combine(dir, "a.txt"),
                RelativePath = "a.txt",
                Size = content.Length,
                Sha256 = Convert.ToHexString(
                    System.Security.Cryptography.SHA256.HashData(
                        System.Text.Encoding.UTF8.GetBytes(content))).ToLowerInvariant(),
            });
            manager.WriteSnapshot(snapDir, info);

            // 钉住临时目录为备份根（不用 Initialize：EnsureBackupRoot 会写到真实盘）
            config.Settings.BackupRoot = dir;
            FileBackupViewModel vm = CreateVm(config, rules);
            vm.Rules.Add(new RuleRowVm(rule));
            vm.SelectedRule = vm.Rules.First(r => r.RuleId == rule.RuleId); // 触发快照重载
            string ruleDir = Path.Combine(dir, "Rule_" + rule.RuleId[..8]);
            string[] foundDirs = Directory.Exists(ruleDir)
                ? Directory.GetDirectories(ruleDir, "*", SearchOption.AllDirectories)
                : Array.Empty<string>();
            Assert.True(vm.Snapshots.Count > 0,
                $"Snapshots.Count=0；ruleDir 存在={Directory.Exists(ruleDir)}；" +
                $"子目录=[{string.Join(" | ", foundDirs)}]");
            vm.SelectedSnapshot = vm.Snapshots.First();

            await vm.VerifySnapshotCommand.ExecuteAsync(null);

            // 🔴 断言落盘状态而非 UI 日志：LogFeed.Append 跨线程用 dispatcher.InvokeAsync
            //（不等待），测试宿主里可能投递到不处理消息的 Dispatcher 而永不执行——
            // 用日志断言会 flaky。校验通过必然把 ChecksumStatus 写回 manifest，更可靠。
            SnapshotInfo? after = manager.ReadSnapshot(snapDir);
            Assert.NotNull(after);
            Assert.Equal("passed", after!.ChecksumStatus);
        }
        finally
        {
            try
            {
                Directory.Delete(dir, recursive: true);
            }
            catch
            {
                // 清理失败不影响断言
            }
        }
    }

    // ════════ 保留快照输入（🟠 V12-F5，2026-09-14） ════════
    // 缺陷原状：MaxSnapshotsInput 是 int + XAML 双向绑定 ⇒ 输入 "12a" 时 WPF 类型转换
    // **静默失败**：输入框看着有值、保存后规则仍按旧值落盘（用户以为生效、实际没生效）。
    // 修法：字符串承载 + 就地校验（MaxSnapshotsError）+ 保存拦截（FormError），不静默回落。

    [Fact]
    public void MaxSnapshotsInput_InvalidText_SurfacesInlineError()
    {
        FileBackupViewModel vm = CreateVm();

        vm.MaxSnapshotsInput = "12a";

        Assert.NotEqual("", vm.MaxSnapshotsError);
        Assert.Contains("1", vm.MaxSnapshotsError);   // 文案须给出合法范围
    }

    [Theory]
    [InlineData("")]
    [InlineData("0")]
    [InlineData("101")]
    [InlineData("1 2")]
    public void MaxSnapshotsInput_OutOfRangeOrMessy_SurfacesInlineError(string input)
    {
        FileBackupViewModel vm = CreateVm();

        vm.MaxSnapshotsInput = input;

        Assert.NotEqual("", vm.MaxSnapshotsError);
    }

    [Fact]
    public void MaxSnapshotsInput_ValidText_ClearsInlineError()
    {
        FileBackupViewModel vm = CreateVm();
        vm.MaxSnapshotsInput = "12a";   // 先制造错误态

        vm.MaxSnapshotsInput = "12";    // 改回合法值应就地清错

        Assert.Equal("", vm.MaxSnapshotsError);
    }

    [Fact]
    public void SaveRule_WithValidMaxSnapshots_PersistsParsedValue()
    {
        FileBackupViewModel vm = CreateVm();
        vm.RuleNameInput = "范围校验";
        vm.SourcePathsInput = Path.GetTempPath();
        vm.MaxSnapshotsInput = "12";

        vm.SaveRuleCommand.Execute(null);

        BackupRule saved = Assert.Single(vm.Rules).Model;
        Assert.Equal(12, saved.MaxSnapshots);
    }

    [Fact]
    public void SaveRule_WithInvalidMaxSnapshots_BlocksWithVisibleError()
    {
        FileBackupViewModel vm = CreateVm();
        vm.RuleNameInput = "非法保留数";
        vm.SourcePathsInput = Path.GetTempPath();
        vm.MaxSnapshotsInput = "12a";

        vm.SaveRuleCommand.Execute(null);

        Assert.Empty(vm.Rules);                              // 不落盘（静默回落旧值正是原缺陷的成因）
        Assert.NotEqual("", vm.FormError);                   // 弹窗底部可见错误
        Assert.NotEqual("", vm.MaxSnapshotsError);           // 字段旁就地提示
    }
}
