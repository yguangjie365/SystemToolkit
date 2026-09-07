using System.Text.RegularExpressions;

namespace SystemToolkit.Core.Drivers;

/// <summary>
/// pnputil 令牌合法性规则——提权通道双端（ElevatedPnpUtilClient 与 ElevatedHelper）共用的白名单。
/// 🔴 背景（05 安全设计）：这些值最终进入提权命令行，必须白名单校验，禁止"调用方凑巧给对"。
/// </summary>
public static class PnpUtilTokenRules
{
    /// <summary>INF 文件名（不含路径）：字母/数字/下划线/点/连字符 + .inf 后缀（大小写不敏感，\z 防尾换行）。
    /// 覆盖 oemXX.inf 与收件箱 inf 名。</summary>
    private static readonly Regex InfFileNameRegex =
        new(@"^[A-Za-z0-9_.\-]+\.inf\z", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>可删除的发布名：仅 oem 编号的第三方包（收件箱 pnputil 本就拒绝，前端直接拦）。
    /// \z 锚定防尾换行通过（与 InfFileNameRegex 同纪律，审查 M7）。</summary>
    private static readonly Regex DeletablePublishedNameRegex =
        new(@"^[Oo][Ee][Mm]\d{1,5}\.inf\z", RegexOptions.Compiled);

    /// <summary>INF 文件名合法性（/export-driver 目标 token 用；禁止路径分隔符与任何注入字符）。</summary>
    public static bool IsValidInfFileName(string? token) =>
        token is not null
        && InfFileNameRegex.IsMatch(token)
        && token.IndexOfAny(Path.GetInvalidFileNameChars()) < 0
        && !token.Contains('/');

    /// <summary>删除目标白名单：仅第三方 oem 包发布名。</summary>
    public static bool IsDeletablePublishedName(string? token) =>
        token is not null && DeletablePublishedNameRegex.IsMatch(token);

    /// <summary>添加源 INF 路径：根路径 + .inf 后缀 + 文件存在。</summary>
    public static bool IsValidInfPathForAdd(string? path) =>
        !string.IsNullOrWhiteSpace(path)
        && Path.IsPathRooted(path)
        && path.EndsWith(".inf", StringComparison.OrdinalIgnoreCase)
        && File.Exists(path);
}
