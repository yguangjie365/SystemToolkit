using System.Runtime.ExceptionServices;
using SystemToolkit.Core.GameManager.Models;
using SystemToolkit.Core.GameManager.Services;
using SystemToolkit.Modules.GameManager;

namespace SystemToolkit.Tests.GameManager;

/// <summary>
/// A2 详情面板（2026-09-13 游戏管理试点批次 2）的纯逻辑回归。
/// <para>
/// 覆盖三处**UI 手点验不出**的行为：
/// ① 面板展示的「安装位置」必须与 <c>SteamService.OpenGameFolder</c> 实际打开的路径**同源**
///    —— 两处各写一份拼法迟早漂移，表现就是「面板说装在这里、点打开目录却打开别处」（状态欺骗）；
/// ② 路径缺任一段时必须是空串 + <c>HasInstallPath=false</c>（UI 隐藏整行，不显示半截路径）；
/// ③ <c>IsDetailOpen</c> 的 <c>PropertyChanged</c> 必须真发出 —— 遮罩与面板的可见性全靠它。
/// </para>
/// <para>VM 构造会创建 ListCollectionView（WPF 类型有线程亲和）→ 全部用例跑在手开 STA 线程上，
/// 与 SteamAccountAndCapacityTests / ViewLoadSmokeGuardTests 同一手法。</para>
/// </summary>
public class GameManagerDetailPanelTests
{
    private static SteamGame Game(
        uint appId,
        string name = "SomeGame",
        string installDir = "SomeGame",
        string libraryPath = @"D:\SteamLibrary",
        ulong sizeOnDisk = 0,
        ulong playtimeMinutes = 0,
        uint stateFlags = 4) =>
        new()
        {
            AppId = appId,
            Name = name,
            InstallDir = installDir,
            LibraryPath = libraryPath,
            SizeOnDisk = sizeOnDisk,
            PlaytimeMinutes = playtimeMinutes,
            StateFlags = stateFlags,
        };

