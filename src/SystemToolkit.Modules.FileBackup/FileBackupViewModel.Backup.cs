using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SystemToolkit.Abstractions;
using SystemToolkit.Core.Backup.Contracts;
using SystemToolkit.Core.Backup.Models;
using SystemToolkit.Core.Backup.Services;
using SystemToolkit.Core.Contracts;
using SystemToolkit.UI.Common;

namespace SystemToolkit.Modules.FileBackup;

public partial class FileBackupViewModel
{
    [RelayCommand(CanExecute = nameof(CanBackupNow))]
    private async Task BackupNowAsync()
    {
        // 🟠 v18-🟠-2（2026-09-16）：补命令体顶层 catch。
        // 此前本命令体无 try —— `RunBackupPipelineAsync` 的 finally 里含 `ReloadSnapshots()`
        // （读磁盘 + 重建行 VM）等可抛动作，异常冒泡到 `AsyncRelayCommand` 会被吞：
        // 用户零反馈、日志零记录。同时收缩 `AsyncCommandCatchBaseline.json` 对应条目。
        try
        {
            if (SelectedRule is null)
            {
                return;
            }

            await RunBackupPipelineAsync(new[] { SelectedRule.Model }).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            Log("[备份] ❌ 备份命令异常：" + ex.Message);
            _logger.Error("备份命令异常", ex);
        }
    }

    private bool CanBackupNow => SelectedRule is not null && !IsBusy;

    /// <summary>备份全部：串行备份所有已启用规则（2026-09-07 用户拍板纳入本轮）。</summary>
    [RelayCommand(CanExecute = nameof(CanBackupAll))]
    private async Task BackupAllAsync()
    {
        // 🟠 v18-🟠-2（2026-09-16）：补命令体顶层 catch —— `ConfirmRequest?.Invoke` 是 View 注入的
        // `MessageBox.Show` 回调，壳层异常（owner 已关闭 / 对话框初始化失败）会抛；此前裸在 try 外
        // ⇒ 绕过命令体 catch 直冲 `AsyncRelayCommand` 吞异常路径（用户零反馈、日志零记录）。
        // 与 DriverManagerViewModel.DeleteSelectedAsync 的 V12-D2 修复同款。同时收缩基线条目。
        try
        {
            var targets = Rules.Where(r => r.Enabled).Select(r => r.Model).ToList();
            if (targets.Count == 0)
            {
                Log("[备份] 没有已启用的规则可备份。");
                return;
            }

            string names = string.Join("\n", targets.Select(r => $"  · {r.RuleName}"));
            if (ConfirmRequest?.Invoke("备份全部",
                    $"将按顺序备份以下 {targets.Count} 个已启用规则：\n{names}\n\n逐项执行、可随时取消（关闭窗口即停），失败项会标注原因。确定继续吗？") != true)
            {
                Log("[备份] 已取消备份全部。");
                return;
            }

            await RunBackupPipelineAsync(targets).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            Log("[备份] ❌ 备份全部命令异常：" + ex.Message);
            _logger.Error("备份全部命令异常", ex);
        }
    }

    private bool CanBackupAll => !IsBusy && Rules.Any(r => r.Enabled);

    /// <summary>
    /// 备份流水线闸门：单规则与「备份全部」共用——设忙态、逐条调核心、
    /// 结束统一恢复状态并刷新快照。核心异常自理，取消中断剩余项。
    /// </summary>
    private async Task RunBackupPipelineAsync(IReadOnlyList<BackupRule> targets)
    {
        _backupCts = new CancellationTokenSource();
        IsBusy = true;
        IsBackupRunning = true; // 🟠 C-🟠-3：只有备份这一步是可取消的（须在 RefreshCanExecute 之前）
        HasProgress = true;
        ProgressValue = 0;
        RefreshCanExecute();
        int ok = 0, fail = 0;
        try
        {
            for (int i = 0; i < targets.Count; i++)
            {
                BackupRule rule = targets[i];
                if (targets.Count > 1)
                {
                    ProgressText = $"[{i + 1}/{targets.Count}] {rule.RuleName}";
                    ProgressValue = 0;
                }

                try
                {
                    if (await BackupRuleCoreAsync(rule).ConfigureAwait(true))
                    {
                        ok++;
                    }
                    else
                    {
                        fail++;
                    }
                }
                catch (OperationCanceledException)
                {
                    Log(targets.Count > 1
                        ? $"[备份] ⏹ 已取消：{rule.RuleName}（剩余规则不再执行）"
                        : "[备份] 已取消。");
                    break;
                }
            }

            if (targets.Count > 1)
            {
                Log($"[备份] 备份全部结束：成功 {ok}、失败 {fail}（共 {targets.Count} 条）。");
            }
        }
        finally
        {
            IsBusy = false;
            IsBackupRunning = false; // 🟠 C-🟠-3
            HasProgress = false;
            ProgressText = "";
            _backupCts.Dispose();
            _backupCts = null;
            RefreshCanExecute();
            ReloadSnapshots();
        }
    }

