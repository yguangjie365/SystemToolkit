using System.Diagnostics;
using SystemToolkit.Core.Network.Services;
using SystemToolkit.Modules.NetManager;

namespace SystemToolkit.Tests;

/// <summary>
/// 「后台循环的卸载-恢复」回归锁（🟠 V16-1，2026-09-15）。
/// <para>
/// <b>缺陷类</b>：批② 把 9 个模块 View 从 DI 单例改为 <c>AddTransient</c> 后，**主题切换会重建视图**
/// ⇒ 旧视图 <c>Unloaded</c> + 新视图 <c>Loaded</c>。单例时期 <c>Content = 同一对象</c> 是 WPF 空操作、
/// 从不真正 Unload，所以循环得以保留；现在 <c>Unloaded</c>（O7 防泄漏清理）被**意外**当成了
/// 「用户离开页面」⇒ 用户主动启动的持续 ping / 自动监控被永久停掉，而 <c>LoadAsync</c> 只重读数据、
/// 不重启"用户主动启动"的循环。
/// </para>
/// <para>
/// <b>判据（本锁钉的不变量）</b>：<c>Cancel*()</c>（卸载收口）**不得改写用户意图**；
/// 恢复与否只由「用户意图位 × 当前是否在跑」两条判据决定。
/// 特别是「用户主动停止后切主题」**不得**把循环又拉起来（反模式 ㊷ 双重翻转）。
/// </para>
/// <para>
/// ✅ <b>无需真机</b>：直接构造 Tab VM 驱动状态机。ping 用回环地址（必回包、无权限要求）；
/// 自动监控的首轮是 <c>Task.Delay(5min)</c>，用例在数秒内结束 ⇒ 不会触发真实网段扫描。
/// </para>
/// </summary>
public class NetManagerBackgroundLoopResumeTests
{
    private static readonly Action<string> NoLog = _ => { };

    /// <summary>诊断 Tab：本类只驱动持续 ping ⇒ 诊断服务 / DNS 探测传 null（不触其路径；
    /// 构造函数不做 null 校验，与 <c>ViewLoadSmokeGuardTests</c> 的 <c>null!</c> 冒烟同口径）。</summary>
    private static NetDiagnosticsTabViewModel NewDiag()
        => new(null!, null!, new ContinuousPingService(), NoLog);

    /// <summary>局域网 Tab：本类只翻监控开关 ⇒ 适配器服务 / 扫描服务传 null（理由同上）。</summary>
    private static LanScanTabViewModel NewLan() => new(null!, null!, NoLog);

    private static async Task WaitAsync(Func<bool> condition, int timeoutMs = 5000)
    {
        var sw = Stopwatch.StartNew();
        while (!condition() && sw.ElapsedMilliseconds < timeoutMs)
        {
            await Task.Delay(25);
        }
    }

    // ═══════════════════════════ 持续 ping（诊断 Tab） ═══════════════════════════

    [Fact]
    public void Ping_WithoutUserIntent_ResumeMustNotStart()
    {
        NetDiagnosticsTabViewModel vm = NewDiag();

        vm.ResumePingIfIntended();

        Assert.False(vm.IsPinging);
        // StartPing 会写入该文案 ⇒ 保持空串即证明「未进入启动路径」
        Assert.Equal(string.Empty, vm.PingStatsText);
    }

    /// <summary>🔴 反模式 ㊷（双重翻转）的回归锁：用户主动停止 = 意图作废，切主题不得复活它。</summary>
    [Fact]
    public async Task Ping_AfterUserStop_ResumeMustNotRestart()
    {
        NetDiagnosticsTabViewModel vm = NewDiag();
        vm.PingTarget = "127.0.0.1";

        vm.StartPingCommand.Execute(null);
        Assert.True(vm.IsPinging, "前置条件：用户已启动持续 ping");

        vm.StopPingCommand.Execute(null);       // 用户主动停止
        await WaitAsync(() => !vm.IsPinging);   // 等循环 finally 收口

        vm.CancelPing();                        // 卸载取消：**不得**把「用户已停」改写成「可恢复」
        vm.ResumePingIfIntended();

        Assert.False(vm.IsPinging);
    }

