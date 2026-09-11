using System.Collections.ObjectModel;
using System.ComponentModel;
using Microsoft.Extensions.DependencyInjection;
using SystemToolkit.Core.Music.Models;
using SystemToolkit.Core.Music.Services;
using SystemToolkit.Modules.MusicManager;

namespace SystemToolkit.Tests.Music;

/// <summary>
/// 音乐 VM 轻量单测（MUSIC-8）：不依赖引擎与真实扫描的纯逻辑——搜索过滤与模式切换。
/// 扫描端到端需要真实音频文件（TagLib 校验），由真机验收承担。
/// </summary>
public class MusicManagerViewModelTests
{
    private static MusicManagerViewModel CreateVm(out string dir)
    {
        dir = Path.Combine(Path.GetTempPath(), $"music-vm-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        return new MusicManagerViewModel(
            store: new JsonMusicLibraryStore(Path.Combine(dir, "music-library.json")),
            scanner: new LocalMusicScanner(new NoopLogger(), new TagLibMusicTagReader(new NoopLogger())),
            queue: new PlaybackQueueService(),
            log: new NoopLogger());
    }

    [Fact]
    public void FilterText_FiltersByTitleArtistAlbum_CaseInsensitive()
    {
        MusicManagerViewModel vm = CreateVm(out string dir);
        try
        {
            vm.Songs.Add(new MusicSong { Id = "a", LocalPath = @"C:/m/a.mp3", Name = "晴天", Artist = "周杰伦", Album = "叶惠美" });
            vm.Songs.Add(new MusicSong { Id = "b", LocalPath = @"C:/m/b.mp3", Name = "夜曲", Artist = "jay", Album = "十一月的萧邦" });

            ICollectionView view = vm.SongsView;
            Assert.Equal(2, view.Cast<object>().Count());

            vm.FilterText = "周杰伦";
            Assert.Single(view.Cast<object>());

            vm.FilterText = "JAY"; // 艺术家忽略大小写
            Assert.Single(view.Cast<object>());

            vm.FilterText = "萧邦"; // 专辑命中
            Assert.Single(view.Cast<object>());

            vm.FilterText = string.Empty;
            Assert.Equal(2, view.Cast<object>().Count());
        }
        finally
        {
            try
            {
                Directory.Delete(dir, recursive: true);
            }
            catch
            {
                // 清理失败不阻塞断言
            }
        }
    }

    [Fact]
    public void ToggleMode_CyclesListShuffleOne()
    {
        MusicManagerViewModel vm = CreateVm(out string _);

        Assert.Equal("列表循环", vm.ModeText);

        vm.ToggleModeCommand.Execute(null);
        Assert.Equal("随机播放", vm.ModeText);

        vm.ToggleModeCommand.Execute(null);
        Assert.Equal("单曲循环", vm.ModeText);

        vm.ToggleModeCommand.Execute(null);
        Assert.Equal("列表循环", vm.ModeText);
    }

    [Fact]
    public void LibraryCountText_ReflectsSongsCollection()
    {
        MusicManagerViewModel vm = CreateVm(out string dir);
        try
        {
            Assert.Equal("曲库 0 首", vm.LibraryCountText);

            vm.Songs.Add(new MusicSong { Id = "x", LocalPath = @"C:/m/x.mp3", Name = "X" });
            Assert.Equal("曲库 1 首", vm.LibraryCountText);
        }
        finally
        {
            try
            {
                Directory.Delete(dir, recursive: true);
            }
            catch
            {
                // 清理失败不阻塞断言
            }
        }
    }

    // ════════ MUSIC-7：Shell 迷你播放条契约映射（IPlaybackBarSource）════════

    [Fact]
    public void PlaybackBar_InitialIdleState_MapsContractSurface()
    {
        MusicManagerViewModel vm = CreateVm(out string dir);
        try
        {
            var bar = (SystemToolkit.Abstractions.IPlaybackBarSource)vm;

            Assert.False(bar.HasTrack);          // 未选曲 → 播放条不占位
            Assert.Null(bar.NavigationModule);   // harness 未注入模块实例（生产由模块工厂传 this）
            Assert.Equal("未在播放", bar.Title);
            Assert.False(bar.IsPlaying);
            Assert.Null(bar.Cover);
            Assert.Equal(0, bar.ProgressPercent);
            Assert.Same(vm.PlayPauseCommand, bar.TogglePlayCommand); // 命令别名同源，不复制逻辑
            Assert.Same(vm.NextCommand, bar.NextTrackCommand);
            Assert.Same(vm.PreviousCommand, bar.PreviousTrackCommand);

            // 引擎缺席（headless/未注册）时 Seek 必须安全静默，不抛
            bar.SeekToRatio(0.5);
            bar.SeekToRatio(5);   // 越界钳制
            bar.SeekToRatio(-1);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}

/// <summary>
/// 审查报告 🟠-1 实锤：[RelayCommand(CanExecute = nameof(IsScanning))] 引用同类
/// [ObservableProperty] 生成的属性时，CommunityToolkit 源生成器应自动挂接
/// PropertyChanged → NotifyCanExecuteChanged（无需手写 [NotifyCanExecuteChangedFor]）。
/// 本用例验证该自动行为是否存在——不存在则需手写特性。
/// </summary>
public class MusicManagerCancelScanTests
{
    [Fact]
    public async Task IsScanningChange_AutoNotifiesCancelScanCommand()
    {
        string dir = Path.Combine(Path.GetTempPath(), $"music-cancel-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            var vm = new MusicManagerViewModel(
                store: new JsonMusicLibraryStore(Path.Combine(dir, "music-library.json")),
                scanner: new LocalMusicScanner(new NoopLogger(), new TagLibMusicTagReader(new NoopLogger())),
                queue: new PlaybackQueueService(),
                log: new NoopLogger());

            // 初始：未扫描 → 取消命令不可执行
            Assert.False(vm.CancelScanCommand.CanExecute(null));

            // 模拟扫描开始（IsScanning=true，不真扫描）
            typeof(SystemToolkit.Modules.MusicManager.MusicManagerViewModel)
                .GetProperty("IsScanning")!.SetValue(vm, true);

            Assert.True(vm.CancelScanCommand.CanExecute(null),
                "IsScanning=true 后取消命令应自动变为可执行（源生成器自动通知）");
        }
        finally
        {
            try
            {
                Directory.Delete(dir, recursive: true);
            }
            catch
            {
                // 清理失败不阻塞断言
            }
        }
    }

}
