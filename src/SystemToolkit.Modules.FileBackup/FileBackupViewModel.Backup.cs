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
        if (SelectedRule is null)
        {
            return;
        }

        await RunBackupPipelineAsync(new[] { SelectedRule.Model }).ConfigureAwait(true);
    }

    private bool CanBackupNow => SelectedRule is not null && !IsBusy;

    /// <summary>备份全部：串行备份所有已启用规则（2026-09-07 用户拍板纳入本轮）。</summary>
    [RelayCommand(CanExecute = nameof(CanBackupAll))]
    private async Task BackupAllAsync()
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

    private bool CanBackupAll => !IsBusy && Rules.Any(r => r.Enabled);

    /// <summary>
    /// 备份流水线闸门：单规则与「备份全部」共用——设忙态、逐条调核心、
    /// 结束统一恢复状态并刷新快照。核心异常自理，取消中断剩余项。
    /// </summary>
    private async Task RunBackupPipelineAsync(IReadOnlyList<BackupRule> targets)
    {
        _backupCts = new CancellationTokenSource();
        IsBusy = true;
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
            var reporter = new UiProgressReporter(OnProgress, phase => ProgressText = phase, Log);
            Log($"[备份] ▶ 开始备份：{rule.RuleName}（{rule.Sources().Count} 个源）");
            BackupResult result = await _backup.BackupRuleAsync(rule, reporter, _backupCts!.Token).ConfigureAwait(true);
            Log(result.Success
                ? $"[备份] ✅ {result.Message}（{result.FileCount} 个文件，{FormatSize(result.TotalSize)}，校验 {result.ChecksumStatus}）"
                : $"[备份] ⚠️ {result.Message}（失败 {result.Failures.Count} 项，详见日志）");
            foreach (string failure in result.Failures.Take(10))
            {
                Log("[备份]   ✗ " + failure);
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

    [RelayCommand(CanExecute = nameof(CanCancelBackup))]
    private void CancelBackup() => _backupCts?.Cancel();

    private bool CanCancelBackup => IsBusy;

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
    }

    private void OnProgress(int done, int total, string phase)
    {
        ProgressText = $"{phase} {done}/{total}";
        ProgressValue = total > 0 ? done * 100.0 / total : 0;
    }
}

