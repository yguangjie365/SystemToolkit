using SystemToolkit.Core.Contracts;
using SystemToolkit.Core.Music.Models;
using SystemToolkit.Core.Music.Online;
using SystemToolkit.Core.Music.Services;
using SystemToolkit.Modules.MusicManager;
using SystemToolkit.Tests.Music;

namespace SystemToolkit.Tests.Music.Online;

/// <summary>
/// OM-4 播放管线（VM 级）：混合队列、竞态序号、不可播自动跳过、试听透传。
/// 全部用 FakePlaybackEngine + 假解析器/假代理，无网络、无真实音频。
/// </summary>
public class MusicOnlinePipelineTests
{
    private sealed class FakeResolver : IOnlineUrlResolver
    {
        /// <summary>track.Id → 结果；未命中键默认不可播（VIP 语义）。</summary>
        public Dictionary<string, OnlineSongUrlResult> Results { get; } = [];

        /// <summary>进阶控制：按调用序出队；null = 挂起（模拟慢网络），TCS 记入 <see cref="InFlight"/> 供测试补完。</summary>
        public Queue<TaskCompletionSource<OnlineSongUrlResult>?> Pending { get; } = new();

        /// <summary>被挂起的解析（测试补完其结果以模拟慢响应返回）。</summary>
        public List<TaskCompletionSource<OnlineSongUrlResult>> InFlight { get; } = [];

        public List<string> ResolvedIds { get; } = [];

        public Task<OnlineSongUrlResult> ResolveAsync(OnlineTrack track, string preferredQuality, CancellationToken ct = default)
        {
            ResolvedIds.Add(track.Id);
            if (Pending.Count > 0)
            {
                TaskCompletionSource<OnlineSongUrlResult>? tcs = Pending.Dequeue();
                if (tcs is null)
                {
                    var hung = new TaskCompletionSource<OnlineSongUrlResult>(TaskCreationOptions.RunContinuationsAsynchronously);
                    InFlight.Add(hung);
                    return hung.Task;
                }

                tcs.TrySetResult(GetResult(track));
                return tcs.Task;
            }

            return Task.FromResult(GetResult(track));
        }

        private OnlineSongUrlResult GetResult(OnlineTrack track) =>
            Results.GetValueOrDefault(track.Id, VipResult);

        private static OnlineSongUrlResult VipResult => new()
        {
            Playable = false,
            Reason = "url_unavailable",
            Fee = 1,
            Message = "该歌曲可能需要 VIP 或登录",
        };
    }

    private sealed class FakeProxy : IAudioProxyService
    {
        public bool IsRunning => true;
        public int Port => 41234;

        public Task<int> StartAsync(CancellationToken ct = default) => Task.FromResult(Port);

        public Task StopAsync() => Task.CompletedTask;

        public Task<string> GetProxiedAudioUrlAsync(string rawUrl, CancellationToken ct = default) =>
            Task.FromResult($"proxy::{rawUrl}");

        public Task<string> GetProxiedCoverUrlAsync(string rawUrl, CancellationToken ct = default) =>
            Task.FromResult($"coverproxy::{rawUrl}");
    }

    private static OnlineTrack Track(string id, OnlineProvider provider = OnlineProvider.NetEase) => new()
    {
        Provider = provider,
        Id = id,
        Name = $"曲{id}",
        Artist = "测试",
        Playable = true,
    };

    private static OnlineSongUrlResult PlayableUrl(string url) => new()
    {
        Playable = true,
        Url = url,
        Level = "exhigh",
    };

    private static MusicManagerViewModel CreateVm(
        FakePlaybackEngine engine,
        IOnlineUrlResolver resolver,
        out List<string> playedUrls)
    {
        string dir = Path.Combine(Path.GetTempPath(), $"music-om4-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        var vm = new MusicManagerViewModel(
            store: new JsonMusicLibraryStore(Path.Combine(dir, "music-library.json")),
            scanner: new LocalMusicScanner(new NoopLogger(), new TagLibMusicTagReader(new NoopLogger())),
            queue: new PlaybackQueueService(),
            log: new NoopLogger(),
            engineProvider: () => engine,
            dispatcher: null,
            urlResolver: resolver,
            audioProxy: new FakeProxy());
        playedUrls = engine.PlayedSources;
        return vm;
    }

    [Fact]
    public async Task PlayOnlineTracksAsync_FirstUnplayableSecondPlayable_AutoAdvancesToSecond()
    {
        var engine = new FakePlaybackEngine();
        var resolver = new FakeResolver();
        resolver.Results["a"] = new OnlineSongUrlResult { Playable = false, Reason = "url_unavailable", Fee = 1 };
        resolver.Results["b"] = PlayableUrl("https://cdn/b.flac");
        MusicManagerViewModel vm = CreateVm(engine, resolver, out List<string> played);

        await vm.PlayOnlineTracksAsync([Track("a"), Track("b")], Track("a"));

        // 第一首 VIP 不可播 → 原因分类可见 + 自动跳到第二首并真正起播
        Assert.Contains("VIP", vm.ScanStatusText, StringComparison.Ordinal);
        Assert.Single(played);
        Assert.Equal("proxy::https://cdn/b.flac", played[0]);
        Assert.Equal("NetEase:b", vm.QueueCurrent?.Id);
    }

