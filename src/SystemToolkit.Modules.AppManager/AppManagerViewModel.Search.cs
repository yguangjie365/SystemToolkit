using CommunityToolkit.Mvvm.Input;
using SystemToolkit.Core.Software.Models;
using SystemToolkit.Core.Software.Services;

namespace SystemToolkit.Modules.AppManager;

public partial class AppManagerViewModel
{
    // ==================================================================
    // winget 源内搜索（搜索框）→ 安装候选
    // ==================================================================
    [RelayCommand]
    private async Task RunSearchAsync()
    {
        if (string.IsNullOrWhiteSpace(SearchQuery) || IsSearching)
        {
            return;
        }

        IsSearching = true;
        IsSearchPopupOpen = true;
        SearchResults.Clear();
        AddLog("正在搜索 winget 源：" + SearchQuery);
        try
        {
            List<WingetSearchResult> results = WingetService.ParseSearchResults(await _winget.SearchAsync(SearchQuery));
            foreach (WingetSearchResult item in results)
            {
                SearchResults.Add(item);
            }

            AddLog($"搜索完成：找到 {results.Count} 个候选包");
        }
        catch (Exception ex)
        {
            AddLog("搜索失败：" + ex.Message);
        }
        finally
        {
            IsSearching = false;
        }
    }

    [RelayCommand]
    private void CloseSearch() => IsSearchPopupOpen = false;

    [RelayCommand(CanExecute = nameof(CanOperate))]
    private async Task InstallSearchResultAsync(WingetSearchResult? item)
    {
        if (item is null || IsOperating)
        {
            return;
        }

        await AcquireOperationAsync();

        // 🟠 V11-A3：IsSearchPopupOpen / _opCts 赋值原先裸露在 try 之外——任一步抛出都会绕过
        // finally，导致 winget 闸门与忙态（IsOperating）**永久不释放**（此后安装/升级/卸载恒被拒）。
        // 两句连同其后的执行段全部移入 try；Acquire 本身仍在 try 外（其失败由 V11-A2 的兜底负责）。
        try
        {
            IsSearchPopupOpen = false;
            // 🟠 审查 2026-09-10（🟠-2）：本入口此前既不建 _opCts 也不传 ct，导致
            // CancelOperation/关窗取消对「从搜索弹窗安装」这条路径完全失效（子 winget 进程收不到取消）。
            // 与 RunPackageOperationAsync 同构：建局部闸 → 传入 → finally 释放并 dispose。
            _opCts = new CancellationTokenSource();
            AddLog($"开始安装：{item.Name}（{item.Id}）");
            WingetRunResult result = await _winget.InstallAsync(item.Id, "winget", _opCts.Token);
            if (result.Success)
            {
                AddLog("✅ 安装完成：" + item.Name);
                // winget 包 Id 不区分大小写：不去重会出现两条相同 Id 条目
                if (ThirdPartyPackages.FirstOrDefault(p => string.Equals(p.Model.Id, item.Id, StringComparison.OrdinalIgnoreCase)) is WingetPackageVm existing)
                {
                    AddLog($"「{item.Name}」已在清单中，跳过重复添加");
                    existing.MarkInstalled(); // 审查 2026-09-04（P2）：行状态局部更新，避免停留在"未安装"
                    return;
                }

                // V11-A1：改走建行工厂 —— 此前此处手写了一段与 HookSelectionCounter 等价的订阅
                //（原 🟡-9 重复），且同样漏了忽略命令挂接与忽略态回写；工厂收口后重复代码一并消失。
                WingetPackageVm newVm = CreateRow(new WingetPackage
                {
                    Id = item.Id,
                    Name = item.Name,
                    Description = item.Version,
                    Category = "其他",
                    Source = "winget",
                });
                // 安装成功即为已安装：局部标记（仍在写闸门内，RefreshStates 的 IsOperating 守卫会拦截）
                newVm.MarkInstalled();
                ThirdPartyPackages.Add(newVm);
                PersistAll();
                RebuildArchives();
                SearchQuery = "";
            }
            else
            {
                AddLog($"❌ 安装失败：{item.Name}（退出码 {result.ExitCode}）{WingetExitHint(result.ExitCode)}");
            }
        }
        catch (OperationCanceledException)
        {
            AddLog("安装已取消：" + item.Name);
        }
        catch (Exception ex)
        {
            AddLog($"安装异常：{item.Name}（{ex.Message}）");
        }
        finally
        {
            ExitOperation();
            _opCts?.Dispose();
            _opCts = null;
        }
    }

}