    [Fact]
    public async Task Ping_AfterUnload_ResumeMustRestartByIntent()
    {
        NetDiagnosticsTabViewModel vm = NewDiag();
        vm.PingTarget = "127.0.0.1";
        vm.StartPingCommand.Execute(null);

        vm.CancelPing();                        // 模拟页面卸载（O7 清理仍照常执行）
        await WaitAsync(() => !vm.IsPinging);

        vm.ResumePingIfIntended();              // 视图重新加载（含主题切换重建新实例）

        Assert.True(vm.IsPinging, "用户意图为「跑」⇒ 卸载取消后重新加载必须恢复，否则切主题会静默停掉 ping");

        vm.StopPingCommand.Execute(null);       // 清理：勿在测试进程里留下后台循环
    }

    // ═══════════════════════════ 自动监控（局域网 Tab） ═══════════════════════════

    [Fact]
    public void Monitor_WithoutUserIntent_ResumeMustNotStart()
    {
        LanScanTabViewModel vm = NewLan();

        vm.ResumeMonitorIfIntended();

        Assert.False(vm.IsMonitorOn);
    }

    /// <summary>同 ping 的反模式 ㊷ 锁：用户关掉监控后切主题，不得自动打开。</summary>
    [Fact]
    public void Monitor_AfterUserOff_ResumeMustNotRestart()
    {
        LanScanTabViewModel vm = NewLan();
        vm.IsMonitorOn = true;
        vm.ToggleMonitorCommand.Execute(null); // 用户开
        Assert.True(vm.IsMonitorOn);

        vm.IsMonitorOn = false;
        vm.ToggleMonitorCommand.Execute(null); // 用户关 ⇒ 意图作废

        vm.CancelMonitor();                    // 卸载取消：不得改意图
        vm.ResumeMonitorIfIntended();

        Assert.False(vm.IsMonitorOn);
    }

    [Fact]
    public void Monitor_AfterUnload_ResumeMustRestartByIntent()
    {
        LanScanTabViewModel vm = NewLan();
        vm.IsMonitorOn = true;
        vm.ToggleMonitorCommand.Execute(null); // 用户开
        Assert.True(vm.IsMonitorOn);

        vm.CancelMonitor();                    // 模拟卸载（O7：IsMonitorOn 置 false + Cancel）
        Assert.False(vm.IsMonitorOn);

        vm.ResumeMonitorIfIntended();          // 视图重新加载

        Assert.True(vm.IsMonitorOn, "用户意图为「开」⇒ 卸载取消后重新加载必须恢复，否则切主题会静默停掉监控");

        vm.IsMonitorOn = false;                // 清理
        vm.ToggleMonitorCommand.Execute(null);
    }

    // ═══════════════════════════ 契约锁（结构不变量） ═══════════════════════════

    /// <summary>
    /// 恢复调用必须留在 <c>_loaded</c> 早退**之前**。
    /// <para>
    /// 理由：恢复判据在 **VM（单例）** 的意图位上、与视图实例无关；一旦放进早退之后，
    /// 任何"<c>_loaded</c> 已被置位但视图实际重新加载"的场景都会被整段跳过 ——
    /// 与 <c>FileTransferView.ResumeTimer</c> 是同一条口径。
    /// </para>
    /// </summary>
    [Fact]
    public void View_MustCallResumeBeforeLoadedEarlyReturn()
    {
        string path = Path.Combine(RepoRoot(), "src/SystemToolkit.Modules.NetManager", "NetManagerView.xaml.cs");
        string text = File.ReadAllText(path);

        int early = text.IndexOf("if (_loaded)", StringComparison.Ordinal);
        Assert.True(early >= 0, "未找到 _loaded 早退 —— 结构已变，本锁需同步修订");

        // 🔴 必须**逐个**断言两个恢复调用：只查其中一个时，另一个被删**不会有任何用例变红**
        // —— VM 侧那 6 个行为用例直接调 VM 方法，天然绕过「View 接线」这一环（本仓已实证过的覆盖洞形态）。
        foreach (string call in new[] { "ResumePingIfIntended()", "ResumeMonitorIfIntended()" })
        {
            int at = text.IndexOf(call, StringComparison.Ordinal);
            Assert.True(at >= 0,
                $"NetManagerView.OnLoaded 未调用 {call} —— 恢复接线被移除或改名，本锁需同步修订");
            Assert.True(at < early,
                $"{call} 必须位于 `_loaded` 早退之前（与 FileTransferView.ResumeTimer 同口径）");
        }
    }

