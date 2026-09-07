using CommunityToolkit.Mvvm.Input;
using SystemToolkit.Core.Software.Models;
using SystemToolkit.Core.Software.Services;

namespace SystemToolkit.Modules.AppManager;

public partial class AppManagerViewModel
{
    // ==================================================================
    // 软件源管理（复用 WingetMirrors + winget source 命令）
    // ==================================================================
    [RelayCommand(CanExecute = nameof(CanOperate))]
    private async Task UpdateSourceAsync()
    {
        await AcquireOperationAsync();

        AddLog("正在更新软件源（winget source update）…");
        try
        {
            WingetRunResult result = await _winget.UpdateSourceAsync();
            AddLog(result.Success
                ? "✅ 软件源更新完成"
                : $"❌ 软件源更新失败（退出码 {result.ExitCode}）{WingetExitHint(result.ExitCode)}");
        }
        catch (Exception ex)
        {
            _logger.Error("更新软件源失败", ex);
            AddLog("更新软件源失败：" + ex.Message);
        }
        finally
        {
            ExitOperation();
        }
    }

    [RelayCommand(CanExecute = nameof(CanOperate))]
    private async Task SwitchToMirrorAsync()
    {
        // 换源改动系统级 winget 配置（需管理员），破坏性操作必须确认并留痕。
        string detail = string.Join("\n", WingetMirrors.Ustc.Select(m => $"  {m.Name,-12}→ {m.Url}"));
        if (ConfirmRequest?.Invoke("切换软件源",
                "将把以下 winget 源切换为中科大镜像：\n\n" + detail +
                "\n\n官方源在国内网络下可能连接超时，切换后搜索与安装通常快很多。\n" +
                "此操作需要管理员权限（winget 源为系统级配置）：若当前未提权会失败，\n" +
                "请以管理员身份运行后再试。随时可用「恢复官方源」还原，不影响已安装软件。") != true)
        {
            AddLog("已取消：切换到国内镜像源");
            return;
        }

        await AcquireOperationAsync();

        AddLog("正在切换软件源到中科大镜像…");
        try
        {
            int ok = 0, fail = 0;
            foreach (WingetMirrorSource mirror in WingetMirrors.Ustc)
            {
                AddLog($"  切换：{mirror.Name} → {mirror.Url}");
                WingetRunResult result = await _winget.SetSourceAsync(mirror.Name, mirror.Url);
                if (result.Success)
                {
                    ok++;
                    AddLog($"  ✅ {mirror.Name} 已切换");
                }
                else
                {
                    fail++;
                    AddLog($"  ❌ {mirror.Name} 切换失败（退出码 {result.ExitCode}）{WingetExitHint(result.ExitCode)}");
                }
            }

            if (fail == 0)
            {
                SourceLabel = "中科大";
                AddLog($"✅ 镜像源切换完成（{ok} 个源）。建议点「更新软件源」刷新一次索引。");
            }
            else
            {
                AddLog($"⚠ 镜像源切换结束：成功 {ok} 个、失败 {fail} 个。");
            }
        }
        catch (Exception ex)
        {
            _logger.Error("切换镜像源失败", ex);
            AddLog("切换镜像源失败：" + ex.Message);
        }
        finally
        {
            ExitOperation();
        }
    }

    [RelayCommand(CanExecute = nameof(CanOperate))]
    private async Task RestoreOfficialSourceAsync()
    {
        if (ConfirmRequest?.Invoke("恢复官方源",
                "将把 winget 与 winget-font 两个源恢复为微软官方默认地址\n" +
                "（winget source reset，等价于移除镜像源条目并还原默认）。\n\n" +
                "此操作需要管理员权限；已安装的软件不受影响。") != true)
        {
            AddLog("已取消：恢复官方源");
            return;
        }

        await AcquireOperationAsync();

        AddLog("正在恢复官方软件源…");
        try
        {
            int ok = 0, fail = 0;
            foreach (string name in WingetMirrors.DefaultSourceNames)
            {
                WingetRunResult result = await _winget.ResetSourceAsync(name);
                if (result.Success)
                {
                    ok++;
                    AddLog($"  ✅ {name} 已恢复为官方源");
                }
                else
                {
                    fail++;
                    AddLog($"  ❌ {name} 恢复失败（退出码 {result.ExitCode}）{WingetExitHint(result.ExitCode)}");
                }
            }

            if (fail == 0)
            {
                SourceLabel = "官方";
                AddLog($"✅ 已恢复官方源（{ok} 个源）。建议点「更新软件源」刷新一次索引。");
            }
            else
            {
                AddLog($"⚠ 恢复结束：成功 {ok} 个、失败 {fail} 个。");
            }
        }
        catch (Exception ex)
        {
            _logger.Error("恢复官方源失败", ex);
            AddLog("恢复官方源失败：" + ex.Message);
        }
        finally
        {
            ExitOperation();
        }
    }

}
