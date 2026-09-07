using System.IO;
using CommunityToolkit.Mvvm.Input;
using SystemToolkit.Core.Drivers;

namespace SystemToolkit.Modules.DriverManager;

public partial class DriverManagerViewModel
{
    // ==================================================================
    // 提权删除 / 批量导出 / 添加安装（经 Elevated Helper，单次 UAC 覆盖整批）
    // ==================================================================

    /// <summary>删除勾选的第三方驱动包（pnputil /delete-driver）。系统关键（含启动关键）防御性排除。</summary>
    [RelayCommand(CanExecute = nameof(CanOperate))]
    private async Task DeleteSelectedAsync()
    {
        var targets = Packages
            .Where(p => p.IsSelected && p.IsThirdParty && !p.IsSystemCritical)
            .ToList();
        if (targets.Count == 0)
        {
            AddLog("删除未启动：勾选中没有可删除的第三方驱动包（收件箱/启动关键驱动不可删除）。");
            return;
        }

        string names = string.Join("\n", targets.Select(p => $"  {p.InfName}（{p.Provider} {p.Version}）"));
        if (ConfirmRequest?.Invoke("删除驱动确认",
                $"将从 Driver Store 删除以下 {targets.Count} 个驱动包：\n{names}\n\n"
                + "删除后设备可能失去当前驱动（重新扫描硬件可自动重装）。\n确定继续吗？") != true)
        {
            AddLog("已取消删除。");
            return;
        }

        await RunPrivilegedBatchAsync(targets, force: false, actionName: "删除");
    }

    /// <summary>强制删除勾选的第三方驱动包（/force，连设备关联一并移除——红色危险操作）。</summary>
    [RelayCommand(CanExecute = nameof(CanOperate))]
    private async Task ForceDeleteSelectedAsync()
    {
        var targets = Packages
            .Where(p => p.IsSelected && p.IsThirdParty && !p.IsSystemCritical)
            .ToList();
        if (targets.Count == 0)
        {
            AddLog("强制删除未启动：勾选中没有可删除的第三方驱动包。");
            return;
        }

        string names = string.Join("\n", targets.Select(p => $"  {p.InfName}（{p.Provider} {p.Version}）"));
        if (ConfirmRequest?.Invoke("⚠ 强制删除确认",
                $"将强制删除以下 {targets.Count} 个驱动包（/force，设备关联一并移除）：\n{names}\n\n"
                + "关联设备会立即失去驱动并可能停用，仅建议在驱动引发故障时使用。\n确定继续吗？") != true)
        {
            AddLog("已取消强制删除。");
            return;
        }

        await RunPrivilegedBatchAsync(targets, force: true, actionName: "强制删除");
    }