    /// <summary>在 STA 线程上执行并原样重抛断言异常（保留原始堆栈）。</summary>
    private static void RunOnSta(Action body)
    {
        Exception? captured = null;
        var thread = new Thread(() =>
        {
            try
            {
                body();
            }
            catch (Exception ex)
            {
                captured = ex;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.IsBackground = true;
        thread.Start();
        thread.Join(TimeSpan.FromSeconds(30));

        if (captured is not null)
        {
            ExceptionDispatchInfo.Capture(captured).Throw();
        }
    }

    // ==================== 安装位置投影 ====================

    [Fact]
    public void InstallPathText_MatchesOpenGameFolderLayout()
    {
        RunOnSta(() =>
        {
            var vm = new GameManagerViewModel(new SteamService());
            var card = new GameCardVm(
                Game(1245620, "ELDEN RING", installDir: "ELDEN RING", libraryPath: @"D:\SteamLibrary"),
                "cover.png",
                false,
                vm);

            // 与 SteamService.OpenGameFolder 的 Path.Combine(libraryPath,"steamapps","common",installDir) 同源
            Assert.Equal(@"D:\SteamLibrary\steamapps\common\ELDEN RING", card.InstallPathText);
            Assert.True(card.HasInstallPath);
        });
    }

    [Fact]
    public void InstallPathText_BlankLibraryPath_EmptyAndHidden()
    {
        RunOnSta(() =>
        {
            var vm = new GameManagerViewModel(new SteamService());
            var card = new GameCardVm(Game(1, libraryPath: string.Empty), "cover.png", false, vm);

            Assert.Equal(string.Empty, card.InstallPathText);
            Assert.False(card.HasInstallPath); // UI 据 HasInstallPath 隐藏整行
        });
    }

    [Fact]
    public void InstallPathText_BlankInstallDir_EmptyAndHidden()
    {
        RunOnSta(() =>
        {
            var vm = new GameManagerViewModel(new SteamService());
            var card = new GameCardVm(Game(1, installDir: string.Empty), "cover.png", false, vm);

            Assert.Equal(string.Empty, card.InstallPathText);
            Assert.False(card.HasInstallPath);
        });
    }

    [Fact]
    public void AppIdText_HasNoGroupingSeparator()
    {
        RunOnSta(() =>
        {
            var vm = new GameManagerViewModel(new SteamService());
            var card = new GameCardVm(Game(1245620), "cover.png", false, vm);

            // 若误用带千分位的格式（1,245,620 / 1 245 620），复制 AppID 去商店搜就会失败
            Assert.Equal("1245620", card.AppIdText);
        });
    }

    // ==================== 面板开合 ====================

    [Fact]
    public void IsDetailOpen_TogglesWithOpenAndClose()
    {
        RunOnSta(() =>
        {
            var vm = new GameManagerViewModel(new SteamService());
            var card = new GameCardVm(Game(7), "cover.png", false, vm);

            Assert.False(vm.IsDetailOpen);
            Assert.Null(vm.SelectedGame);

            vm.OpenDetailCommand.Execute(card);
            Assert.True(vm.IsDetailOpen);
            Assert.Same(card, vm.SelectedGame);

            vm.CloseDetailCommand.Execute(null);
            Assert.False(vm.IsDetailOpen);
            Assert.Null(vm.SelectedGame);
        });
    }

    [Fact]
    public void OpenDetail_NullParameter_LeavesPanelClosed()
    {
        RunOnSta(() =>
        {
            var vm = new GameManagerViewModel(new SteamService());

            // 空参数（例如绑定在卡片回收瞬间求值为 null）不得把面板打开成"空白详情"
            vm.OpenDetailCommand.Execute(null);

            Assert.False(vm.IsDetailOpen);
        });
    }

    [Fact]
    public void IsDetailOpen_RaisesPropertyChanged_SoOverlayVisibilityFollows()
    {
        RunOnSta(() =>
        {
            var vm = new GameManagerViewModel(new SteamService());
            var card = new GameCardVm(Game(7), "cover.png", false, vm);
            var raised = new List<string?>();
            vm.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

            vm.OpenDetailCommand.Execute(card);
            vm.CloseDetailCommand.Execute(null);

            // 遮罩与面板可见性都绑在 IsDetailOpen 上——不发通知 = 面板永远不出现
            Assert.Contains(nameof(GameManagerViewModel.IsDetailOpen), raised);
        });
    }

    // ==================== 状态徽章判据（与卡片同口径） ====================

    [Fact]
    public void Card_StateFlags_DriveBadgeBranches()
    {
        RunOnSta(() =>
        {
            var vm = new GameManagerViewModel(new SteamService());

            // 判据是位运算 (StateFlags & 4) != 0，即 bit2=FullyInstalled。
            // ⚠️ 注意 StateFlags=6 也含 bit2 → 按现有实现 IsFullyInstalled 为 true（StateText 显示「已安装」），
            //    而 SteamGame.StateFlags 的文档注释把 6 写作「下载中」——两者口径不一致，属**既存问题**。
            //    本用例只钉住实现自身的位语义，不替它改判据（改判据属另一变更单元，已在变更记录中报告）。
            var installed = new GameCardVm(Game(1, stateFlags: 4), "c.png", false, vm);
            var downloading = new GameCardVm(Game(2, stateFlags: 2), "c.png", false, vm);
            var offline = new GameCardVm(Game(3, stateFlags: 4), "c.png", true, vm);

            Assert.True(installed.IsFullyInstalled);    // bit2 置位 → 徽章走「已安装」
            Assert.False(downloading.IsFullyInstalled); // 只有 bit1（UpdateRequired）→ 徽章走「下载/更新中」
            Assert.True(offline.IsLibraryOffline);      // 徽章走「库离线」（优先级最高）
        });
    }
}
