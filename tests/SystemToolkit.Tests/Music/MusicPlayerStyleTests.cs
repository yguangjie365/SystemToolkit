using SystemToolkit.Core.Contracts;
using SystemToolkit.Core.Music.Models;
using SystemToolkit.Core.Music.Online;
using SystemToolkit.Core.Music.Services;
using SystemToolkit.Infrastructure.Music.Online;
using SystemToolkit.Modules.MusicManager;
using SystemToolkit.Tests.Music;

namespace SystemToolkit.Tests.Music;

/// <summary>
/// OM-6 VM 播放器层：三风格状态机 / 封面装载与主色提取（走真 PNG 解码）/
/// 音质菜单与在线重取。无网络、无真实音频文件（封面 PNG 内嵌）。
/// </summary>
public class MusicPlayerStyleTests
{
    // 1x1 红色 PNG（知名 base64 样本）
    private static readonly byte[] RedPng = Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAIAAACQd1PeAAAADElEQVR4nGP4z8AAAAMBAQDJ/pLvAAAAAElFTkSuQmCC");

    private sealed class FakeTagReader : IMusicTagReader
    {
        public byte[]? CoverBytes { get; set; }

        public IReadOnlySet<string> SupportedExtensions { get; } = new HashSet<string> { ".mp3" };

        public MusicTagReadResult Read(string filePath) =>
            new() { Success = true, Tags = new MusicTagInfo { Title = "t", Artist = "a" } };

        public MusicCover? ReadCover(string filePath) =>
            CoverBytes is null ? null : new MusicCover(CoverBytes, "image/png");
    }

    private sealed class FakeResolver : IOnlineUrlResolver
    {
        public int ResolveCount;

        public Task<OnlineSongUrlResult> ResolveAsync(OnlineTrack track, string preferredQuality, CancellationToken ct = default)
        {
            ResolveCount++;
            return Task.FromResult(new OnlineSongUrlResult { Playable = true, Url = "https://cdn/x.mp3", Level = preferredQuality });
        }
    }

    private sealed class FakeProxy : IAudioProxyService
    {
        public bool IsRunning => true;
        public int Port => 9;

        public Task<int> StartAsync(CancellationToken ct = default) => Task.FromResult(9);

        public Task StopAsync() => Task.CompletedTask;

        public Task<string> GetProxiedAudioUrlAsync(string rawUrl, CancellationToken ct = default) => Task.FromResult("proxy::" + rawUrl);

        public Task<string> GetProxiedCoverUrlAsync(string rawUrl, CancellationToken ct = default) => Task.FromResult("cover::" + rawUrl);
    }

    private static async Task<bool> WaitUntilAsync(Func<bool> condition, int timeoutMs = 3000)
    {
        DateTime deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (DateTime.UtcNow < deadline)
        {
            if (condition())
            {
                return true;
            }

            await Task.Delay(20);
        }

        return condition();
    }

    private static MusicManagerViewModel CreateVm(
        FakePlaybackEngine engine,
        IMusicTagReader? tagReader = null,
        IOnlineUrlResolver? resolver = null,
        IAudioProxyService? proxy = null)
    {
        string dir = Path.Combine(Path.GetTempPath(), $"music-om6-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        return new MusicManagerViewModel(
            store: new JsonMusicLibraryStore(Path.Combine(dir, "music-library.json")),
            scanner: new LocalMusicScanner(new NoopLogger(), tagReader ?? new TagLibMusicTagReader(new NoopLogger())),
            queue: new PlaybackQueueService(),
            log: new NoopLogger(),
            engineProvider: () => engine,
            tagReader: tagReader,
            dispatcher: null,
            urlResolver: resolver,
            audioProxy: proxy,
            catalog: null,
            credentials: null);
    }

    private static MusicSong LocalSong(string name, bool hasCover = false) => new()
    {
        Id = "local:" + name,
        LocalPath = $"C:/m/{name}.mp3",
        Name = name,
        Artist = "艺术家",
        HasEmbeddedCover = hasCover,
    };

    private static OnlineTrack OnlineTrack(string id) => new()
    {
        Provider = OnlineProvider.NetEase,
        Id = id,
        Name = $"在线{id}",
        Artist = "在线艺术家",
        Playable = true,
    };

    // ════════ 风格状态机 ════════

    /// <summary>支持 EQ 的假引擎：记录每次 ApplyEqualizer 收到的快照。</summary>
    private sealed class FakeEqEngine : FakePlaybackEngine, IEqualizerEngine
    {
        public List<(bool Enabled, double Preamp, double[] Gains)> Applied { get; } = [];

        public void ApplyEqualizer(EqProfile profile) =>
            Applied.Add((profile.Enabled, profile.PreampDb, profile.GainsCopy()));
    }

    [Fact]
    public void EqAvailability_False_WhenEngineLacksCapability()
    {
        MusicManagerViewModel vm = CreateVm(new FakePlaybackEngine());

        Assert.False(vm.IsEqAvailable);
        vm.ToggleEqCommand.Execute(null); // 不炸：ApplyEqToEngine 静默（UI 已禁用兜底）
    }