    /// <summary>备份向导入口：View 弹窗收集范围与目录，编排下沉 DriverBackupService（Core）。</summary>
    [RelayCommand(CanExecute = nameof(CanOperate))]
    private async Task RunBackupAsync()
    {
        (IReadOnlyList<string> Names, string DestDir, bool AllThirdParty)? request = BackupWizardRequest?.Invoke();
        if (request is null)
        {
            return; // 用户取消向导
        }

        (IReadOnlyList<string> names, string destDir, bool allThirdParty) = request.Value;
        DriverBackupScope scope = allThirdParty ? DriverBackupScope.ThirdPartyOnly : DriverBackupScope.All;
        IsOperating = true;
        try
        {
            Progress<string> progress = new(t => StatusText = t);
            DriverBackupResult result = await _backup.BackupAsync(
                names, Packages.Select(p => p.Model).ToList(), destDir, scope, progress).ConfigureAwait(true);

            if (result.Missing.Count > 0)
            {
                AddLog($"⚠ {result.Missing.Count} 个包未在目标目录发现导出结果：\n{string.Join("\n", result.Missing.Select(n => "  " + n))}");
                _logger.Warn($"导出实证校验：缺失 {string.Join(", ", result.Missing)}");
            }

            if (result.Exported.Count > 0)
            {
                if (result.MovedDirs > 0)
                {
                    AddLog($"✅ 已按设备类别整理 {result.MovedDirs} 个驱动包目录" +
                           (result.SkippedDirs > 0 ? $"（跳过 {result.SkippedDirs} 个无法识别的目录）" : ""));
                }

                AddLog("✅ 备份三件套已生成：manifest.json / devices.json / checksum.json");
                _logger.Info($"驱动备份三件套生成完成：{destDir}（{result.Exported.Count} 包）");

                if (result.Raw.Success)
                {
                    AddLog($"✅ 导出完成：{names.Count} 个驱动包 → {destDir}");
                    StatusText = $"备份完成：{names.Count} 个驱动包 + 三件套 → {destDir}";
                    _logger.Info($"驱动备份完成：{names.Count} 个包 → {destDir}");
                }
                else
                {
                    AddLog($"⚠ 导出部分失败（退出码 {result.Raw.ExitCode}），已成功导出 {result.Exported.Count}/{names.Count} 个包，三件套按实际内容生成；明细见日志");
                    StatusText = $"备份部分完成：{result.Exported.Count}/{names.Count} 个包 → {destDir}";
                    _logger.Warn($"驱动备份部分失败：退出码 {result.Raw.ExitCode}，成功 {result.Exported.Count} 包");
                }
            }
            else
            {
                AddLog($"❌ 导出失败（退出码 {result.Raw.ExitCode}）：\n{result.Raw.Output}");
                StatusText = $"导出失败（退出码 {result.Raw.ExitCode}），明细见日志";
                _logger.Warn($"驱动备份失败：退出码 {result.Raw.ExitCode}");
            }
        }
        catch (Exception ex)
        {
            AddLog($"❌ 备份过程异常：{ex.Message}");
            StatusText = "备份异常，明细见日志";
            _logger.Error("驱动备份异常", ex);
        }
        finally
        {
            IsOperating = false;
        }
    }

    /// <summary>添加/安装驱动：目录选择 → 递归找 *.inf → 确认 → 提权执行 → 重扫（RAPR ButtonAddDriver 同款流程）。
    /// CommandParameter："add" = 仅入 Store；"install" = 入 Store 并安装到匹配设备。</summary>
    [RelayCommand(CanExecute = nameof(CanOperate))]
    private async Task AddDriversAsync(string? mode)
    {
        bool install = string.Equals(mode, "install", StringComparison.OrdinalIgnoreCase);
        string actionName = install ? "安装" : "添加";
        string? folder = AddSourceFolderRequest?.Invoke();
        if (folder is null)
        {
            AddLog($"已取消{actionName}。");
            return;
        }

        if (!Directory.Exists(folder))
        {
            AddLog($"{actionName}未启动：目录不存在——{folder}");
            return;
        }

        List<string> infs = Directory.GetFiles(folder, "*.inf", SearchOption.AllDirectories).ToList();
        if (infs.Count == 0)
        {
            AddLog($"{actionName}未启动：所选目录（含子目录）未找到任何 .inf 文件——{folder}");
            return;
        }

        string preview = string.Join("\n", infs.Take(10).Select(p => "  " + Path.GetFileName(p)))
            + (infs.Count > 10 ? $"\n  …等共 {infs.Count} 个" : "");
        if (ConfirmRequest?.Invoke($"{actionName}驱动确认",
                $"将{actionName}以下 {infs.Count} 个 INF 到 Driver Store{(install ? "并安装到匹配设备" : "")}：\n{preview}\n\n"
                + "来源不可信的驱动可能危害系统安全，请确认来源可靠。\n确定继续吗？") != true)
        {
            AddLog($"已取消{actionName}。");
            return;
        }

        IsOperating = true;
        try
        {
            StatusText = $"正在{actionName} {infs.Count} 个 INF（已弹出 UAC，请确认）…";
            // 单 INF 走 AddDriverAsync（保留计数双判）；批量走 AddManyAsync（逐段退出码判定）
            DriverRunResult result = infs.Count == 1
                ? await _pnputil.AddDriverAsync(infs[0], install).ConfigureAwait(true)
                : await _pnputil.AddManyAsync(infs, install).ConfigureAwait(true);

            if (result.Success)
            {
                AddLog($"✅ {actionName}完成：{infs.Count} 个 INF 已提交到 Driver Store");
                StatusText = $"{actionName}完成：{infs.Count} 个 INF";
                _logger.Info($"驱动{actionName}完成：{infs.Count} 个 INF ← {folder}");
            }
            else
            {
                List<(int ExitCode, string Description)> failedSegments = PnpUtilOutputAnalyzer.ExtractFailedSegments(result.Output);
                string detail = failedSegments.Count > 0
                    ? "\n失败子命令：\n" + string.Join("\n", failedSegments.Select(f => $"  [exit {f.ExitCode}] {f.Description}"))
                    : $"\n{result.Output}";
                AddLog($"❌ {actionName}失败（退出码 {result.ExitCode}）：{detail}");
                StatusText = $"{actionName}失败（退出码 {result.ExitCode}），明细见日志";
                _logger.Warn($"驱动{actionName}失败：退出码 {result.ExitCode}，失败段 {failedSegments.Count} 个");
            }

            await ScanAsync().ConfigureAwait(true);
        }
        finally
        {
            IsOperating = false;
        }
    }

