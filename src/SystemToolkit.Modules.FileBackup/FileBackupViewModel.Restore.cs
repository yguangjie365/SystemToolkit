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
    // ── 快照 ──

    private SnapshotManager? SnapshotManagerForSelected()
        => SelectedRule is null ? null : SnapshotManager.FromRule(SelectedRule.Model, GlobalRoot, _logger);

    [RelayCommand]
    private void ReloadSnapshots()
    {
        Snapshots.Clear();
        if (SnapshotManagerForSelected() is not { } manager)
        {
            return;
        }

        foreach (SnapshotInfo info in manager.SnapshotsLight())
        {
            Snapshots.Add(new SnapshotRowVm(info));
        }
    }

    [RelayCommand(CanExecute = nameof(CanOperateSelected))]
    private void OpenSnapshotDir()
    {
        if (SelectedSnapshot is null || SnapshotManagerForSelected() is not { } manager)
        {
            return;
        }

        // 按 SnapshotId 定位快照目录（目录名是时间戳，与 Id 不同名）
        string? dir = manager.AllSnapshotDirs()
            .FirstOrDefault(d => manager.ReadSnapshotLight(d)?.SnapshotId == SelectedSnapshot.SnapshotId);
        if (dir is null)
        {
            Log("[备份] ❌ 未找到该快照的目录（可能已被手动删除）");
            return;
        }

        SnapshotOpenResult result = new SnapshotPathOpener().Open(dir, manager.IsSnapshotDirAllowed);
        Log(result.Kind switch
        {
            SnapshotOpenResultKind.Opened => $"[备份] 已打开：{result.FullPath}",
            SnapshotOpenResultKind.Denied => "[备份] ❌ 路径越界，拒绝打开（非本规则快照目录）",
            SnapshotOpenResultKind.NotFound => "[备份] ❌ 快照目录不存在",
            _ => "[备份] ❌ 路径非法，拒绝打开",
        });
    }

    // ── 快照校验（2026-09-07 补齐旧版「校验」命令；Core SnapshotVerifier 重算哈希比对） ──

    /// <summary>按 SnapshotId 定位快照目录（目录名是时间戳，与 Id 不同名）。</summary>
    private string? SnapshotDirOf(SnapshotManager manager, string snapshotId)
        => manager.AllSnapshotDirs()
            .FirstOrDefault(d => manager.ReadSnapshotLight(d)?.SnapshotId == snapshotId);

    [RelayCommand(CanExecute = nameof(CanVerify))]
    private async Task VerifySnapshotAsync()
    {
        if (SelectedSnapshot is null || SelectedRule is null || SnapshotManagerForSelected() is not { } manager)
        {
            return;
        }

        string? snapDir = SnapshotDirOf(manager, SelectedSnapshot.SnapshotId);
        if (snapDir is null)
        {
            Log("[校验] ❌ 未找到该快照的目录（可能已被手动删除）");
            return;
        }

        // 🔴 2026-09-08 修复：ReadSnapshot 的参数是「快照目录路径」，此前误传 SnapshotId
        // → manifest 永远找不到 → throw 被 AsyncRelayCommand 吞掉 → 点击无任何反应
        // 审查 O16（2026-09-10）：万级快照 manifest 读取+反序列化不应占 UI 线程
        SnapshotInfo? info = await Task.Run(() => manager.ReadSnapshot(snapDir)).ConfigureAwait(true);
        if (info is null)
        {
            Log("[校验] ❌ 快照清单读取失败（manifest.json 缺失或损坏）");
            return;
        }

        IsBusy = true;
        HasProgress = true;
        ProgressValue = 0;
        RefreshCanExecute();
        try
        {
            var verifier = new SnapshotVerifier(_config.Settings.MaxWorkers, _logger);
            var reporter = new UiProgressReporter(OnProgress, phase => ProgressText = phase, Log, _dispatcher);
            Log($"[校验] ▶ 开始校验「{SelectedRule.RuleName}」{SelectedSnapshot.DisplayTime}（{info.Files.Count} 个文件）");
            SnapshotVerifyReport report = await verifier.VerifyAsync(info, snapDir, reporter).ConfigureAwait(true);
            Log(report.Success ? $"[校验] ✅ {report.Message}" : $"[校验] ⚠️ {report.Message}");
            foreach (string failure in report.Failures)
            {
                Log("[校验]   ✗ " + failure);
            }

            // 结果写回快照状态（原子写），列表徽章随之更新。审查 O16：序列化+落盘移出 UI 线程
            info.ChecksumStatus = report.Success ? ChecksumStatuses.Passed : ChecksumStatuses.Failed;
            await Task.Run(() => manager.WriteSnapshot(snapDir, info)).ConfigureAwait(true);
            ReloadSnapshots();
        }
        catch (OperationCanceledException)
        {
            Log("[校验] 已取消。");
        }
        catch (Exception ex)
        {
            Log("[校验] ❌ 校验失败：" + ex.Message);
            _logger.Error("快照校验失败", ex);
        }
        finally
        {
            IsBusy = false;
            HasProgress = false;
            ProgressText = "";
            RefreshCanExecute();
        }
    }

    private bool CanVerify => SelectedSnapshot is not null && !IsBusy;

    [RelayCommand(CanExecute = nameof(CanRestore))]
    private async Task RestoreSnapshotAsync()
    {
        if (SelectedSnapshot is null || SelectedRule is null || SnapshotManagerForSelected() is not { } manager)
        {
            return;
        }

        // 🔴 2026-09-08 修复：同校验侧——ReadSnapshot 参数是「快照目录路径」，
        // 误传 SnapshotId → manifest 找不到 → throw 被吞 → 恢复点击无任何反应
        string? snapDir = SnapshotDirOf(manager, SelectedSnapshot.SnapshotId);
        if (snapDir is null)
        {
            Log("[恢复] ❌ 未找到该快照的目录（可能已被手动删除）");
            return;
        }

        // 审查 O16（2026-09-10）：万级快照 manifest 读取+反序列化移出 UI 线程
        SnapshotInfo? info = await Task.Run(() => manager.ReadSnapshot(snapDir)).ConfigureAwait(true);
        if (info is null)
        {
            Log("[恢复] ❌ 快照清单读取失败（manifest.json 缺失或损坏）");
            return;
        }

        string original = SelectedRule.Model.Sources().FirstOrDefault() ?? "";

        // 2026-09-07 补齐旧版恢复向导：目标二选一 + 冲突策略四选一（不再写死 Rename）
        string summary = $"规则「{info.RuleName}」· 快照 {info.DisplayTime}（{info.FileCount} 个文件，{info.SizeText}）";
        RestoreChoice? choice = RestoreRequest?.Invoke(summary, original);
        if (choice is null)
        {
            Log("[恢复] 已取消（未确认恢复选项）");
            return;
        }

        string target = choice.TargetRoot ?? original;
        if (string.IsNullOrWhiteSpace(target) || !Directory.Exists(target))
        {
            Log("[恢复] ❌ 恢复目标不存在：" + target);
            return;
        }

        // 冲突预演（与真实恢复同解析规则，只读探测不落盘）
        RestorePreviewReport preview = await _preview.PreviewConflictsAsync(
            info, target, SelectedRule.Model.Sources().ToList()).ConfigureAwait(true);
        string confirm = $"恢复预演（{preview.Total} 个文件）→ {target}\n\n" +
            $"· 目标已存在：{preview.ExistsCount}（冲突策略：{PolicyText(choice.Policy)}）\n" +
            $"· 恢复时会被安全校验拒绝：{preview.BlockedCount}\n" +
            $"· 全新写入：{preview.Total - preview.ExistsCount - preview.BlockedCount}\n\n确定执行恢复吗？";
        if (ConfirmRequest?.Invoke("恢复快照", confirm) != true)
        {
            Log("[恢复] 已取消（预演后未确认）");
            return;
        }

        await RestoreCoreAsync(info, SelectedRule.Model, target, choice.Policy).ConfigureAwait(true);
    }

    private static string PolicyText(ConflictPolicy policy) => policy switch
    {
        ConflictPolicy.Overwrite => "覆盖",
        ConflictPolicy.Rename => "重命名保留两者",
        ConflictPolicy.Skip => "跳过",
        _ => "逐条询问",
    };

    /// <summary>单快照恢复核心（单条恢复与「恢复全部」共用）：闸门 + 结果汇报。</summary>
    private async Task RestoreCoreAsync(SnapshotInfo info, BackupRule rule, string target, ConflictPolicy policy)
    {
        IsBusy = true;
        HasProgress = true;
        RefreshCanExecute();
        try
        {
            var reporter = new UiProgressReporter(OnProgress, phase => ProgressText = phase, Log, _dispatcher);
            Log($"[恢复] ▶ 开始恢复「{info.RuleName}」→ {target}");
            RestoreReport report = await _restore.RestoreSnapshotAsync(
                info, target, policy,
                userChoice: null, reporter: reporter,
                trustedRoots: rule.Sources().ToList()).ConfigureAwait(true);
            Log(report.Success
                ? $"[恢复] ✅ {report.Message}（恢复 {report.Restored}/{report.Total}，跳过 {report.Skipped}）"
                : $"[恢复] ⚠️ {report.Message}（成功 {report.Restored}，失败 {report.Failed}，校验不一致 {report.VerifyFailed}，跳过 {report.Skipped}）");
            foreach (string failure in report.Failures.Take(10))
            {
                Log("[恢复]   ✗ " + failure);
            }

            _logger.Info($"快照恢复：{info.SnapshotId} restored={report.Restored}/{report.Total} to={target} policy={policy}");
        }
        catch (Exception ex)
        {
            Log("[恢复] ❌ 恢复失败：" + ex.Message);
            _logger.Error("快照恢复失败", ex);
        }
        finally
        {
            IsBusy = false;
            HasProgress = false;
            ProgressText = "";
            RefreshCanExecute();
        }
    }

    /// <summary>
    /// 恢复全部（2026-09-07 主人拍板）：启用规则各自恢复最新快照；目标与冲突策略统一问一次，
    /// 逐条执行并汇报。旧版有此高危入口（红色按钮），本项目以 RowDangerButton + 双重确认承接。
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanRestoreAll))]
    private async Task RestoreAllAsync()
    {
        // 审查 v6（O-3b）：与兄弟入口（RestoreSnapshotAsync / BackupAllAsync）同构——IsBusy 闸门 + catch 兜底。
        // v5 O-3 只做了离线程，漏了两者，竞态异常会被 AsyncRelayCommand 吞成静默空操作。
        IsBusy = true;
        RefreshCanExecute();
        try
        {
            // 审查 v5（O-3）+ v6（O-3b）：LatestSnapshot() 的目录+manifest 同步 IO 必须离 UI 线程；
            // 但 Rules 是 UI 绑定的 ObservableCollection——**在 UI 线程先物化**，线程池只摸不可变快照，
            // 杜绝"探测期间用户改规则"的跨线程枚举竞态。
            var enabled = Rules.Where(r => r.Enabled).ToList();
            List<(BackupRule Rule, SnapshotInfo Info)> targets = await Task.Run(() =>
            {
                var found = new List<(BackupRule Rule, SnapshotInfo Info)>();
                foreach (RuleRowVm row in enabled)
                {
                    var manager = SnapshotManager.FromRule(row.Model, GlobalRoot, _logger);
                    SnapshotInfo? latest = manager.LatestSnapshot();
                    if (latest is not null)
                    {
                        found.Add((row.Model, latest));
                    }
                }

                return found;
            }).ConfigureAwait(true);

            if (targets.Count == 0)
            {
                Log("[恢复] 没有可恢复的快照（已启用规则均无快照）。");
                return;
            }

            string list = string.Join("\n", targets.Select(t => $"  · {t.Rule.RuleName}（{t.Info.DisplayTime}，{t.Info.FileCount} 个文件）"));
            RestoreChoice? choice = RestoreRequest?.Invoke(
                $"将恢复以下 {targets.Count} 个已启用规则的最新快照：\n\n{list}",
                "各规则自身的原始源路径");
            if (choice is null)
            {
                Log("[恢复] 已取消恢复全部。");
                return;
            }

            if (ConfirmRequest?.Invoke("恢复全部",
                    $"即将恢复 {targets.Count} 个规则的最新快照。\n" +
                    $"目标：{choice.TargetRoot ?? "各规则原始位置"}\n" +
                    $"冲突策略：{PolicyText(choice.Policy)}\n\n" +
                    "⚠️ 这是破坏性操作，可能覆盖现有文件。确定继续吗？") != true)
            {
                Log("[恢复] 已取消恢复全部。");
                return;
            }

            foreach ((BackupRule rule, SnapshotInfo info) in targets)
            {
                string target = choice.TargetRoot ?? rule.Sources().FirstOrDefault() ?? "";
                if (choice.TargetRoot is null && rule.Sources().Count() > 1)
                {
                    // 🟠-4 产品语义现状：未指定目标时仅回首个源（多源原位还原待 BKP-3 收口时定夺）——显式日志不静默
                    Log($"[恢复] ⚠️ 「{rule.RuleName}」为多源规则，当前仅以首个源作为恢复目标：{target}");
                }

                if (string.IsNullOrWhiteSpace(target) || !Directory.Exists(target))
                {
                    Log($"[恢复] ⚠️ 跳过「{rule.RuleName}」：恢复目标不存在（{target}）");
                    continue;
                }

                await RestoreCoreAsync(info, rule, target, choice.Policy).ConfigureAwait(true);
            }

            Log($"[恢复] 恢复全部结束（共 {targets.Count} 条）。");
        }
        catch (OperationCanceledException)
        {
            Log("[恢复] ⚠️ 恢复全部已取消。");
        }
        catch (Exception ex)
        {
            // v6 O-3b：命令体兜底（AsyncRelayCommand 会吞异常，用户此前看不到任何结果）
            Log("[恢复] ❌ 恢复全部失败：" + ex.Message);
            _logger.Error("恢复全部失败", ex);
        }
        finally
        {
            IsBusy = false;
            RefreshCanExecute();
        }
    }

    private bool CanRestoreAll => !IsBusy && Rules.Any(r => r.Enabled);

    private static bool ruleSourcesContain(SnapshotInfo info, string target)
        => info.SourcePaths.Concat(new[] { info.SourcePath })
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .Any(s => string.Equals(
                Path.GetFullPath(s!).TrimEnd(Path.DirectorySeparatorChar),
                Path.GetFullPath(target).TrimEnd(Path.DirectorySeparatorChar),
                StringComparison.OrdinalIgnoreCase));

    private bool CanRestore => SelectedSnapshot is not null && !IsBusy;

    [RelayCommand(CanExecute = nameof(CanRestore))]
    private void DeleteSnapshot()
    {
        if (SelectedSnapshot is null || SnapshotManagerForSelected() is not { } manager)
        {
            return;
        }

        SnapshotRowVm target = SelectedSnapshot;
        // 审查 🟠-3 采纳：只展示该快照自身信息，不展示 SnapRoot（易被误解为要删整棵规则快照树）
        if (ConfirmRequest?.Invoke("删除快照",
                $"确定删除 {target.DisplayTime} 的快照吗？\n" +
                $"（{target.FileCount} 个文件，{target.SizeText}）\n\n此操作不可撤销。") != true)
        {
            return;
        }

        try
        {
            // 审查 O1（2026-09-10）：DeleteSnapshot 收的是目录路径——误传 SnapshotId 会让
            // Directory.Exists 恒 false → 静默空操作 + 假成功（已修 ReadSnapshot 的姊妹路径漏网此处）
            string? snapDir = SnapshotDirOf(manager, target.SnapshotId);
            if (snapDir is null)
            {
                Log("[备份] ❌ 未找到该快照目录（可能已被手动删除）");
                return;
            }

            manager.DeleteSnapshot(snapDir);
            Log($"[备份] 快照已删除：{target.DisplayTime}");
        }
        catch (Exception ex)
        {
            Log("[备份] ❌ 快照删除失败：" + ex.Message);
            return;
        }

        ReloadSnapshots();
    }
}