    [Fact]
    public async Task PlayOnlineTracksAsync_TenConsecutiveFailures_StopsWithVisibleSummary()
    {
        var engine = new FakePlaybackEngine();
        var resolver = new FakeResolver(); // 默认全部不可播
        MusicManagerViewModel vm = CreateVm(engine, resolver, out _);

        var tracks = Enumerable.Range(1, OnlineSkipPolicy.MaxConsecutiveSkips + 3)
            .Select(i => Track($"x{i}")).ToList();
        await vm.PlayOnlineTracksAsync(tracks, tracks[0]);

        // 达上限即停：不再继续解析后续曲目（首曲 + 连续 10 次失败 = 11 次解析）
        Assert.Equal(OnlineSkipPolicy.MaxConsecutiveSkips, resolver.ResolvedIds.Count);
        Assert.Empty(engine.PlayedSources);
        Assert.Contains("停止", vm.ScanStatusText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SuccessPlaying_ResetsConsecutiveFailureCounter()
    {
        var engine = new FakePlaybackEngine();
        var resolver = new FakeResolver();
        resolver.Results["ok"] = PlayableUrl("https://cdn/ok.mp3");
        MusicManagerViewModel vm = CreateVm(engine, resolver, out _);

        // 前 9 首失败，第 10 首起播成功
        var tracks = Enumerable.Range(1, 9).Select(i => Track($"f{i}")).ToList();
        tracks.Add(Track("ok"));
        await vm.PlayOnlineTracksAsync(tracks, tracks[0]);

        Assert.Single(engine.PlayedSources);
        Assert.True(vm.IsPlaying);

        // 起播成功已归零计数：之后再造失败仍会自动跳（未达上限，不出现「已停止」）
        resolver.Results["after1"] = new OnlineSongUrlResult { Playable = false, Reason = "error", Message = "net down" };
        resolver.Results["after2"] = PlayableUrl("https://cdn/after2.mp3");
        await vm.PlayOnlineTracksAsync([Track("after1"), Track("after2")], Track("after1"));

        Assert.Equal(2, engine.PlayedSources.Count);
        Assert.Equal("proxy::https://cdn/after2.mp3", engine.PlayedSources[1]);
    }

    [Fact]
    public async Task PlayOnlineTracksAsync_TrialResult_PlaysWithVisibleTrialNote()
    {
        var engine = new FakePlaybackEngine();
        var resolver = new FakeResolver();
        resolver.Results["t"] = new OnlineSongUrlResult
        {
            Playable = true,
            Trial = true,
            Url = "https://cdn/trial.mp3",
            Level = "standard",
            Message = "仅试听片段",
        };
        MusicManagerViewModel vm = CreateVm(engine, resolver, out List<string> played);

        await vm.PlayOnlineTracksAsync([Track("t")], Track("t"));

        // trial/reason 语义透传 UI（🔴 不静默）：起播但明确告知是试听
        Assert.Single(played);
        Assert.Contains("试听", vm.ScanStatusText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task StaleResolution_AbandonedByPlaySongSeq_NeverOverwritesNewTrack()
    {
        var engine = new FakePlaybackEngine();
        var resolver = new FakeResolver();
        // 第一首的解析挂起（慢网络）；随后用户切换到第二首
        resolver.Pending.Enqueue(null);
        resolver.Results["slow"] = PlayableUrl("https://cdn/slow.mp3");
        resolver.Results["fast"] = PlayableUrl("https://cdn/fast.mp3");
        MusicManagerViewModel vm = CreateVm(engine, resolver, out List<string> played);

        Task first = vm.PlayOnlineTracksAsync([Track("slow")], Track("slow"));
        Task second = vm.PlayOnlineTracksAsync([Track("fast")], Track("fast"));
        await second;

        Assert.Single(played); // 第二首已起播
        Assert.Equal("proxy::https://cdn/fast.mp3", played[0]);

        // 慢解析此刻才返回：seq 已过期，必须被放弃
        Assert.Single(resolver.InFlight);
        resolver.InFlight[0].TrySetResult(PlayableUrl("https://cdn/slow.mp3"));
        await first;

        Assert.Single(played); // 仍是 1 次——slow 未被灌进引擎
        Assert.Equal("NetEase:fast", vm.QueueCurrent?.Id);
    }

    [Fact]
    public async Task PlayOnlineTracksAsync_WithoutResolver_ShowsExplicitStatus()
    {
        var engine = new FakePlaybackEngine();
        string dir = Path.Combine(Path.GetTempPath(), $"music-om4n-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        var vm = new MusicManagerViewModel(
            store: new JsonMusicLibraryStore(Path.Combine(dir, "music-library.json")),
            scanner: new LocalMusicScanner(new NoopLogger(), new TagLibMusicTagReader(new NoopLogger())),
            queue: new PlaybackQueueService(),
            log: new NoopLogger(),
            engineProvider: () => engine);

        await vm.PlayOnlineTracksAsync([Track("a")], Track("a"));

        // 🔴 在线组件缺席不得静默：显式提示（本地播放不受影响——故障隔离）
        Assert.Contains("未就绪", vm.ScanStatusText, StringComparison.Ordinal);
        Assert.Empty(engine.PlayedSources);
    }
}
