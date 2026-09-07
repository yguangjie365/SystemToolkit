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
        => new(config, rules, new BackupService(config), new RestoreService(config),
            new RestoreService(config), new BackupTaskSchedulerService(new CommandRunner()));

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
        // 忙态下仍应可取消
        Assert.True(vm.CancelBackupCommand.CanExecute(null));
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
}