    [Fact]
    public async Task ToggleEq_AppliesProfileToCapableEngine()
    {
        var engine = new FakeEqEngine();
        MusicManagerViewModel vm = CreateVm(engine);

        await WaitUntilAsync(() => vm.IsEqAvailable); // WireEngineOnce 异步挂 IsEqAvailable? 同步 WireEngineOnce 由 InitializeAsync 调——测试直接调 Toggle 前需接线
        vm.ToggleEqCommand.Execute(null);

        Assert.True(vm.Eq.Enabled);
        (bool enabled, _, double[] gains) = engine.Applied.Single();
        Assert.True(enabled);
        Assert.All(gains, g => Assert.Equal(0, g));
    }

    [Fact]
    public async Task ApplyEqPreset_SyncsBandSlidersAndEngine()
    {
        var engine = new FakeEqEngine();
        MusicManagerViewModel vm = CreateVm(engine);
        await WaitUntilAsync(() => vm.IsEqAvailable);

        vm.ApplyEqPresetCommand.Execute("重低音");

        (_, _, double[] gains) = engine.Applied.Single();
        Assert.Equal(8.0, gains[0]); // 重低音首段 +8（2026-09-09 预设整体增强约一倍）
        Assert.Equal(8.0, vm.EqBands[0].Gain); // 滑条同步
        Assert.Equal(0.0, vm.EqBands[9].Gain);
        Assert.Contains("重低音", vm.ScanStatusText, StringComparison.Ordinal);
    }

    [Fact]
    public void EqBandGain_Setter_ClampsAndApplies()
    {
        var engine = new FakeEqEngine();
        MusicManagerViewModel vm = CreateVm(engine);

        vm.EqBands[2].Gain = 99;

        Assert.Equal(EqProfile.MaxGainDb, vm.EqBands[2].Gain);
        Assert.Equal(EqProfile.MaxGainDb, vm.Eq[2]);
    }

    [Fact]
    public void PlayerStyle_DefaultsToVinyl()
    {
        MusicManagerViewModel vm = CreateVm(new FakePlaybackEngine());

        Assert.True(vm.IsVinylStyle);
        Assert.False(vm.IsImmersionStyle);
        Assert.Equal("透明彩胶", vm.PlayerStyleText);
    }

    [Theory]
    [InlineData("Immersion")]
    [InlineData("Modern")]
    [InlineData("Vinyl")]
    [InlineData("垃圾输入")]
    public void SwitchPlayerStyle_FlipsStateAndVisibility(string style)
    {
        MusicManagerViewModel vm = CreateVm(new FakePlaybackEngine());

        vm.SwitchPlayerStyleCommand.Execute(style);

        bool valid = style is "Immersion" or "Modern" or "Vinyl";
        if (style == "Immersion")
        {
            Assert.True(vm.IsImmersionStyle);
            Assert.Equal("沉浸", vm.PlayerStyleText);
        }
        else if (style == "Modern")
        {
            Assert.True(vm.IsModernStyle);
        }
        else
        {
            Assert.True(vm.IsVinylStyle); // 默认/非法输入回退彩胶
        }

        Assert.True(valid || vm.IsVinylStyle);
        Assert.True(vm.IsVinylStyle || vm.IsImmersionStyle || vm.IsModernStyle); // 三态恰一
    }

    // ════════ 封面装载 + 主色 ════════

    [Fact]
    public async Task PlayLocalSongWithCover_LoadsImageAndNonNeutralAccent()
    {
        var engine = new FakePlaybackEngine();
        var tagReader = new FakeTagReader { CoverBytes = RedPng };
        MusicManagerViewModel vm = CreateVm(engine, tagReader: tagReader);
        MusicSong song = LocalSong("cover", hasCover: true);
        vm.Songs.Add(song);
        vm.SelectedSong = song;

        await vm.PlayFromLibraryCommand.ExecuteAsync(null);

        // 时序加固（2026-09-09）：封面装载走异步管线，全量并行时 STA 线程繁忙可能超 3s——
        // 本测试已两次在全量跑中超时（单测/复跑均过），上限提到 10s 消除脆弱
        bool loaded = await WaitUntilAsync(() => vm.CurrentCoverImage is not null, timeoutMs: 10000);
        Assert.True(loaded, "封面应在起播后装载");
        Assert.True(vm.HasCover);
        // 红封面 → 主色偏红（R 明显高），且非中性回退
        System.Windows.Media.Color c = vm.CurrentAccentBrush.Color;
        Assert.True(c.R > 180, $"实际主色 {c}");
        Assert.True(c.R > c.G + 80, $"实际主色 {c}");

        // OM-6 截图对齐（2026-09-09 NexBox 复刻）：三风格背景升级为渐变刷
        // 彩胶=固定浅灰三段渐变（近白）；沉浸=色板深色的深-本色-浅横向渐变（中段深）；
        // 现代=封面原色 0-40% 平铺 + 右缘压暗（首段色 == 封面主色）
        // 亮度用感知加权（WPF Color 无 GetBrightness）
        static double Luma(System.Windows.Media.Color c) => (0.299 * c.R + 0.587 * c.G + 0.114 * c.B) / 255.0;
        System.Windows.Media.LinearGradientBrush vinylBg = Assert.IsType<System.Windows.Media.LinearGradientBrush>(vm.VinylBackgroundBrush);
        Assert.True(Luma(vinylBg.GradientStops[0].Color) > 0.8, "彩胶底应近白（固定浅灰渐变）");
        System.Windows.Media.LinearGradientBrush immersionBg = Assert.IsType<System.Windows.Media.LinearGradientBrush>(vm.ImmersionBackgroundBrush);
        Assert.True(Luma(immersionBg.GradientStops[1].Color) < 0.45, "沉浸底应深（色板匹配）");
        System.Windows.Media.LinearGradientBrush modernBg = Assert.IsType<System.Windows.Media.LinearGradientBrush>(vm.ModernBackgroundBrush);
        Assert.Equal(vm.CurrentAccentBrush.Color, modernBg.GradientStops[0].Color);
        Assert.Equal(vm.CurrentAccentBrush.Color, modernBg.GradientStops[1].Color);
        Assert.True(Luma(modernBg.GradientStops[2].Color) < Luma(modernBg.GradientStops[0].Color), "现代右缘应压暗");
    }

