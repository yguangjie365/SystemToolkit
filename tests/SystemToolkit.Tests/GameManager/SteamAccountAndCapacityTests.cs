using System.Runtime.ExceptionServices;
using SystemToolkit.Core.GameManager.Models;
using SystemToolkit.Core.GameManager.Services;
using SystemToolkit.Modules.GameManager;

namespace SystemToolkit.Tests.GameManager;

/// <summary>
/// A1 账户选择器 + A3 库容量（2026-09-13 游戏管理试点批次 1）的纯逻辑回归。
/// <para>
/// 覆盖两处**UI 手点验不出来**的行为：
/// ① 账户「当前项」是按 SteamID64 认的（不是按列表下标 / 不是"第一个"）；
/// ② 库容量剩余空间按**卷根去重**（同一分区放多个库时不得重复计数）。
/// </para>
/// <para>VM 构造会创建 ListCollectionView（WPF 类型有线程亲和）→ 全部用例跑在手开 STA 线程上，
/// 与 ViewLoadSmokeGuardTests / WindowSmokeGuardTests 同一手法。</para>
/// </summary>
public class SteamAccountAndCapacityTests
{
    private const ulong OneGb = 1024UL * 1024 * 1024;

    private static SteamUser User(string id, string account, string persona, bool mostRecent = false) =>
        new()
        {
            SteamId64 = id,
            AccountName = account,
            PersonaName = persona,
            MostRecent = mostRecent,
        };

    private static SteamLibrary Lib(string path, ulong freeSize) =>
        new()
        {
            Index = 0,
            Path = path,
            Label = string.Empty,
            Apps = Array.Empty<string>(),
            FreeSize = freeSize,
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

    // ==================== A1 账户 ====================

    [Fact]
    public void BuildAccounts_NoUsers_EmptyAndNotPickable()
    {
        RunOnSta(() =>
        {
            var vm = new GameManagerViewModel(new SteamService());

            vm.BuildAccounts(Array.Empty<SteamUser>(), null);

            Assert.Empty(vm.Accounts);
            Assert.False(vm.HasAccounts);
            Assert.False(vm.CanPickAccount); // 无账户 → 下拉按钮禁用（ToolTip 给出原因）
        });
    }

    [Fact]
    public void BuildAccounts_MarksCurrentBySteamId_NotByPosition()
    {
        RunOnSta(() =>
        {
            var vm = new GameManagerViewModel(new SteamService());
            SteamUser[] users =
            [
                User("111", "alpha", "阿青"),
                User("222", "beta", "Beta", mostRecent: true),
                User("333", "gamma", "Gamma"),
            ];

            vm.BuildAccounts(users, users[1]);

            Assert.Equal(3, vm.Accounts.Count);
            Assert.True(vm.HasAccounts);
            Assert.True(vm.CanPickAccount);
            Assert.False(vm.Accounts[0].IsCurrent);
            Assert.True(vm.Accounts[1].IsCurrent);
            Assert.False(vm.Accounts[2].IsCurrent);
        });
    }

    [Fact]
    public void BuildAccounts_ActiveNotInList_NothingMarked()
    {
        RunOnSta(() =>
        {
            var vm = new GameManagerViewModel(new SteamService());
            SteamUser[] users = [User("111", "alpha", "阿青")];

            // 活动账户不在记住列表里（loginusers.vdf 被外部改过）→ 不得误标第一个
            vm.BuildAccounts(users, User("999", "ghost", "Ghost"));

            Assert.Single(vm.Accounts);
            Assert.False(vm.Accounts[0].IsCurrent);
        });
    }

    [Fact]
    public void SteamAccountVm_DisplayName_PrefersPersonaNameAndFallsBack()
    {
        RunOnSta(() =>
        {
            var vm = new GameManagerViewModel(new SteamService());

            var named = new SteamAccountVm(User("1", "alpha", "阿青"), true, null, vm);
            var blank = new SteamAccountVm(User("2", "beta", "   "), false, null, vm);

            Assert.Equal("阿青", named.DisplayName);
            Assert.Equal("beta", blank.DisplayName);
        });
    }

    [Fact]
    public void SteamAccountVm_Initial_BlankNameFallsBackToSt()
    {
        RunOnSta(() =>
        {
            var vm = new GameManagerViewModel(new SteamService());

            var blank = new SteamAccountVm(User("1", "   ", "   "), false, null, vm);
            var normal = new SteamAccountVm(User("2", "alpha", "阿青"), false, null, vm);

            Assert.Equal("St", blank.Initial);
            Assert.Equal("阿", normal.Initial);
        });
    }

    [Fact]
    public void SteamAccountVm_HasAvatar_EmptyStringCountsAsNoAvatar()
    {
        RunOnSta(() =>
        {
            var vm = new GameManagerViewModel(new SteamService());

            var nullPath = new SteamAccountVm(User("1", "a", "A"), false, null, vm);
            var emptyPath = new SteamAccountVm(User("2", "b", "B"), false, string.Empty, vm);
            var realPath = new SteamAccountVm(User("3", "c", "C"), false, @"C:\x\avatar.png", vm);

            Assert.False(nullPath.HasAvatar);
            // 🔴 空串也必须判"无头像"——写成 "is not null" 会在这里红（GameCardVm 同款坑）
            Assert.False(emptyPath.HasAvatar);
            Assert.True(realPath.HasAvatar);
        });
    }

    // ==================== A3 库容量 ====================

    [Fact]
    public void UpdateLibraryCapacity_NoLibraries_TextEmpty()
    {
        RunOnSta(() =>
        {
            var vm = new GameManagerViewModel(new SteamService());

            vm.UpdateLibraryCapacity(Array.Empty<SteamLibrary>());

            Assert.Equal(string.Empty, vm.LibraryCapacityText);
            Assert.False(vm.HasLibraryCapacity); // UI 据此隐藏该段，不留悬空分隔符
        });
    }

    [Fact]
    public void UpdateLibraryCapacity_SameVolumeMultipleLibraries_DedupsRemainingSpace()
    {
        RunOnSta(() =>
        {
            var vm = new GameManagerViewModel(new SteamService());
            // C 盘两个库（各自报的是**同一份**可用空间）+ D 盘一个库。
            // 正确：2 + 3 = 5 GB；逐库累加的错法会得到 2 + 2 + 3 = 7 GB。
            SteamLibrary[] libs =
            [
                Lib(@"C:\SteamLibraryA", 2 * OneGb),
                Lib(@"C:\SteamLibraryB", 2 * OneGb),
                Lib(@"D:\Games", 3 * OneGb),
            ];

            vm.UpdateLibraryCapacity(libs);

            Assert.Equal("· 库 3 个 · 剩余 5 GB", vm.LibraryCapacityText);
            Assert.True(vm.HasLibraryCapacity);
        });
    }

    [Fact]
    public void UpdateLibraryCapacity_NoVolumeReadable_ReportsCountOnly()
    {
        RunOnSta(() =>
        {
            var vm = new GameManagerViewModel(new SteamService());
            // 离线盘/网络盘：FillDriveSize 吞异常 → FreeSize 保持 0
            SteamLibrary[] libs = [Lib(@"Z:\Offline", 0), Lib(@"Y:\Offline", 0)];

            vm.UpdateLibraryCapacity(libs);

            Assert.Equal("· 库 2 个", vm.LibraryCapacityText);
            Assert.True(vm.HasLibraryCapacity);
        });
    }
}
