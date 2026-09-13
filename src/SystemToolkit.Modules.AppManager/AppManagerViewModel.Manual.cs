using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using CommunityToolkit.Mvvm.Input;
using SystemToolkit.Core.Logging;
using SystemToolkit.Core.Software.Models;
using SystemToolkit.Core.Software.Services;

namespace SystemToolkit.Modules.AppManager;

public partial class AppManagerViewModel
{
    // ==================================================================
    // 批量操作（M-UI-2 落地 2026-09-05：浮底条入口；具体 winget 流水线逻辑
    // 与 BatchInstallAsync 复用，留 V0.x 后续补——UI 与逻辑分离）
    //
    // 🔴 2026-09-08（第三方审查 S-7）：功能未实现期间按钮必须**禁用**，
    // 不能让人点了只有一行「待办 N 项」日志——那是"按钮能点但不响应"的体验事故。
    // 实现完成后把 CanBatchOperate 换成真实条件即可。
    // ==================================================================

    /// <summary>
    /// 批量升级/卸载尚未实现，故恒为 false（按钮禁用）。
    /// 2026-09-09：两份审查结论冲突——9-8 审查 S-7 要求"未实现必须禁用（不做能点不响应的按钮）"，
    /// 9-9 审查 02-1 指出"恒禁用时用户分不清是未实现还是坏了"。
    /// 取两者交集：**保持禁用 + XAML 侧 ToolTip 明示"功能开发中"**（原因可见，又不制造假可点）。
    /// 实现 winget 串行流水线后，把此处换成真实条件并移除 ToolTip 即可。
    /// </summary>
    private bool CanBatchOperate => false;

    [RelayCommand(CanExecute = nameof(CanBatchOperate))]
    private void BatchUpgradeSelected()
    {
        int count = SelectedCount;
        if (count == 0)
        {
            AddLog("批量升级未启动：未勾选任何软件。");
            return;
        }

        AddLog($"批量升级：待办 {count} 项（具体 winget 串行流水线留 V0.x 后续补）");
        // TODO：复用 BatchInstallAsync 的 AcquireOperationAsync + winget 串行闸模式遍历已选项
    }

    [RelayCommand(CanExecute = nameof(CanBatchOperate))]
    private void BatchUninstallSelected()
    {
        int count = SelectedCount;
        if (count == 0)
        {
            AddLog("批量卸载未启动：未勾选任何软件。");
            return;
        }

        AddLog($"批量卸载：待办 {count} 项（具体 winget 串行流水线留 V0.x 后续补）");
    }

    [RelayCommand]
    private void ClearBatchSelection()
    {
        foreach (WingetPackageVm vm in StorePackages.Concat(ThirdPartyPackages).Where(p => p.IsSelected))
        {
            vm.IsSelected = false;
        }
        AddLog("已清空全部勾选。");
    }

    // ==================================================================
    // 清单编辑（用户 2026-09-04：第三方列表编辑功能必须有）
    // ==================================================================
    [RelayCommand]
    private void AddManualSoftware()
    {
        SoftwareEditResult? result = SoftwareEditRequest?.Invoke(null);
        if (result is null || result.Deleted || result.Item is not ManualSoftware sw)
        {
            return;
        }

        ManualSoftwares.Add(sw);
        PersistAll();
        AddLog($"已添加手动软件：{sw.Name}");
    }

    [RelayCommand]
    private void EditSoftware(WingetPackageVm? vm)
    {
        if (vm is null)
        {
            return;
        }

        SoftwareEditResult? result = SoftwareEditRequest?.Invoke(vm.Model);
        if (result is null)
        {
            return;
        }

        ObservableCollection<WingetPackageVm> list = vm.Model.IsMsStore ? StorePackages : ThirdPartyPackages;
        if (result.Deleted)
        {
            list.Remove(vm);
            AddLog("已删除软件：" + vm.Name);
            RecountSelection(); // 审查 O1：被删行的勾选态不会自己触发 PropertyChanged
        }
        else if (result.Item is WingetPackage package)
        {
            int index = list.IndexOf(vm);
            if (index >= 0)
            {
                // 审查 2026-09-04（P1-2）：替换行 VM 必须重挂勾选计数订阅，否则编辑后该行勾选不计数
                var replacement = new WingetPackageVm(package);
                HookSelectionCounter(replacement);
                list[index] = replacement;
                RecountSelection(); // 审查 O1：旧行若已勾选，替换后必须回算
            }

            AddLog("已更新软件：" + package.Name);
        }

        PersistAll();
        RebuildArchives();
        _ = RefreshStatesSafeAsync(); // v5 B2（🟡-2）：fire-and-forget 必须带兜底
    }

