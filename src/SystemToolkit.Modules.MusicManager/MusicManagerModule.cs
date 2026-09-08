using System.IO;
using Microsoft.Extensions.DependencyInjection;
using SystemToolkit.Abstractions;
using SystemToolkit.Core.Contracts;
using SystemToolkit.Core.Music.Services;

namespace SystemToolkit.Modules.MusicManager;

/// <summary>
/// 音乐管理 模块（扩展模块，可在设置中禁用）。MUSIC-6：三 Tab UI + 服务注册。
/// </summary>
/// <remarks>
/// <b>注册说明</b>：<see cref="IMusicPlaybackEngine"/> 的实现由 MUSIC-4（并行）提供，
/// 合入后由其提交方补注册——本模块所有服务在引擎缺席时同样可用
/// （VM 用 <c>GetService</c> 延迟解析，null 时播放控制禁用，模块故障隔离）。
/// </remarks>
public sealed class MusicManagerModule : ModuleBase
{
    public override string Id => "musicmanager";

    public override string DisplayName => "音乐管理";

    public override int Order => 9;

    public override bool CanDisable => true;

    /// <inheritdoc/>
    public override object CreateView(IServiceProvider services) =>
        services.GetRequiredService<MusicManagerView>();

    public override void RegisterServices(IServiceCollection services)
    {
        services.AddKeyedSingleton<ILogger>("musicmanager", new FileLogger("musicmanager"));

        // 标签读取器（MUSIC-2，构造要非 keyed ILogger——工厂注入本模块 keyed 实例）
        services.AddSingleton<IMusicTagReader>(sp => new TagLibMusicTagReader(
            sp.GetRequiredKeyedService<ILogger>("musicmanager")));
        services.AddSingleton(sp => new LocalMusicScanner(
            sp.GetRequiredKeyedService<ILogger>("musicmanager"),
            sp.GetRequiredService<IMusicTagReader>()));

        // 曲库存储（MUSIC-5，Q-008：模块私有 JSON 落 %AppData%/SystemToolkit）
        services.AddSingleton(sp => new JsonMusicLibraryStore(
            Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "SystemToolkit",
                "music-library.json"),
            sp.GetRequiredKeyedService<ILogger>("musicmanager")));
        services.AddSingleton<IMusicLibraryStore>(sp => sp.GetRequiredService<JsonMusicLibraryStore>());

        // 播放队列（MUSIC-5）
        services.AddSingleton<IPlaybackQueueService, PlaybackQueueService>();

        services.AddSingleton(sp => new MusicManagerViewModel(
            sp,
            sp.GetRequiredService<IMusicLibraryStore>(),
            sp.GetRequiredService<LocalMusicScanner>(),
            sp.GetRequiredService<IPlaybackQueueService>(),
            sp.GetRequiredKeyedService<ILogger>("musicmanager")));
        services.AddSingleton<MusicManagerView>();
    }
}
