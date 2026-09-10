namespace SystemToolkit.Core.Network.Models;

/// <summary>
/// 优化改前快照（每次应用前写入，仅保留最近一份；落
/// <c>%LOCALAPPDATA%/SystemToolkit/net/tuning_snapshot.json</c>——02 §六统一配置根，
/// 旧位置 <c>%APPDATA%/FileBackupTool/net/</c> 只作一次性迁移来源；snake_case JSON 沿用项目约定）。
/// <para>
/// <see cref="NetshValues"/> 只记录<b>能可靠读到的项</b>（键：autotuninglevel / rss / ecncapability）；
/// 解析失败的项不进快照——还原时跳过，绝不拿「未知」去写系统。
/// </para>
/// </summary>
public sealed record TuningSnapshot(
    DateTime CapturedAt,
    Dictionary<string, string> NetshValues,
    uint? NetworkThrottlingIndex,

    // 【M6c P1-6】接口跃点数快照（可选项：旧格式快照文件缺此键 → null → 还原时跳过，
    // 与「缺项跳过」纪律一致；需补旧格式契约测试——Overview 缓存契约的教训）
    Dictionary<string, int>? InterfaceMetrics = null);
