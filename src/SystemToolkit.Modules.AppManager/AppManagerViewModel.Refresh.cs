using CommunityToolkit.Mvvm.Input;
using SystemToolkit.Core.Software.Services;

namespace SystemToolkit.Modules.AppManager;

public partial class AppManagerViewModel
{
    // ==================================================================
    // 安装状态检测（异步，不阻塞 UI；设计文档 §3 建议方案）
    // ==================================================================
    [RelayCommand]
    public async Task RefreshStatesAsync()
    {
        if (IsRefreshing)
        {
            RefreshStatus = "正在刷新中…";
            return;
        }

        int total = StorePackages.Count + ThirdPartyPackages.Count;
        if (total == 0)
        {
            RefreshStatus = "暂无软件";
            return;
        }

        // winget 有进程级互斥锁：写操作进行中禁止并发刷新
        if (IsOperating)
        {
            RefreshStatus = "winget 操作进行中，完成后请手动刷新状态";
            return;
        }

        IsRefreshing = true;
        RefreshStatus = "正在查询安装状态…";
        try
        {
            // 预检：winget 不可用时全部置 Unknown，避免满屏误判"未安装"
            if (!await _winget.IsAvailableAsync())
            {
                foreach (WingetPackageVm p in StorePackages.Concat(ThirdPartyPackages))
                {
                    p.State = WingetPackageState.Unknown;
                    p.VersionText = "";
                }

                RefreshStatus = "未检测到 winget，无法查询安装状态";
                AddLog("未检测到 winget：请确认已安装 WinGet（winget --version 可验证）");
                return;
            }

            string listText = await _winget.ListInstalledAsync();
            string upgradeText = "";
            try
            {
                upgradeText = await _winget.ListUpgradesAsync();
            }
            catch (Exception ex)
            {
                AddLog("查询可升级列表失败（不影响已安装判定）：" + ex.Message);
            }

            // 第一遍：全量文本匹配；msstore 短 Id 命不中进补查队列
            // （性能审查 P0-4：Split 一次复用，FindPackageRow 走数组重载）
            string[] listLines = listText.Split('\n');
            string[] upgradeLines = upgradeText.Split('\n');
            List<WingetPackageVm> msStorePending = new();
            int done = 0;
            foreach (WingetPackageVm vm in StorePackages.Concat(ThirdPartyPackages))
            {
                (bool found, string? version, string? _) = WingetService.FindPackageRow(listLines, vm.Id);
                if (found)
                {
                    (bool uFound, string? _, string? uAvailable) = WingetService.FindPackageRow(upgradeLines, vm.Id);
                    if (uFound)
                    {
                        vm.State = WingetPackageState.Updatable;
                        vm.VersionText = string.IsNullOrEmpty(uAvailable) ? version ?? "" : $"{version} → {uAvailable}";
                    }
                    else
                    {
                        vm.State = WingetPackageState.Installed;
                        vm.VersionText = version ?? "";
                    }
                }
                else if (vm.Model.IsMsStore)
                {
                    msStorePending.Add(vm);
                    vm.State = WingetPackageState.Unknown;
                    vm.VersionText = "";
                }
                else
                {
                    vm.VersionText = "";
                    vm.State = WingetPackageState.NotInstalled;
                }

                done++;
                if (done % 5 == 0 || done == total)
                {
                    RefreshStatus = $"正在查询 {done}/{total}…";
                }
            }

            // 第二遍：msstore 短 Id 包并发补查（并发度 4）
            if (msStorePending.Count > 0)
            {
                using SemaphoreSlim gate = new(4, 4);
                await Task.WhenAll(msStorePending.Select(async vm =>
                {
                    await gate.WaitAsync();
                    try
                    {
                        WingetQueryResult r = await _winget.QueryAsync(vm.Id, "msstore");
                        if (r.Unknown)
                        {
                            vm.State = WingetPackageState.Unknown;
                            return;
                        }

                        vm.VersionText = r.Version ?? "";
                        vm.State = !r.Installed
                            ? WingetPackageState.NotInstalled
                            : r.AvailableVersion is not null
                                ? WingetPackageState.Updatable
                                : WingetPackageState.Installed;
                    }
                    catch
                    {
                        vm.State = WingetPackageState.Unknown;
                    }
                    finally
                    {
                        gate.Release();
                    }
                }));
            }

            int installed = StorePackages.Concat(ThirdPartyPackages)
                .Count(p => p.State is WingetPackageState.Installed or WingetPackageState.Updatable);
            int updatable = StorePackages.Concat(ThirdPartyPackages).Count(p => p.State == WingetPackageState.Updatable);
            RefreshStatus = $"已安装 {installed}，可升级 {updatable}";
            AddLog($"状态刷新完成：已安装 {installed}，可升级 {updatable}");
        }
        catch (Exception ex)
        {
            _logger.Error("刷新安装状态失败", ex);
            AddLog("刷新安装状态失败：" + ex.Message);
            RefreshStatus = "刷新失败：" + ex.Message;
        }
        finally
        {
            IsRefreshing = false;
        }
    }