    /// <summary>提权批量执行共用通道：单次 UAC 整批执行 → 重扫 Driver Store → 基于新列表复核真实状态。</summary>
    private async Task RunPrivilegedBatchAsync(List<DriverPackageVm> targets, bool force, string actionName)
    {
        IsOperating = true;
        try
        {
            StatusText = $"正在{actionName} {targets.Count} 个驱动包（已弹出 UAC，请确认）…";
            DriverRunResult result = await _pnputil.DeleteManyAsync(
                targets.Select(p => p.InfName).ToList(), force).ConfigureAwait(true);

            // 🔴 先重扫再复核（D-1 修复，2026-09-05）：复核必须基于删除后的新列表——
            // 旧实现复核先于重扫读取旧集合，导致每次成功删除都被误报"0/N 已移除"
            await ScanAsync().ConfigureAwait(true);

            if (result.Success)
            {
                var remaining = targets
                    .Where(t => Packages.Any(p => p.InfName.Equals(t.InfName, StringComparison.OrdinalIgnoreCase)))
                    .Select(t => t.InfName)
                    .ToList();
                if (remaining.Count == 0)
                {
                    AddLog($"✅ {actionName}完成：{targets.Count} 个驱动包已从 Driver Store 移除（重扫复核通过）");
                    StatusText = $"{actionName}完成：{targets.Count} 个驱动包";
                    _logger.Info($"驱动{actionName}完成：{targets.Count} 个包");
                }
                else
                {
                    AddLog($"⚠ {actionName}部分完成：{targets.Count - remaining.Count}/{targets.Count} 已移除，" +
                           $"以下包仍存在于 Store（可能被设备重新挂载）：\n{string.Join("\n", remaining.Select(n => "  " + n))}" +
                           (force ? "" : "\n💡 提示：驱动可能正被设备使用。若确认要移除，可尝试「强制删除」（会连设备关联一并卸载）。"));
                    StatusText = $"{actionName}部分完成：{targets.Count - remaining.Count}/{targets.Count}，明细见日志";
                    _logger.Warn($"驱动{actionName}部分完成：残留 {string.Join(", ", remaining)}");
                }
            }
            else
            {
                // 分段失败明细（ElevatedHelper 输出含 [exit N] 标记行，语言无关）
                List<(int ExitCode, string Description)> failedSegments = PnpUtilOutputAnalyzer.ExtractFailedSegments(result.Output);
                string detail = failedSegments.Count > 0
                    ? "\n失败子命令：\n" + string.Join("\n", failedSegments.Select(f => $"  [exit {f.ExitCode}] {f.Description}"))
                    : $"\n{result.Output}";
                AddLog($"❌ {actionName}失败（退出码 {result.ExitCode}）：{detail}");
                StatusText = $"{actionName}失败（退出码 {result.ExitCode}），明细见日志";
                _logger.Warn($"驱动{actionName}失败：退出码 {result.ExitCode}，失败段 {failedSegments.Count} 个");
            }
        }
        finally
        {
            IsOperating = false;
        }
    }
}
