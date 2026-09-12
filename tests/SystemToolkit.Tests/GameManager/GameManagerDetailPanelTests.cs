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
    private static SteamInventoryGame Game(
        uint appId,
        string name = "SomeGame",
        string installDir = "SomeGame",
        string libraryPath = @"D:\SteamLibrary",
        ulong sizeOnDisk = 0,
        ulong playtimeMinutes = 0,
        uint stateFlags = 4,
        bool installed = true) =>
        new()
        {
            AppId = appId,
            Name = name,
            Installed = installed,
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

            // 位语义按 Steam EAppState：bit1 = UpdateRequired(2)、bit2 = FullyInstalled(4)。
            // 🔴 2026-09-13 收紧：6 = bit1+bit2 是「已安装但待更新」，不能再落到「已安装」。
            var installed = new GameCardVm(Game(1, stateFlags: 4), "c.png", false, vm);
            var downloading = new GameCardVm(Game(2, stateFlags: 2), "c.png", false, vm);
            var needsUpdate = new GameCardVm(Game(4, stateFlags: 6), "c.png", false, vm);
            var offline = new GameCardVm(Game(3, stateFlags: 4), "c.png", true, vm);
            var notInstalled = new GameCardVm(Game(5, stateFlags: 0, installed: false), "c.png", false, vm);

            // 纯已安装（bit2 且非 bit1）
            Assert.True(installed.IsFullyInstalled);
            Assert.False(installed.NeedsUpdate);
            Assert.Equal(GameCardState.Installed, installed.StateKind);
            Assert.Equal("已安装", installed.StateText);
            Assert.False(installed.ShowProgressBadge);

            // 只有 bit1（UpdateRequired，无 bit2）→ 未装全
            Assert.False(downloading.IsFullyInstalled);
            Assert.False(downloading.NeedsUpdate);
            Assert.Equal(GameCardState.Downloading, downloading.StateKind);
            Assert.Equal("下载/更新中", downloading.StateText);
            Assert.True(downloading.ShowProgressBadge);

            // 🔴 收紧的那一条：bit2+bit1 同置
            Assert.False(needsUpdate.IsFullyInstalled);
            Assert.True(needsUpdate.NeedsUpdate);
            Assert.Equal(GameCardState.NeedsUpdate, needsUpdate.StateKind);
            Assert.Equal("需更新", needsUpdate.StateText);
            Assert.True(needsUpdate.ShowProgressBadge);

            // 库离线优先于「需更新」（优先级：未安装 > 库离线 > 需更新）
            Assert.Equal(GameCardState.LibraryOffline, offline.StateKind);
            Assert.Equal("库离线", offline.StateText);
            Assert.False(offline.ShowProgressBadge);

            // 未安装（库存里的"见过但没装"条目）：StateFlags=0 也会让 bit2 为假，
            // 所以进度徽章**必须**绑精确状态判据——否则「未安装」与「下载/更新中」两块徽章同时可见
            // （封面遮罩是半透明渐变 #80000000，压不住下面那块）。这条是 2026-09-13 修叠加徽章时的回归锁。
            Assert.Equal(GameCardState.NotInstalled, notInstalled.StateKind);
            Assert.False(notInstalled.IsFullyInstalled);
            Assert.False(notInstalled.ShowProgressBadge);
        });
    }
}
