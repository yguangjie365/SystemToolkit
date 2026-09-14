using CommunityToolkit.Mvvm.Input;
using SystemToolkit.Core.Logging;
using SystemToolkit.Core.Software.Services;
using SystemToolkit.Core.Software.Models;

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

        // winget 有进程级互斥锁：写操作进行中禁止并发刷新。
        // 🟠 A-🟠-4（v11~v14 后续批次）：原判据是布尔 `IsOperating`——check-then-act，
        // 与真正的闸门 `_wingetGate` **不同步**：若上一次写操作刚 ExitOperation 释放闸门、
        // 而另一次写操作尚未 Acquire，本检查会漏过 ⇒ 刷新与写操作并发调用 winget
        //（winget 有**进程级互斥锁**，并发调用互相报错 = 正是 _wingetGate 存在的理由）。
        // 改为直接尝试占用同一把闸门：谁持闸谁独占，判据与保护对象是同一个东西。
        // 注：`WaitAsync(0)` 与下面的 `Release()` 严格成对（try 内所有 return 都经 finally）。
        if (!await _wingetGate.WaitAsync(0)) // 非阻塞重载（本仓有 sync-over-async 源码守卫）
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
            _wingetGate.Release(); // 🟠 A-🟠-4：与进入时的 `Wait(0)` 严格成对
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

    /// <summary>
    /// V11-A2：单条写操作「进闸」兜底。获取闸门（<see cref="AcquireOperationAsync"/>）本身抛 / 取消时，
    /// 命令体没有顶层 catch，异常会被 AsyncRelayCommand 吞掉 —— 用户零反馈，且已置位的
    /// <c>IsOperating</c>（忙态 → 所有命令 CanOperate 恒假）与已占用的闸门**永久不释放**。
    /// <para>
    /// 异常计数与 <c>RunPackageOperationAsync</c> 内层**刻意不重复**：后者 catch 后正常返回，
    /// 故凡是进到这里的异常，其命令必未产生安装历史记录，此处 ±1 恒配对。
    /// </para>
    /// </summary>
    private void ExitOperationOnFailure(string action, Exception ex)
    {
        _logger.Time("AppManagerOperation").Complete(
            LogResult.Failed, LogLevel.Error, $"获取 winget 操作闸门失败（{action}）", ex);
        try
        {
            // 用 ExitOperation 而非裸 Release：同步复位 IsOperating（否则忙态同样永久卡死）
            ExitOperation();
        }
        catch (Exception releaseEx)
        {
            // 兜底路径的兜底：绝不从 catch 再抛（会绕过全局熔断直冲 Dispatcher）
            _logger.Warn($"释放 winget 操作闸门时再次异常（{action}）：{releaseEx.Message}");
        }

        AddLog($"{action}未启动：winget 操作通道异常（{ex.Message}）");
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
            _logger.Time("InstallPackage").Complete(LogResult.Rejected, LogLevel.Info, "安装未启动：目标为空或行忙");
            return;
        }

        try
        {
            await AcquireOperationAsync();
        }
        catch (Exception ex)
        {
            // V11-A2：闸门未获取（或获取中途取消）→ 就地兜底，不留半开状态
            ExitOperationOnFailure("安装", ex);
            return;
        }

        await RunPackageOperationAsync(pkg, InstallAction.Install, "InstallPackage", ct => _winget.InstallAsync(pkg.Id, pkg.Model.Source, ct), pkg.MarkInstalled);
    }

    [RelayCommand(CanExecute = nameof(CanOperate))]
    private async Task UpgradeAsync(WingetPackageVm? pkg)
    {
        if (pkg is null || pkg.IsBusy)
        {
            AddLog("升级未启动：目标为空或该行正忙，请稍后重试。");
            _logger.Time("UpgradePackage").Complete(LogResult.Rejected, LogLevel.Info, "升级未启动：目标为空或行忙");
            return;
        }

        try
        {
            await AcquireOperationAsync();
        }
        catch (Exception ex)
        {
            // V11-A2：闸门未获取（或获取中途取消）→ 就地兜底，不留半开状态
            ExitOperationOnFailure("升级", ex);
            return;
        }

        await RunPackageOperationAsync(pkg, InstallAction.Upgrade, "UpgradePackage", ct => _winget.UpgradeAsync(pkg.Id, pkg.Model.Source, ct), pkg.MarkInstalled);
    }

    [RelayCommand(CanExecute = nameof(CanOperate))]
    private async Task UninstallAsync(WingetPackageVm? pkg)
    {
        // 破坏性操作三处提前 return 全部留痕（旧工程教训）
        if (pkg is null || pkg.IsBusy)
        {
            AddLog("卸载未启动：目标为空或该行正忙，请稍后重试。");
            _logger.Time("UninstallPackage").Complete(LogResult.Rejected, LogLevel.Info, "卸载未启动：目标为空或行忙");
            return;
        }

        if (ConfirmRequest?.Invoke("卸载确认",
                $"确定要卸载以下软件吗？\n{pkg.Name}（{pkg.Id}）\n\n此操作将移除该软件，请谨慎操作。") != true)
        {
            AddLog("已取消卸载：" + pkg.Name);
            _logger.Time("UninstallPackage").Complete(LogResult.Cancelled, LogLevel.Info, $"已取消卸载：{pkg.Name}");
            return;
        }

        try
        {
            await AcquireOperationAsync();
        }
        catch (Exception ex)
        {
            // V11-A2：闸门未获取（或获取中途取消）→ 就地兜底，不留半开状态
            ExitOperationOnFailure("卸载", ex);
            return;
        }

        await RunPackageOperationAsync(pkg, InstallAction.Uninstall, "UninstallPackage", ct => _winget.UninstallAsync(pkg.Id, pkg.Model.Source, ct), pkg.MarkNotInstalled);
    }

    /// <summary>单包操作公共编排：执行→结果留痕→局部状态更新（避免全量刷新导致状态集体闪变）。</summary>
    private async Task RunPackageOperationAsync(WingetPackageVm pkg, InstallAction kind, string actionKey,
        Func<CancellationToken, Task<WingetRunResult>> run, Action markLocal)
    {
        // 文案由枚举派生（唯一来源）：避免"安装/升级/卸载"在调用点与日志里各写一遍
        string action = InstallHistoryLabels.ActionText(kind);
        // LOG-2：安装/升级/卸载是破坏性+长耗时操作，Action/Result/Duration 一条落齐
        LogTiming timing = _logger.Time(actionKey);
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
            RecordInstall(
                kind,
                result.Success ? InstallOutcome.Success : InstallOutcome.Failed,
                pkg,
                exitCode: result.ExitCode,
                detail: result.Success ? "" : WingetExitHint(result.ExitCode, pkg.Model.IsMsStore));
            timing.Complete(
                result.Success ? LogResult.Success : LogResult.Failed,
                result.Success ? LogLevel.Info : LogLevel.Warn,
                $"{action} {(result.Success ? "完成" : $"失败（退出码 {result.ExitCode}）")}：{pkg.Name}（{pkg.Id}）");
        }
        catch (OperationCanceledException)
        {
            AddLog($"{action}已取消：" + pkg.Name);
            RecordInstall(kind, InstallOutcome.Cancelled, pkg);
            timing.Complete(LogResult.Cancelled, LogLevel.Info, $"{action}已取消：{pkg.Name}");
        }
        catch (Exception ex)
        {
            timing.Complete(LogResult.Failed, LogLevel.Error, $"{action}异常：{pkg.Name}（{pkg.Id}）", ex);
            AddLog($"{action}异常：{pkg.Name}（{ex.Message}）");
            RecordInstall(kind, InstallOutcome.Failed, pkg, detail: ex.Message);
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