    /// <summary>
    /// 🟠 V17-1：`OnLoaded` 的两个恢复调用必须位于 `try { }` **之内**。
    /// <para>
    /// 落在 try 外时：它们抛异常会连带跳过 `_loaded` 置位与 `LoadAsync()` ⇒ 该次首屏数据不加载；
    /// 且 `async void` 的异常不受命令 catch 守卫覆盖，会直冲 Dispatcher（只剩一条错误日志、无用户提示）。
    /// </para>
    /// <para>
    /// 🔴 锚点必须用**带缩进与换行**的 `"\n        try\n"` —— 注释正文里同样写着"try 内"三个字，
    /// 用裸 `IndexOf("try")` 会命中注释 ⇒ 把调用移出 try 时判据**照样通过**（反模式 ㊿ 的第一形态）。
    /// </para>
    /// </summary>
    [Fact]
    public void View_MustCallResumeInsideTry()
    {
        string path = Path.Combine(RepoRoot(), "src/SystemToolkit.Modules.NetManager", "NetManagerView.xaml.cs");
        string text = File.ReadAllText(path);

        int methodAt = text.IndexOf("private async void OnLoaded", StringComparison.Ordinal);
        Assert.True(methodAt >= 0, "未找到 OnLoaded —— 结构已变，本锁需同步修订");

        int tryAt = text.IndexOf("\n        try\n", methodAt, StringComparison.Ordinal);
        int catchAt = text.IndexOf("\n        catch", methodAt, StringComparison.Ordinal);
        Assert.True(tryAt > methodAt, "OnLoaded 内未找到 try 块 —— 结构已变，本锁需同步修订");
        Assert.True(catchAt > tryAt, "OnLoaded 的 try 之后未找到 catch —— 结构已变，本锁需同步修订");

        foreach (string call in new[] { "ResumePingIfIntended()", "ResumeMonitorIfIntended()" })
        {
            int at = text.IndexOf(call, methodAt, StringComparison.Ordinal);
            Assert.True(at >= 0, $"OnLoaded 未调用 {call} —— 恢复接线被移除或改名，本锁需同步修订");
            Assert.True(at > tryAt && at < catchAt,
                $"{call} 必须位于 OnLoaded 的 try 之内（v17-🟠-1）：落在 try 外时，"
                + "它抛异常会连带跳过 `_loaded` 置位与 `LoadAsync()` ⇒ 该次首屏数据不加载，"
                + "且 async void 的异常不受命令 catch 守卫覆盖（只剩一条错误日志、无用户提示）");
        }
    }

    /// <summary>
    /// Split 守护**有意**不设意图位：它的恢复由 <c>LoadAsync</c> 的「台账在位」外部判据承担。
    /// 本锁钉住这条裁定的两侧 —— 若有人删掉台账自恢复、又不补意图位，此处会红。
    /// </summary>
    [Fact]
    public void SplitGuard_MustKeepLedgerSelfRecovery()
    {
        string path = Path.Combine(RepoRoot(), "src/SystemToolkit.Modules.NetManager", "SplitRouteTabViewModel.cs");
        string text = File.ReadAllText(path);

        Assert.Contains("不需要「用户意图位」", text); // 口径差异必须"有解释"，不是漏改

        // 🔴 必须断言「LoadAsync 内那处」StartGuardLoop —— 只 Assert.Contains("StartGuardLoop();")
        // 是**弱判据**：该字符串在 ToggleGuard() 与 StartGuardLoop() 方法定义里各有一份，
        // 删掉自恢复那一处判据照样通过（2026-09-15 反向验证实测踩到）。
        // 故改为「锚点行之后紧跟」的位置相关判据（不写死缩进，避免缩进一变就误红）。
        int anchor = text.IndexOf("台账在位默认起守护", StringComparison.Ordinal);
        Assert.True(anchor >= 0,
            "未找到「台账在位默认起守护」—— Split 的自恢复路径已被移除，本锁需同步修订");

        int startLoop = text.IndexOf("StartGuardLoop();", anchor, StringComparison.Ordinal);
        Assert.True(startLoop > anchor && startLoop - anchor < 200,
            "「台账在位默认起守护」之后必须紧跟 StartGuardLoop() —— 删掉它，守护就失去唯一的自恢复路径"
            + "（Split 有意不设用户意图位，故 LoadAsync 这处是它的全部恢复能力）");
    }

    private static string RepoRoot()
    {
        DirectoryInfo? d = new(AppContext.BaseDirectory);
        while (d is not null && !File.Exists(Path.Combine(d.FullName, "SystemToolkit.sln")))
        {
            d = d.Parent;
        }

        return d?.FullName ?? throw new InvalidOperationException("未找到仓库根（SystemToolkit.sln）");
    }
}