    [RelayCommand]
    private void EditManualSoftware(ManualSoftware? sw)
    {
        if (sw is null)
        {
            return;
        }

        SoftwareEditResult? result = SoftwareEditRequest?.Invoke(sw);
        if (result is null)
        {
            return;
        }

        if (result.Deleted)
        {
            ManualSoftwares.Remove(sw);
            AddLog("已删除手动软件：" + sw.Name);
        }
        else if (result.Item is ManualSoftware updated)
        {
            int index = ManualSoftwares.IndexOf(sw);
            if (index >= 0)
            {
                ManualSoftwares[index] = updated;
            }

            AddLog("已更新手动软件：" + updated.Name);
        }

        PersistAll();
    }

    [RelayCommand]
    private void DeleteManualSoftware(ManualSoftware? sw)
    {
        if (sw is null)
        {
            return;
        }

        if (ConfirmRequest?.Invoke("删除软件",
                $"确定要删除「{sw.Name}」吗？\n\n仅从软件清单移除，不会影响已安装的软件。") != true)
        {
            return;
        }

        if (ManualSoftwares.Remove(sw))
        {
            PersistAll();
            AddLog("已删除手动软件：" + sw.Name);
        }
    }

    // ==================================================================
    // 批量安装（2026-09-04 新增：底栏）
    // ==================================================================
    [RelayCommand(CanExecute = nameof(CanOperate))]
    private async Task BatchInstallAsync()
    {
        LogTiming timing = _logger.Time("BatchInstall");
        var targets = StorePackages.Concat(ThirdPartyPackages)
            .Where(p => p.IsSelected && p.IsQueryable)
            .ToList();
        if (targets.Count == 0)
        {
            AddLog("批量安装未启动：未勾选任何可安装的软件。");
            timing.Complete(LogResult.Rejected, LogLevel.Info, "批量安装未启动：无勾选目标");
            return;
        }

        string names = string.Join("\n", targets.Select(p => $"  {p.Name}（{p.Id}）"));
        if (ConfirmRequest?.Invoke("批量安装确认",
                $"将按顺序安装以下 {targets.Count} 个软件：\n{names}\n\n逐项执行、可随时取消（关闭窗口即停），失败项会标注原因。确定继续吗？") != true)
        {
            AddLog("已取消批量安装。");
            timing.Complete(LogResult.Cancelled, LogLevel.Info, "批量安装：二次确认取消");
            return;
        }

        await AcquireOperationAsync();

        // 审查 2026-09-04（P2）：批量安装接入取消令牌——承诺的"关闭窗口即停"由此兑现
        using CancellationTokenSource batchCts = new();
        _batchCts = batchCts;

        AddLog($"开始批量安装（共 {targets.Count} 项）…");
        int ok = 0, fail = 0;
        bool wasCancelled = false;
        try
        {
            foreach (WingetPackageVm pkg in targets)
            {
                pkg.IsBusy = true;
                try
                {
                    AddLog($"[{ok + fail + 1}/{targets.Count}] 安装：{pkg.Name}（{pkg.Id}）");
                    WingetRunResult result = await _winget.InstallAsync(pkg.Id, pkg.Model.Source, batchCts.Token);
                    if (result.Success)
                    {
                        ok++;
                        pkg.MarkInstalled();
                        AddLog($"  ✅ 完成：{pkg.Name}");
                    }
                    else
                    {
                        fail++;
                        AddLog($"  ❌ 失败：{pkg.Name}（退出码 {result.ExitCode}）{WingetExitHint(result.ExitCode, pkg.Model.IsMsStore)}");
                    }
                }
                catch (OperationCanceledException) when (batchCts.IsCancellationRequested)
                {
                    AddLog($"  ⏹ 已取消：{pkg.Name}（剩余项不再执行）");
                    wasCancelled = true;
                    break;
                }
                catch (Exception ex)
                {
                    fail++;
                    AddLog($"  ❌ 异常：{pkg.Name}（{ex.Message}）");
                }
                finally
                {
                    pkg.IsBusy = false;
                    pkg.IsSelected = false;
                }
            }

            AddLog(fail == 0
                ? $"✅ 批量安装完成：成功 {ok}/{targets.Count}。"
                : $"⚠ 批量安装结束：成功 {ok}、失败 {fail}（明细见上方日志）。");
            timing.Complete(
                wasCancelled ? LogResult.Cancelled : fail == 0 ? LogResult.Success : LogResult.Failed,
                wasCancelled || fail > 0 ? LogLevel.Warn : LogLevel.Info,
                $"批量安装结束：成功 {ok}、失败 {fail}、共 {targets.Count} 项{(wasCancelled ? "（已取消中止）" : "")}");
            PersistAll();
            RecountSelection(); // 审查 O2：正常完成重算仍为 0；取消路径如实反映残留勾选
        }
        finally
        {
            _batchCts = null;
            ExitOperation();
        }
    }

