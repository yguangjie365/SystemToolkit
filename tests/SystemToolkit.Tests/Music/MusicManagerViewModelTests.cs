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
}
