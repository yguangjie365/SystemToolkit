using System.Runtime.ExceptionServices;
using SystemToolkit.Core.GameManager.Models;
using SystemToolkit.Core.GameManager.Services;
using SystemToolkit.Modules.GameManager;

namespace SystemToolkit.Tests.GameManager;

/// <summary>
/// B2 库存显示（2026-09-13 游戏管理试点批次 3 后半）的纯逻辑回归。
/// <para>
/// 覆盖三处**手点验不出 / 最容易写错**的行为：
/// ① 未安装的判据优先级最高——未安装条目 <c>StateFlags=0</c>，若先判 bit2 就会显示成
/// 「下载/更新中」（**告诉用户一个没发生的事实**）；
/// ② 未安装被隐藏时，空态必须说清"是被开关挡住的"而不是"没有匹配"；
/// ③ 未安装条目的占用显示 <c>—</c> 而非 <c>0 KB</c>。
/// </para>
/// <para>VM 构造会创建 ListCollectionView（WPF 类型有线程亲和）→ 全部用例跑在手开 STA 线程上。</para>
/// <para>⚠️ 所有卡片都给了**假封面路径**：<c>HasCover</c> 为真 → 勾选开关时不会触发 CDN 补封面，
/// 测试因此不发任何网络请求（项目对回环/外网测试有明确的 flaky 纪律）。</para>
/// </summary>
public class GameManagerInventoryDisplayTests
{
    private const string FakeCover = @"C:\fake\cover.jpg";

    private static SteamInventoryGame Game(uint appId, string name, bool installed, uint stateFlags = 4, ulong minutes = 0) =>
        new()
        {
            AppId = appId,
            Name = name,
            Installed = installed,
            StateFlags = stateFlags,
            PlaytimeMinutes = minutes,
            SizeOnDisk = installed ? 1024UL * 1024 * 1024 : 0,
            InstallDir = installed ? name : string.Empty,
            LibraryPath = installed ? @"D:\SteamLibrary" : string.Empty,
        };

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

    private static GameManagerViewModel NewVmWith(params SteamInventoryGame[] games)
    {
        var vm = new GameManagerViewModel(new SteamService());
        foreach (SteamInventoryGame g in games)
        {
            vm.Games.Add(new GameCardVm(g, FakeCover, false, vm));
        }

        vm.ApplyFilter();
        return vm;
    }

    // ==================== 状态判据（优先级） ====================

    [Fact]
    public void StateKind_NotInstalled_OutranksDownloading()
    {
        RunOnSta(() =>
        {
            var vm = new GameManagerViewModel(new SteamService());
            var card = new GameCardVm(Game(9, "Gone", installed: false), FakeCover, false, vm);

            // 未安装条目的 StateFlags 是 0 → bit2 未置位。判据若先看 bit2 会错报「下载/更新中」
            Assert.Equal(GameCardState.NotInstalled, card.StateKind);
            Assert.Equal("未安装", card.StateText);
            Assert.True(card.IsNotInstalled);
            Assert.False(card.IsInstalled);
        });
    }

    [Fact]
    public void StateKind_OfflineOutranksDownloading_AndInstalledIsDefault()
    {
        RunOnSta(() =>
        {
            var vm = new GameManagerViewModel(new SteamService());
            var offline = new GameCardVm(Game(1, "Off", true, stateFlags: 0), FakeCover, true, vm);
            var downloading = new GameCardVm(Game(2, "Down", true, stateFlags: 0), FakeCover, false, vm);
            var installed = new GameCardVm(Game(3, "On", true, stateFlags: 4), FakeCover, false, vm);

            Assert.Equal(GameCardState.LibraryOffline, offline.StateKind);
            Assert.Equal(GameCardState.Downloading, downloading.StateKind);
            Assert.Equal(GameCardState.Installed, installed.StateKind);
            Assert.Equal("已安装", installed.StateText);
        });
    }

    // ==================== 数值占位 ====================

    [Fact]
    public void SizeText_NotInstalled_ShowsPlaceholderInsteadOfZero()
    {
        RunOnSta(() =>
        {
            var vm = new GameManagerViewModel(new SteamService());
            var gone = new GameCardVm(Game(9, "Gone", installed: false), FakeCover, false, vm);
            var here = new GameCardVm(Game(10, "Here", installed: true), FakeCover, false, vm);

            // 「0 KB」会把"没装"说成"装了但极小"
            Assert.Equal("—", gone.SizeText);
            Assert.Equal("—", gone.SizeOnDiskText);
            Assert.NotEqual("—", here.SizeText);
        });
    }

    [Fact]
    public void NotInstalled_HasNoInstallPath_SoDetailRowHides()
    {
        RunOnSta(() =>
        {
            var vm = new GameManagerViewModel(new SteamService());
            var gone = new GameCardVm(Game(9, "Gone", installed: false), FakeCover, false, vm);

            Assert.False(gone.HasInstallPath);
            Assert.Equal(string.Empty, gone.InstallPathText);
        });
    }