    /// <summary>取消进行中的批量安装（窗口关闭时由 View 调用；审查 2026-09-04 P2：兑现"关闭窗口即停"）。</summary>
    public void CancelBatchInstall() => _batchCts?.Cancel();

    // ==================================================================
    // 手动软件（第三方）：下载 / 打开安装包
    // ==================================================================
    [RelayCommand]
    private void Download(ManualSoftware? sw)
    {
        if (sw is null || string.IsNullOrWhiteSpace(sw.DownloadUrl))
        {
            return;
        }

        // ShellExecute 语义下非 http(s) 字符串会按 PATH/协议解析为任意程序——
        // 下载链接可经导入清单外部投递，必须白名单 scheme（审查 M8）
        if (!Uri.TryCreate(sw.DownloadUrl, UriKind.Absolute, out Uri? uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            AddLog($"已拦截非 http(s) 下载链接：{sw.Name}（{sw.DownloadUrl}）");
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo(sw.DownloadUrl) { UseShellExecute = true });
            AddLog("已打开下载链接：" + sw.Name);
        }
        catch (Exception ex)
        {
            AddLog("打开下载链接失败：" + ex.Message);
        }
    }

    [RelayCommand]
    private void OpenInstaller(ManualSoftware? sw)
    {
        if (sw is null || string.IsNullOrWhiteSpace(sw.LocalInstallerPath))
        {
            return;
        }

        try
        {
            string fullPath = Path.GetFullPath(sw.LocalInstallerPath);
            if (File.Exists(fullPath) || Directory.Exists(fullPath))
            {
                Process.Start(new ProcessStartInfo { FileName = fullPath, UseShellExecute = true });
                AddLog("已启动/打开：" + fullPath);
            }
            else
            {
                AddLog("本地安装包不存在：" + fullPath);
            }
        }
        catch (Exception ex)
        {
            AddLog("打开安装包失败：" + ex.Message);
        }
    }

    // ==================================================================
    // 导入 / 导出
    // 落地计划 B4-④（2026-09-13）：清单格式**直接切换**为 winget 官方格式（schema 2.0），
    // 旧自研 EnvCatalog 格式降级为**只读导入**（已裁定：不长期养两套解析判据）。
    // ==================================================================

    /// <summary>
    /// 导出当前 winget 清单为 **winget 官方格式**（可与 <c>winget import</c> 互通）。
    /// </summary>
    /// <remarks>
    /// 🔴 该格式**只有包 Id**：名称/分类/图标/描述、以及手工软件与驱动工具清单均无对应字段 →
    /// 导出后必须**如实说明范围**，不能让用户以为这是全量备份（本批的行为红线）。
    /// </remarks>
    [RelayCommand]
    private void ExportList()
    {
        string? exportPath = PickSavePath?.Invoke();
        if (string.IsNullOrEmpty(exportPath))
        {
            return;
        }

        try
        {
            int count = _env.ExportWingetManifest(exportPath);
            AddLog($"已导出清单（winget 官方格式，{count} 个包）：{exportPath}");
            AddLog("范围说明：该格式只含包 Id（不含名称/分类/图标），手工软件与驱动工具清单不在其中；"
                + "该文件可直接交由 winget import 使用。");
        }
        catch (Exception ex)
        {
            _logger.Error("导出软件清单失败：" + exportPath, ex);
            AddLog("导出失败：" + ex.Message);
        }
    }

