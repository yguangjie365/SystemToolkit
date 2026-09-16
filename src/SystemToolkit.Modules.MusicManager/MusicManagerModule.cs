using System.IO;
using Microsoft.Extensions.DependencyInjection;
using SystemToolkit.Abstractions;
using SystemToolkit.Core.Contracts;
using SystemToolkit.Core.Music.Online;
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

    public override int Order => 8;

    public override bool CanDisable => true;

    /// <inheritdoc/>
    public override object CreateView(IServiceProvider services) =>
        services.GetRequiredService<MusicManagerView>();

    public override void RegisterServices(IServiceCollection services)
    {
        var moduleLogger = new FileLogger("musicmanager");
        services.AddKeyedSingleton<ILogger>("musicmanager", moduleLogger);

        // 02 §六：旧 Roaming 位置一次性迁移（曲库 + 搜索历史），保留老用户数据
        // Q-021（2026-09-14 用户裁定）：迁移类"失败不阻断"的 catch 必须留日志 → 迁移需要日志出口
        MigrateLegacyDataFile("music-library.json", moduleLogger);
        MigrateLegacyDataFile("music-search-history.json", moduleLogger);

        // 标签读取器（MUSIC-2，构造要非 keyed ILogger——工厂注入本模块 keyed 实例）
        services.AddSingleton<IMusicTagReader>(sp => new TagLibMusicTagReader(
            sp.GetRequiredKeyedService<ILogger>("musicmanager")));
        services.AddSingleton(sp => new LocalMusicScanner(
            sp.GetRequiredKeyedService<ILogger>("musicmanager"),
            sp.GetRequiredService<IMusicTagReader>()));

        // 曲库存储（MUSIC-5，Q-008：模块私有 JSON；02 §六统一配置根 %LOCALAPPDATA%\SystemToolkit\）
        services.AddSingleton(sp => new JsonMusicLibraryStore(
            LocalDataPath("music-library.json"),
            sp.GetRequiredKeyedService<ILogger>("musicmanager")));
        services.AddSingleton<IMusicLibraryStore>(sp => sp.GetRequiredService<JsonMusicLibraryStore>());

        // 搜索历史（P3a：模块私有 JSON，同目录）
        services.AddSingleton(sp => new JsonSearchHistoryStore(
            LocalDataPath("music-search-history.json"),
            sp.GetRequiredKeyedService<ILogger>("musicmanager")));
        services.AddSingleton<ISearchHistoryStore>(sp => sp.GetRequiredService<JsonSearchHistoryStore>());

        // 播放队列（MUSIC-5）
        services.AddSingleton<IPlaybackQueueService, PlaybackQueueService>();

        // 在线流实现（OM-0~OM-3：平台客户端/Cookie 存储/音频代理/URL 解析器）位于
        // Infrastructure——🔴 模块禁引 Infrastructure（依赖守卫红线），由 Shell
        // RegisterSharedInfrastructure 注册；本模块 VM 经 GetService 可选解析（缺席降级）

        services.AddSingleton(sp => new MusicManagerViewModel(
            sp.GetRequiredService<IMusicLibraryStore>(),
            sp.GetRequiredService<LocalMusicScanner>(),
            sp.GetRequiredService<IPlaybackQueueService>(),
            sp.GetRequiredKeyedService<ILogger>("musicmanager"),
            engineProvider: () => sp.GetService<IMusicPlaybackEngine>(), // 引擎可选依赖（DI 未注册时为 null，模块降级可用）
            tagReader: sp.GetRequiredService<IMusicTagReader>(),
            dispatcher: System.Windows.Application.Current?.Dispatcher,
            urlResolver: sp.GetService<IOnlineUrlResolver>(),
            audioProxy: sp.GetService<IAudioProxyService>(),
            catalog: sp.GetService<IOnlineMusicCatalogService>(),
            credentials: sp.GetService<IOnlineCredentialStore>(),
            searchHistory: sp.GetService<ISearchHistoryStore>(),
            navigationModule: this));
        // 🔴 视图必须 Transient：宿主在主题切换后经 CreateView 重建当前页，
        //    以重新解析 {StaticResource} 派生样式（Style.BasedOn 不支持 DynamicResource）。
        //    View 注册为单例时 CreateView 恒返回同一实例 → PageHost.Content 赋同一对象是 WPF 空操作
        //    → 视图停留在旧主题包的颜色上（2026-09-15 实机“浅色下白字压白底”事故）。
        //    视图是无状态壳（DataContext 由 VM 提供），重建只重置 UI 局部状态。
        services.AddTransient<MusicManagerView>();

        // MUSIC-7：Shell 底部迷你播放条数据源——同一 VM 单例的桥接注册。
        // 模块禁用（V1-008 落地后）时本注册随 RegisterServices 一并不存在，
        // Shell GetService 得 null → 播放条整体隐藏。
        services.AddSingleton<IPlaybackBarSource>(sp => sp.GetRequiredService<MusicManagerViewModel>());
    }

    /// <summary>模块私有 JSON 的落盘路径（02 §六统一配置根 <c>%LOCALAPPDATA%\SystemToolkit\</c>）。</summary>
    private static string LocalDataPath(string fileName) => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "SystemToolkit", fileName);

    /// <summary>
    /// 一次性迁移（2026-09-11 口径统一）：旧位置在 Roaming <c>%AppData%\SystemToolkit\</c>，
    /// 新位置在 LOCALAPPDATA 的同结构下。新位置缺失且旧位置存在才复制
    /// （范式对齐 <c>RuleManager.MigrateLegacyRulesFile</c>）；旧文件保留不删。
    /// </summary>
    private static void MigrateLegacyDataFile(string fileName, ILogger logger)
    {
        try
        {
            string target = LocalDataPath(fileName);
            string legacy = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "SystemToolkit", fileName);
            if (File.Exists(target) || !File.Exists(legacy))
            {
                return;
            }

            string? dir = Path.GetDirectoryName(target);
            if (!string.IsNullOrEmpty(dir))
            {
                Directory.CreateDirectory(dir);
            }

            File.Copy(legacy, target);
        }
        catch (Exception ex)
        {
            // Q-021（2026-09-14 用户裁定）：迁移失败仍不阻断模块注册（退化为「新库 / 无搜索历史」，
            // 旧数据仍在原处），但**必须留一条日志** —— 空 catch 时"用户看不到曲库"无从查起（原为空 catch）。
            logger.Warn($"音乐数据迁移失败（{fileName}：退化为新库/无搜索历史，旧数据仍在 Roaming 原处）：{ex.Message}");
        }
    }
}
