using CommunityToolkit.Mvvm.Input;
using SystemToolkit.Core.Software.Services;

namespace SystemToolkit.Modules.AppManager;

public partial class AppManagerViewModel
{
    // ==================================================================
    // 环境档案（MVP：按分类聚合；自定义档案编辑随后续迭代）
    // ==================================================================
    private void RebuildArchives()
    {
        Archives.Clear();
        var all = StorePackages.Concat(ThirdPartyPackages).ToList();
        Archives.Add(new EnvironmentArchiveVm("全部软件", all.Count, all));

        foreach (IGrouping<string, WingetPackageVm> group in all.Where(p => !string.IsNullOrWhiteSpace(p.Category))
                     .GroupBy(p => p.Category!)
                     .OrderBy(g => g.Key, StringComparer.Ordinal))
        {
            Archives.Add(new EnvironmentArchiveVm(group.Key, group.Count(), group.ToList()));
        }
    }

    /// <summary>恢复环境 = 批量安装该档案下所有未安装项（逐项执行，复用写闸门）。</summary>
    [RelayCommand(CanExecute = nameof(CanOperate))]
    private async Task RestoreEnvironmentAsync(EnvironmentArchiveVm? archive)
    {
        if (archive is null)
        {
            return;
        }

        var targets = archive.Packages
            .Where(p => p.IsQueryable && p.State is WingetPackageState.NotInstalled or WingetPackageState.Unknown)
            .ToList();
        if (targets.Count == 0)
        {
            AddLog($"环境「{archive.Name}」无需恢复：清单内软件均已安装（或状态未知，请先检测状态）。");
            return;
        }

        if (ConfirmRequest?.Invoke("恢复环境确认",
                $"环境「{archive.Name}」共有 {archive.Count} 个软件，其中 {targets.Count} 个未安装。\n" +
                "将逐项自动安装（失败项标注原因）。确定继续吗？") != true)
        {
            AddLog($"已取消恢复环境：{archive.Name}");
            return;
        }

        await AcquireOperationAsync();

        AddLog($"开始恢复环境「{archive.Name}」（待安装 {targets.Count} 项）…");
        int ok = 0, fail = 0;
        try
        {
            foreach (WingetPackageVm pkg in targets)
            {
                pkg.IsBusy = true;
                try
                {
                    WingetRunResult result = await _winget.InstallAsync(pkg.Id, pkg.Model.Source);
                    if (result.Success)
                    {
                        ok++;
                        pkg.MarkInstalled();
                        AddLog($"  ✅ {pkg.Name}");
                    }
                    else
                    {
                        fail++;
                        AddLog($"  ❌ {pkg.Name}（退出码 {result.ExitCode}）{WingetExitHint(result.ExitCode, pkg.Model.IsMsStore)}");
                    }
                }
                catch (Exception ex)
                {
                    fail++;
                    AddLog($"  ❌ {pkg.Name}（{ex.Message}）");
                }
                finally
                {
                    pkg.IsBusy = false;
                }
            }

            AddLog(fail == 0
                ? $"✅ 环境「{archive.Name}」恢复完成：成功 {ok}/{targets.Count}。"
                : $"⚠ 环境「{archive.Name}」恢复结束：成功 {ok}、失败 {fail}。");
        }
        finally
        {
            ExitOperation();
        }
    }

    [RelayCommand]
    private void NewArchive()
    {
        AddLog("自定义环境档案编辑将在后续迭代提供（当前按软件分类自动生成档案）。");
        System.Windows.MessageBox.Show(
            "当前版本的环境档案按软件分类自动生成（见卡片列表）。\n\n自定义档案（自由勾选软件组合）将在后续迭代提供。",
            "新建环境档案",
            System.Windows.MessageBoxButton.OK,
            System.Windows.MessageBoxImage.Information);
    }

    // ==================================================================
    // winget 退出码语义翻译（复用旧工程映射表）
    // ==================================================================
    private static string WingetExitHint(int exitCode, bool isMsStore = false)
    {
        string hint = exitCode switch
        {
            -2147012865 => "——网络连接失败（0x80072EFF 无法连接服务器），请检查网络、代理或防火墙后重试",
            -2147012866 => "——与服务器的连接被中止（0x80072EFE），请检查网络、代理或防火墙后重试",
            -2147012867 => "——连接服务器超时（0x80072EFD），请检查网络后重试",
            -2147012889 => "——域名解析失败（0x80072EE7），请检查 DNS 或网络后重试",
            _ => "",
        };

        if (isMsStore)
        {
            hint += (hint.Length > 0 ? "；" : "——")
                 + "此包来自 Microsoft Store，还需确认 Microsoft Store 能正常打开"
                 + "（winget 需代表当前用户向商店获取，商店不可达时版本号会显示 Unknown）";
        }

        return hint;
    }

    partial void OnSearchQueryChanged(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            SearchResults.Clear();
            IsSearchPopupOpen = false;
        }
    }
}