    /// <summary>单规则备份核心（无闸门）：返回是否成功；取消以 OperationCanceledException 上抛。</summary>
    private async Task<bool> BackupRuleCoreAsync(BackupRule rule)
    {
        try
        {
            var reporter = new UiProgressReporter(OnProgress, phase => ProgressText = phase, Log, _dispatcher);
            Log($"[备份] ▶ 开始备份：{rule.RuleName}（{rule.Sources().Count} 个源）");
            BackupResult result = await _backup.BackupRuleAsync(rule, reporter, _backupCts!.Token).ConfigureAwait(true);
            // 审查 O1：服务层把取消咽成 Canceled 返回（headless 记账依赖返回值），此处在 VM 侧恢复
            // 本方法"取消以 OCE 上抛"的契约，令上层"备份全部"取消的 break 真正生效
            if (result.Canceled)
            {
                throw new OperationCanceledException();
            }

            Log(result.Success
                ? $"[备份] ✅ {result.Message}（{result.FileCount} 个文件，{FormatSize(result.TotalSize)}，校验 {result.ChecksumStatus}）"
                : $"[备份] ⚠️ {result.Message}（失败 {result.Failures.Count} 项，详见日志）");
            foreach (string failure in result.Failures.Take(10))
            {
                Log("[备份]   ✗ " + failure);
            }

            // B5a：备份后的读回校验报告。🔴 未通过必须**显式告警**，不能只落在日志里
            if (result.VerifyReport is { } verify)
            {
                Log(verify.Success ? $"[备份] 完整性校验：{verify.Message}" : $"[备份] ⚠️ 完整性校验未通过：{verify.Message}");
                foreach (string failure in verify.Failures.Take(10))
                {
                    Log("[备份]   ✗ " + failure);
                }

                if (!verify.Success)
                {
                    _logger.Warn($"备份完整性校验未通过：{rule.RuleName} {verify.Message}");
                }
            }
            else
            {
                Log("[备份] ⚠️ 本次未取得完整性校验证据（按设置关闭 / 被取消 / 校验未能完成）——"
                    + "快照状态为 skipped，如需完整结论请用「校验快照」");
            }

            _logger.Info($"备份完成：{rule.RuleName} success={result.Success} files={result.FileCount}");
            return result.Success;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            Log("[备份] ❌ 备份失败：" + ex.Message);
            _logger.Error("备份失败", ex);
            return false;
        }
    }

    /// <summary>是否正在跑**可取消**的备份。与 <c>IsBusy</c> 分离：<c>IsBusy</c> 还覆盖
    /// 校验 / 恢复等不可取消的操作（它们没把 <c>_backupCts.Token</c> 传下去）。</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CancelBackupCommand))]
    private bool _isBackupRunning;

    [RelayCommand(CanExecute = nameof(CanCancelBackup))]
    private void CancelBackup() => _backupCts?.Cancel();

    // 🟠 C-🟠-3 v11~v14 后续批次：原先判据是泛化的 `IsBusy` ⇒ 校验/恢复路径也会置
    // IsBusy = true，于是「取消」按钮在**校验 / 恢复期间亮起可点**，但 CancelBackup 只做
    // `_backupCts?.Cancel()`，对它们毫无影响（那两条路径都把 Token 传给了自己的服务）
    // ⇒ 用户点了"取消"界面毫无变化。改用只跟备份的专用标志
    //（对齐 DriverManager 的 IsBackupRunning 范式）。
    private bool CanCancelBackup => IsBackupRunning;

    private void RefreshCanExecute()
    {
        BackupNowCommand.NotifyCanExecuteChanged();
        BackupAllCommand.NotifyCanExecuteChanged();
        CancelBackupCommand.NotifyCanExecuteChanged();
        SaveRuleCommand.NotifyCanExecuteChanged();
        NewRuleCommand.NotifyCanExecuteChanged();
        EditRuleCommand.NotifyCanExecuteChanged();
        DeleteRuleCommand.NotifyCanExecuteChanged();
        ToggleEnableCommand.NotifyCanExecuteChanged();
        RestoreSnapshotCommand.NotifyCanExecuteChanged();
        VerifySnapshotCommand.NotifyCanExecuteChanged();
        RestoreAllCommand.NotifyCanExecuteChanged();
        DeleteSnapshotCommand.NotifyCanExecuteChanged();
        // 🟠 V12-F3：OpenSnapshotDirCommand 原先漏在本名单之外 —— 其 CanExecute 同样是
        // CanOperateSelected（依赖 SelectedSnapshot 与 IsBusy，而 ObservableProperty 的 setter
        // 不会自动通知 RelayCommand，本仓已两次实证），漏通知的后果是「打开目录」按钮永久灰死。
        OpenSnapshotDirCommand.NotifyCanExecuteChanged();
    }

    private void OnProgress(int done, int total, string phase)
    {
        ProgressText = $"{phase} {done}/{total}";
        ProgressValue = total > 0 ? done * 100.0 / total : 0;
    }
}

