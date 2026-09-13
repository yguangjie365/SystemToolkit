using System.Globalization;

namespace SystemToolkit.Core.Backup.Models;

/// <summary>
/// 「未变文件复用」判据（B5b-③）。
/// </summary>
/// <remarks>
/// <para>
/// 🔴 这是**唯一决定"要不要跳过复制"的判据**，因此刻意做成**纯函数**（不碰磁盘、时间由调用方传入）：
/// 判据写错 = 快照里留下旧内容的副本 = **静默漏备份**，是本项目最不可接受的失败模式，
/// 必须能逐条离线断言、也能做反向验证。
/// </para>
/// <para>
/// 🔴 **任何不确定的情形一律判为"已变"**——宁可多复制一次，绝不少复制一次。
/// </para>
/// <para>
/// ⚠️ 已知且**已裁定接受**的风险：本判据只看「字节数 + 修改时间」，**不看内容**。
/// 因此"内容变了、但把修改时间改回原值（且大小恰好相同）"会被判为未变。
/// 这正是本功能**默认关闭**的原因；`BackupUnchangedReuseTests` 里有一条用例专门钉住它，
/// 将来若有人想默认开启，必须正面推翻那条用例。
/// </para>
/// </remarks>
internal static class UnchangedFileReuse
{
    /// <summary>
    /// 判断上一份快照中的同名条目能否直接代表当前源文件（即"源文件未变"）。
    /// </summary>
    /// <param name="previous">上一份快照清单中同相对路径的条目；没有则传 null。</param>
    /// <param name="currentSourcePath">当前源文件的绝对路径（防止撞名时张冠李戴）。</param>
    /// <param name="currentSize">当前源文件字节数。</param>
    /// <param name="currentMtime">当前源文件最后修改时间。</param>
    /// <returns>可复用返回 true；**任何一条不满足、或任何解析失败都返回 false**。</returns>
    internal static bool IsReusable(
        FileEntry? previous,
        string currentSourcePath,
        long currentSize,
        DateTime currentMtime)
    {
        if (previous is null)
        {
            // 上一份快照里没有这个文件（新增/改名）→ 只能是"已变"
            return false;
        }

        if (string.IsNullOrEmpty(previous.Sha256))
        {
            // 旧版快照未记录哈希：复用会让本次清单失去校验依据（恢复后也无法校验）→ 重新复制
            return false;
        }

        if (previous.Size != currentSize)
        {
            return false;
        }

        if (!string.Equals(
                TrimPath(previous.SourcePath),
                TrimPath(currentSourcePath),
                StringComparison.OrdinalIgnoreCase))
        {
            // 多源规则下相对路径本已带源前缀（DirectoryScanner 保证唯一），这里再比一次源路径：
            // 万一撞上，宁可重新复制也不张冠李戴。
            return false;
        }

        if (!TryParseMtime(previous.Mtime, out DateTime previousMtime))
        {
            // 时间不可解析（旧格式/损坏）→ 无法比对 → 不冒险
            return false;
        }

        return previousMtime.Ticks == currentMtime.Ticks;
    }

    /// <summary>
    /// 解析清单里的 ISO 8601 时间（round-trip 格式）；不可解析返回 false。
    /// </summary>
    /// <param name="text">清单中记录的 mtime 文本。</param>
    /// <param name="value">解析结果（失败时为 default）。</param>
    internal static bool TryParseMtime(string? text, out DateTime value)
        => DateTime.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out value);

    /// <summary>
    /// 路径比较用的规范化：统一分隔符、去尾分隔符。
    /// **仅用于比较**，不改写任何实际路径（不做 GetFullPath，避免把相对路径"洗"成意外结果）。
    /// </summary>
    private static string TrimPath(string? path)
        => string.IsNullOrEmpty(path) ? "" : path.Replace('/', '\\').TrimEnd('\\');
}