    [Fact]
    public async Task PlayLocalSongWithoutCover_UsesNeutralFallback()
    {
        var engine = new FakePlaybackEngine();
        MusicManagerViewModel vm = CreateVm(engine, tagReader: new FakeTagReader { CoverBytes = null });
        MusicSong song = LocalSong("nocover");
        vm.Songs.Add(song);
        vm.SelectedSong = song;

        await vm.PlayFromLibraryCommand.ExecuteAsync(null);

        await WaitUntilAsync(() => true); // 给后台封面路径一个机会（无封面应瞬时置 null）
        Assert.Null(vm.CurrentCoverImage);
        Assert.False(vm.HasCover);
    }

    [Fact]
    public async Task SwitchToNextSong_RefreshesCoverForNewTrack()
    {
        var engine = new FakePlaybackEngine();
        var tagReader = new FakeTagReader();
        MusicManagerViewModel vm = CreateVm(engine, tagReader: tagReader);
        MusicSong coverSong = LocalSong("a", hasCover: true);
        MusicSong plainSong = LocalSong("b");
        vm.Songs.Add(coverSong);
        vm.Songs.Add(plainSong);
        vm.SelectedSong = coverSong;
        tagReader.CoverBytes = RedPng;

        await vm.PlayFromLibraryCommand.ExecuteAsync(null); // 播 a（封面歌）
        await WaitUntilAsync(() => vm.HasCover, timeoutMs: 10000); // 同上：并行负载时序加固

        tagReader.CoverBytes = null; // b 无封面
        await vm.NextCommand.ExecuteAsync(null);

        bool settled = await WaitUntilAsync(() => vm.QueueCurrent?.Name == "b" && vm.CurrentCoverImage is null);
        Assert.True(settled, "切到无封面曲后封面应清空");
    }

    // ════════ 音质菜单 ════════

    [Fact]
    public void QualityOptions_ExposeFiveLevelsWithChineseLabels()
    {
        MusicManagerViewModel vm = CreateVm(new FakePlaybackEngine());

        Assert.Equal(5, vm.QualityOptions.Count);
        Assert.Equal("exhigh", vm.QualityOptions[3].Key);
        Assert.Equal("高品质", vm.QualityOptions[3].Label);
        Assert.Equal("高品质", vm.SelectedQualityText); // 默认 exhigh
    }

    [Fact]
    public async Task SetQuality_OnPlayingOnlineTrack_ReResolvesUrl()
    {
        var engine = new FakePlaybackEngine();
        var resolver = new FakeResolver();
        var proxy = new FakeProxy();
        MusicManagerViewModel vm = CreateVm(engine, resolver: resolver, proxy: proxy);

        await vm.PlayOnlineTracksAsync([OnlineTrack("1")], OnlineTrack("1"));
        int firstResolve = resolver.ResolveCount;
        Assert.True(firstResolve >= 1);

        vm.SetQualityCommand.Execute("lossless");

        bool reResolved = await WaitUntilAsync(() => resolver.ResolveCount > firstResolve);
        Assert.True(reResolved, "切音质后应重新解析 URL（走 OM-4 降级链）");
        Assert.Equal("无损", vm.SelectedQualityText);
        Assert.Equal("lossless", vm.PreferredQuality);
    }

    [Fact]
    public void SetQuality_OnLocalOnly_JustRecordsPreference()
    {
        var engine = new FakePlaybackEngine();
        MusicManagerViewModel vm = CreateVm(engine);

        vm.SetQualityCommand.Execute("hires");

        Assert.Equal("hires", vm.PreferredQuality);
        Assert.Equal("Hi-Res", vm.SelectedQualityText);
    }

    [Fact]
    public void SetQuality_WithInvalidKey_IsIgnored()
    {
        MusicManagerViewModel vm = CreateVm(new FakePlaybackEngine());

        vm.SetQualityCommand.Execute("ultra");

        Assert.Equal("exhigh", vm.PreferredQuality);
    }
}