    /// <summary>
    /// 把**本机已安装**的软件导出为 winget 官方清单（走 <c>winget export</c> 自己导，
    /// 故源地址等字段是本机真实值）。
    /// </summary>
    /// <remarks>
    /// 🔴 未在界面挂入口（页头工具条属页骨架，改动影响面大）——Core 能力与测试已就位，
    /// 入口随 B4 剩余 UI 一并落。此处先留实现，避免"有命令无按钮"的假可点。
    /// </remarks>
    [RelayCommand(CanExecute = nameof(CanOperate))]
    private async Task ExportInstalledAsync()
    {
        string? exportPath = PickSavePath?.Invoke();
        if (string.IsNullOrEmpty(exportPath))
        {
            return;
        }

        await AcquireOperationAsync();
        try
        {
            AddLog("正在导出本机已安装软件（winget export）…");
            WingetRunResult result = await _winget.ExportAsync(exportPath);
            if (!result.Success)
            {
                AddLog($"导出失败（退出码 {result.ExitCode}）{WingetExitHint(result.ExitCode, isMsStore: false)}");
                return;
            }

            AddLog($"已导出本机已安装软件：{exportPath}");
            AddLog("范围说明：只包含 winget 源可识别的软件；不在任何源中的已安装软件不会出现（需手动记录）。");
        }
        catch (Exception ex)
        {
            _logger.Error("导出本机已安装软件失败：" + exportPath, ex);
            AddLog("导出失败：" + ex.Message);
        }
        finally
        {
            ExitOperation();
        }
    }

    /// <summary>
    /// 导入清单：**自动判别格式**。winget 官方格式 → 只替换商店/第三方应用清单
    /// （该格式没有手工与驱动条目，清空它们等于用文件没说的东西删数据）；旧格式 → 替换全部三类。
    /// </summary>
    [RelayCommand]
    private void ImportList()
    {
        string? importPath = PickOpenPath?.Invoke();
        if (string.IsNullOrEmpty(importPath))
        {
            return;
        }

        int current = StorePackages.Count + ThirdPartyPackages.Count + ManualSoftwares.Count;
        if (ConfirmRequest?.Invoke("导入清单",
                $"导入将用所选文件替换当前清单（当前共 {current} 条）。\n" +
                "· winget 官方格式：只替换商店/第三方应用清单，手工与驱动清单**保留**\n" +
                "· 本工具旧格式：替换全部三类清单\n" +
                "覆盖前会自动备份当前清单。\n\n确定继续吗？") != true)
        {
            return;
        }

        try
        {
            // 备份在 try 内（审查 M5）：备份 IO 异常必须走导入失败路径留痕，
            // 而不是冒泡到全局兜底被吞、导入静默中止
            string? backupPath = _env.BackupCatalog();
            EnvManifestImport import = _env.ImportManifest(importPath);
            if (!import.Success)
            {
                AddLog("导入失败：文件格式不正确或内容未通过校验（当前清单未改动）");
                return;
            }

            EnvCatalog catalog = import.Catalog;
            StorePackages.Clear();
            ThirdPartyPackages.Clear();
            foreach (WingetPackage item in catalog.Winget)
            {
                var vm = new WingetPackageVm(item);
                HookSelectionCounter(vm); // 审查 M3：ImportList 此前漏挂，导入后勾选计数永久为 0
                if (item.IsMsStore)
                {
                    StorePackages.Add(vm);
                }
                else
                {
                    ThirdPartyPackages.Add(vm);
                }
            }

            // 审查 2026-09-04（P2）：导入重灌集合后残留的勾选计数必须清零
            RecountSelection(); // 审查 O2：统一走重算（导入后全部未勾选，结果同为 0）

            // winget 官方格式**没有**手工/驱动清单字段 → 只有旧格式才替换这两类
            if (import.Format == EnvManifestFormat.LegacyCatalog)
            {
                ManualSoftwares.Clear();
                foreach (ManualSoftware item in catalog.Manual)
                {
                    ManualSoftwares.Add(item);
                }

                _driverSoftwares.Clear();
                _driverSoftwares.AddRange(catalog.Driver);
            }

            PersistAll();
            RebuildViews();
            RebuildArchives();
            AddLog($"已导入清单（{(import.Format == EnvManifestFormat.WingetExport ? "winget 官方格式" : "本工具旧格式")}）："
                + $"{catalog.Winget.Count} 个商店/第三方应用"
                + (import.Format == EnvManifestFormat.LegacyCatalog
                    ? $"，{catalog.Manual.Count} 个手动条目，{catalog.Driver.Count} 个驱动条目"
                    : "（手工/驱动清单未改动）")
                + (backupPath is not null ? $"（原清单已备份：{backupPath}）" : "（备份失败，原清单可能无法恢复）"));

            if (import.Format == EnvManifestFormat.WingetExport)
            {
                AddLog("范围说明：winget 格式只含包 Id，导入后的名称为 Id 原文，分类为默认值。");
            }

            _ = RefreshStatesSafeAsync(); // v5 B2（🟡-2）：fire-and-forget 必须带兜底
        }
        catch (Exception ex)
        {
            _logger.Error("导入软件清单失败：" + importPath, ex);
            AddLog("导入失败：" + ex.Message);
        }
    }

}