    // ==================================================================
    // winget 写操作闸门
    // ==================================================================
    /// <summary>
    /// 进入 winget 写操作：占用串行闸并置忙。
    /// 审查 2026-09-04（P2）：原 TryEnterOperationAsync 恒返回 true，9 处 if 判空是死分支——
    /// 改为无返回值，语义即"获取闸门"；并发防护由 CanOperate（UI 层）+ 闸门串行化（最终层）承担。
    /// </summary>
    /// <summary>当前单条安装/升级/卸载的取消令牌（CancelOperation 触发；操作结束置空）。</summary>
    private CancellationTokenSource? _opCts;

    private async Task AcquireOperationAsync()
    {
        await _wingetGate.WaitAsync();
        IsOperating = true;
    }

    private void ExitOperation()
    {
        IsOperating = false;
        _wingetGate.Release();
    }

    /// <summary>取消当前单条 winget 操作（命中 CancellationToken 抛 OperationCanceledException）。</summary>
    [RelayCommand]
    private void CancelOperation()
    {
        _opCts?.Cancel();          // 单条安装/升级/卸载
        _batchCts?.Cancel();      // 批量安装（批次二已有取消令牌）
    }

    [RelayCommand(CanExecute = nameof(CanOperate))]
    private async Task InstallAsync(WingetPackageVm? pkg)
    {
        if (pkg is null || pkg.IsBusy)
        {
            AddLog("安装未启动：目标为空或该行正忙，请稍后重试。");
            return;
        }

        await AcquireOperationAsync();

        await RunPackageOperationAsync(pkg, "安装", ct => _winget.InstallAsync(pkg.Id, pkg.Model.Source, ct), pkg.MarkInstalled);
    }

    [RelayCommand(CanExecute = nameof(CanOperate))]
    private async Task UpgradeAsync(WingetPackageVm? pkg)
    {
        if (pkg is null || pkg.IsBusy)
        {
            AddLog("升级未启动：目标为空或该行正忙，请稍后重试。");
            return;
        }

        await AcquireOperationAsync();

        await RunPackageOperationAsync(pkg, "升级", ct => _winget.UpgradeAsync(pkg.Id, pkg.Model.Source, ct), pkg.MarkInstalled);
    }

    [RelayCommand(CanExecute = nameof(CanOperate))]
    private async Task UninstallAsync(WingetPackageVm? pkg)
    {
        // 破坏性操作三处提前 return 全部留痕（旧工程教训）
        if (pkg is null || pkg.IsBusy)
        {
            AddLog("卸载未启动：目标为空或该行正忙，请稍后重试。");
            return;
        }

        if (ConfirmRequest?.Invoke("卸载确认",
                $"确定要卸载以下软件吗？\n{pkg.Name}（{pkg.Id}）\n\n此操作将移除该软件，请谨慎操作。") != true)
        {
            AddLog("已取消卸载：" + pkg.Name);
            return;
        }

        await AcquireOperationAsync();

        await RunPackageOperationAsync(pkg, "卸载", ct => _winget.UninstallAsync(pkg.Id, pkg.Model.Source, ct), pkg.MarkNotInstalled);
    }

    /// <summary>单包操作公共编排：执行→结果留痕→局部状态更新（避免全量刷新导致状态集体闪变）。</summary>
    private async Task RunPackageOperationAsync(WingetPackageVm pkg, string action,
        Func<CancellationToken, Task<WingetRunResult>> run, Action markLocal)
    {
        pkg.IsBusy = true;
        _opCts = new CancellationTokenSource(); // 审查：单条安装/升级/卸载可取消（CancelOperation 触发）
        bool stateChanged = false;
        try
        {
            AddLog($"开始{action}：{pkg.Name}（{pkg.Id}）");
            WingetRunResult result = await run(_opCts.Token);
            stateChanged = result.Success;
            AddLog(result.Success
                ? $"✅ {action}完成：" + pkg.Name
                : $"❌ {action}失败：{pkg.Name}（退出码 {result.ExitCode}）{WingetExitHint(result.ExitCode, pkg.Model.IsMsStore)}");
        }
        catch (OperationCanceledException)
        {
            AddLog($"{action}已取消：" + pkg.Name);
        }
        catch (Exception ex)
        {
            _logger.Error($"{action}异常：{pkg.Name}（{pkg.Id}）", ex);
            AddLog($"{action}异常：{pkg.Name}（{ex.Message}）");
        }
        finally
        {
            pkg.IsBusy = false;
            ExitOperation();
            _opCts?.Dispose();
            _opCts = null;
            if (stateChanged)
            {
                markLocal();
                PersistAll();
            }
            else
            {
                // 审查 🟡-4（2026-09-10）：fire-and-forget 也必须带兜底（AsyncRelayCommand/UTE 会吞）
                _ = RefreshStatesSafeAsync();
            }
        }
    }

    /// <summary>后台刷新状态（异常落日志与状态栏，不静默）。</summary>
    private async Task RefreshStatesSafeAsync()
    {
        try
        {
            await RefreshStatesAsync().ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            _logger.Error("后台刷新安装状态异常", ex);
            AddLog("后台刷新状态失败：" + ex.Message);
        }
    }

}
