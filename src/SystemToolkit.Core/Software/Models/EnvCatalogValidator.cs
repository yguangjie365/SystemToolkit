using System.Text.RegularExpressions;

namespace SystemToolkit.Core.Software.Models;

/// <summary>
/// EnvCatalog 及其条目的字段校验（S5/S6，REVIEW-2026-08-30）。
/// <para>
/// 装机清单是可导入导出的外部 JSON（ImportCatalog），字段内容会流入两个高危出口：
/// ① <c>WingetService.BuildIdArgs</c>——id/source 直接成为 winget 命令行参数；
/// ② ManualSoftware 的 DownloadUrl/LocalInstallerPath——「去下载/去安装」会交给
/// shell 执行。因此导入路径必须做与数据来源分离的字段校验，白名单正则在此
/// 单一维护，WingetService 的参数构造引用同一份，避免两处规则漂移。
/// </para>
/// </summary>
public static class EnvCatalogValidator
{
    /// <summary>
    /// 包 Id / 源名称字符白名单：字母数字与 . _ - 组合、首字符必须为字母数字。
    /// msstore 的 9 位 id、winget 包名（如 Microsoft.VisualStudioCode）、源名
    /// （msstore / winget）均能通过；以 - 开头（可能被解析为选项）或含空格/引号
    /// 的值一律拒绝。\z 锚定防尾换行通过（审查 M7）。
    /// </summary>
    public static readonly Regex PackageIdPattern =
        new("^[A-Za-z0-9][A-Za-z0-9._-]*\\z", RegexOptions.CultureInvariant);

    /// <summary>DownloadUrl 仅允许 http(s)，防 file: / 自定义协议被浏览器执行（\z 防尾换行，审查 M7）。</summary>
    private static readonly Regex HttpUrlPattern =
        new("^https?://\\S+\\z", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    /// <summary>供编辑对话框复用（审查 2026-09-04 P2：编辑路径此前零校验）。</summary>
    public static bool IsValidHttpUrl(string url) =>
        !string.IsNullOrWhiteSpace(url) && HttpUrlPattern.IsMatch(url);

    // 字段长度上限：防超大字段撑爆内存/UI（正常数据远低于此）
    private const int MaxIdLength = 128;
    private const int MaxShortTextLength = 256;
    private const int MaxDescriptionLength = 2000;
    private const int MaxPathLength = 1024;

    /// <summary>
    /// 校验整个 catalog。返回错误列表（空 = 通过）；调用方决定整体拒绝还是剔除。
    /// 全程 null 容忍：JSON 显式 null 会把非空默认值的 string 属性写成 null，
    /// 列表里也可能出现 null 项——校验器必须先于业务代码拦住这些形态。
    /// </summary>
    public static List<string> Validate(EnvCatalog catalog)
    {
        var errors = new List<string>();
        foreach (WingetPackage p in catalog.Winget)
        {
            if (p is null)
            {
                errors.Add("[商店应用] 存在 null 条目");
                continue;
            }
            if (string.IsNullOrWhiteSpace(p.Id))
                errors.Add($"[商店应用] 「{Safe(p.Name)}」缺少 Id");
            else if (p.Id.Length > MaxIdLength)
                errors.Add($"[商店应用] Id 超长（>{MaxIdLength}）：{Truncate(p.Id)}");
            else if (!PackageIdPattern.IsMatch(p.Id))
                errors.Add($"[商店应用] Id 含非法字符或以 - 开头：{Truncate(p.Id)}");

            if (!string.IsNullOrWhiteSpace(p.Source) && !PackageIdPattern.IsMatch(p.Source))
                errors.Add($"[商店应用] 「{Safe(p.Name)}」Source 含非法字符：{Truncate(p.Source)}");

            if (string.IsNullOrWhiteSpace(p.Name))
                errors.Add($"[商店应用] 条目 {Truncate(p.Id)} 缺少名称");

            if ((p.Description?.Length ?? 0) > MaxDescriptionLength)
                errors.Add($"[商店应用] 「{Safe(p.Name)}」描述超长（>{MaxDescriptionLength}）");
            if ((p.Category?.Length ?? 0) > MaxShortTextLength || (p.Icon?.Length ?? 0) > MaxShortTextLength)
                errors.Add($"[商店应用] 「{Safe(p.Name)}」分类或图标字段超长（>{MaxShortTextLength}）");
        }

        foreach (ManualSoftware m in catalog.Manual.Concat(catalog.Driver))
        {
            if (m is null)
            {
                errors.Add("[第三方/驱动] 存在 null 条目");
                continue;
            }
            if (string.IsNullOrWhiteSpace(m.Name))
                errors.Add("[第三方/驱动] 存在缺少名称的条目");

            if (!string.IsNullOrWhiteSpace(m.DownloadUrl) && !HttpUrlPattern.IsMatch(m.DownloadUrl))
                errors.Add($"[第三方/驱动] 「{Safe(m.Name)}」下载链接必须是 http(s) 地址：{Truncate(m.DownloadUrl)}");

            if (HasControlChars(m.LocalInstallerPath) || (m.LocalInstallerPath?.Length ?? 0) > MaxPathLength)
                errors.Add($"[第三方/驱动] 「{Safe(m.Name)}」本地安装包路径含控制字符或超长（>{MaxPathLength}）");

            if ((m.Description?.Length ?? 0) > MaxDescriptionLength)
                errors.Add($"[第三方/驱动] 「{Safe(m.Name)}」描述超长（>{MaxDescriptionLength}）");
            if ((m.Category?.Length ?? 0) > MaxShortTextLength || (m.Icon?.Length ?? 0) > MaxShortTextLength)
                errors.Add($"[第三方/驱动] 「{Safe(m.Name)}」分类或图标字段超长（>{MaxShortTextLength}）");
        }

        return errors;
    }

    private static string Safe(string? s) => Truncate(s);

    private static string Truncate(string? s)
    {
        if (string.IsNullOrEmpty(s))
        {
            return "<空>";
        }
        string clean = new string(s.Take(40).Select(c => char.IsControl(c) ? '?' : c).ToArray());
        return s.Length > 40 ? clean + "…" : clean;
    }

    private static bool HasControlChars(string? s)
        => !string.IsNullOrEmpty(s) && s.Any(char.IsControl);
}