    // ==================== 过滤与可见集 ====================

    [Fact]
    public void Filter_ToggleOff_HidesNotInstalled()
    {
        RunOnSta(() =>
        {
            GameManagerViewModel vm = NewVmWith(
                Game(1, "Installed", installed: true),
                Game(2, "Gone", installed: false));

            Assert.False(vm.ShowNotInstalled);
            Assert.Equal(1, vm.VisibleGameCount);
            Assert.Equal(1, vm.HiddenNotInstalledCount);
            Assert.Equal(2, vm.Games.Count); // 数据在，只是不显示
        });
    }

    [Fact]
    public void Filter_ToggleOn_RevealsNotInstalled()
    {
        RunOnSta(() =>
        {
            GameManagerViewModel vm = NewVmWith(
                Game(1, "Here", installed: true),
                Game(2, "Gone", installed: false));

            vm.ShowNotInstalled = true; // 属性变化内部即重算过滤

            Assert.Equal(2, vm.VisibleGameCount);
            Assert.False(vm.ShowNoResultEmpty);
        });
    }

    [Fact]
    public void SearchAndToggle_AreIntersected()
    {
        RunOnSta(() =>
        {
            GameManagerViewModel vm = NewVmWith(
                Game(1, "Alpha Installed", installed: true),
                Game(2, "Alpha Gone", installed: false),
                Game(3, "Beta Installed", installed: true));

            vm.SearchQuery = "Alpha";
            Assert.Equal(1, vm.VisibleGameCount); // 只 Alpha Installed

            vm.ShowNotInstalled = true;
            Assert.Equal(2, vm.VisibleGameCount); // 两个 Alpha
        });
    }

    // ==================== 空态：必须说清"被什么挡住" ====================

    [Fact]
    public void FilteredEmpty_AllHidden_ExplainsTheToggle()
    {
        RunOnSta(() =>
        {
            GameManagerViewModel vm = NewVmWith(
                Game(1, "Gone A", installed: false),
                Game(2, "Gone B", installed: false));

            Assert.True(vm.ShowNoResultEmpty); // 有数据但可见集为 0 → 不能是一片空白
            Assert.Equal("未找到可显示的游戏", vm.FilteredEmptyTitle);
            Assert.Contains("2 款未安装", vm.FilteredEmptyHint, StringComparison.Ordinal);
            Assert.Contains("显示未安装", vm.FilteredEmptyHint, StringComparison.Ordinal);
        });
    }

    [Fact]
    public void FilteredEmpty_SearchMiss_KeepsKeywordHint()
    {
        RunOnSta(() =>
        {
            GameManagerViewModel vm = NewVmWith(Game(1, "Alpha", installed: true));

            vm.SearchQuery = "zzz";

            Assert.True(vm.ShowNoResultEmpty);
            Assert.Equal("未找到匹配的游戏", vm.FilteredEmptyTitle);
            Assert.DoesNotContain("未安装", vm.FilteredEmptyHint, StringComparison.Ordinal);
        });
    }

    [Fact]
    public void HeaderSubtitle_CountsOnlyVisible()
    {
        RunOnSta(() =>
        {
            GameManagerViewModel vm = NewVmWith(
                Game(1, "Here", installed: true),
                Game(2, "Gone", installed: false));

            // 页头数字必须与网格所见一致（否则用户会以为游戏丢了）
            Assert.Equal("1 款游戏 · 0 个库", vm.HeaderSubtitle);

            vm.ShowNotInstalled = true;
            Assert.Equal("2 款游戏 · 0 个库", vm.HeaderSubtitle);
        });
    }

    // ==================== meta 行文本（2026-09-13 UI 评审） ====================

    /// <summary>
    /// 卡片 meta 行改用**紧凑时长**（拉丁单位，与同行的「49.9 GB」风格一致），
    /// 而详情面板保留可读的长格式——两者必须同时成立：评审只针对卡片那一行。
    /// </summary>
    [Theory]
    [InlineData(0UL, "0h", "从未游玩")]
    [InlineData(5UL, "5m", "5 分钟")]
    [InlineData(59UL, "59m", "59 分钟")]
    [InlineData(60UL, "1h", "1 小时 0 分")]
    [InlineData(3670UL, "61h10m", "61 小时 10 分")]
    [InlineData(54710UL, "911h50m", "911 小时 50 分")]
    public void CardMetaPlaytime_IsCompact_DetailKeepsLongFormat(
        ulong minutes, string expectedShort, string expectedLong)
    {
        RunOnSta(() =>
        {
            GameManagerViewModel vm = NewVmWith(Game(1, "任意游戏", installed: true, minutes: minutes));
            GameCardVm card = vm.Games[0];

            Assert.Equal(expectedShort, card.PlaytimeShortText);
            Assert.Equal(expectedLong, card.PlaytimeText);
        });
    }
}
