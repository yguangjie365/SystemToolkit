using System.Text;
using System.Text.Json;

namespace SystemToolkit.Core.Software.Services;

public sealed partial class WingetService
{
    /// <summary>搜索 winget 源（winget search，文本表格模式），返回原始输出供 ParseSearchResults 解析。</summary>
    public async Task<string> SearchAsync(string query, CancellationToken ct = default(CancellationToken))
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            return "";
        }
        var stdout = new StringBuilder();
        // 注意：不能带 --output json——本机 winget（v1.29.290 / v1.30.100-preview 实测）不识别
        // 该选项，会打印用法帮助并退出；帮助文本流入解析器会把“Windows 程序包管理器(预览)
        // v1.30.100-preview”横幅误判成候选包（2026-08-29 用户反馈）。解析走 ParseSearchResults 的表格文本模式。
        // 显式限定 --source winget：搜索框定位为「winget 源查找与安装」，
        // 不混入 msstore 结果（商店应用由装机清单卡片走 msstore 源）。
        int exitCode = await RunProcessAsync(_wingetPath, new[] { "search", "--query", query, "--source", "winget", "--accept-source-agreements", "--disable-interactivity" }, delegate (string line)
        {
            stdout.AppendLine(line);
            _output(line);
        }, delegate (string line)
        {
            _output(line);
        }, ct, DefaultReadTimeout);

        // 查询失败必须让调用方感知，否则空结果会被显示成"找到 0 个候选包"
        if (exitCode != 0 && stdout.Length == 0)
            throw new IOException($"winget search 执行失败（退出码 {exitCode}）");

        // 参数不被识别时 winget 打印帮助文本（退出码可能仍为 0），必须在此拦截报错，
        // 不能让帮助内容流入结果解析。中英文提示语都检查，防 locale 差异漏网。
        string outText = stdout.ToString();
        if (outText.Contains("无法识别参数名称") || outText.Contains("使用情况:")
            || outText.Contains("unrecognized", StringComparison.OrdinalIgnoreCase)
            || outText.Contains("usage:", StringComparison.OrdinalIgnoreCase))
            throw new IOException("winget search 参数不被当前 winget 版本识别（输出为用法帮助），已中止解析");

        return outText;
    }

    /// <summary>解析 winget search 输出为候选包列表（支持 JSON 前瞻分支与表格文本模式）。</summary>
    public static List<WingetSearchResult> ParseSearchResults(string text)
    {
        var list = new List<WingetSearchResult>();
        if (string.IsNullOrWhiteSpace(text))
        {
            return list;
        }
        // JSON 模式分支（前瞻保留）：若未来 winget 恢复 search 的 JSON 输出则直接命中；
        // 当前版本（v1.29~v1.30 实测）不识别 --output json，实际走下方表格文本模式。
        try
        {
            using var doc = JsonDocument.Parse(text);
            if (doc.RootElement.ValueKind == JsonValueKind.Object && doc.RootElement.TryGetProperty("Matches", out JsonElement matches) && matches.ValueKind == JsonValueKind.Array)
            {
                foreach (JsonElement match in matches.EnumerateArray())
                {
                    if (!match.TryGetProperty("Package", out JsonElement pkg))
                    {
                        continue;
                    }
                    // 注意：winget 恢复 JSON 输出时 Id/Name/Version 可能是字符串，也可能版本是纯数字（如 1.29 被 JSON 化成 Number）
                    // 这里做 ValueKind 分支避免 ThrowIfWrongType（今日 NetEaseCrypto 同款回归）。
                    static string? PickStringOrRaw(JsonElement el)
                        => el.ValueKind == JsonValueKind.String ? el.GetString()
                         : el.ValueKind == JsonValueKind.Number ? el.GetRawText()
                         : null;
                    string? id = pkg.TryGetProperty("Id", out JsonElement idEl) ? PickStringOrRaw(idEl) : null;
                    string? name = pkg.TryGetProperty("Name", out JsonElement nameEl) ? PickStringOrRaw(nameEl) : null;
                    string? version = pkg.TryGetProperty("Version", out JsonElement verEl) ? PickStringOrRaw(verEl) : null;
                    if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(name))
                    {
                        continue;
                    }
                    list.Add(new WingetSearchResult(id, name, version ?? ""));
                }
                return list;
            }
        }
        catch (JsonException)
        {
            // 非 JSON 输出（旧版 winget / 错误信息），回退文本解析
        }
        // 文本模式：winget 表格输出。首选按表头列位切分（中英文表头均可）：
        // 名称列可能含点号（如 ".NET SDK"），"首个含点号 token 即 Id"的启发式会误切，
        // 因此先定位表头 ID 列的起始字符位，再按位置切分每行。
        string[] array = text.Split('\n');
        int idCol = FindIdColumnIndex(array);
        if (idCol >= 0)
        {
            foreach (string line in array)
            {
                if (line.Length <= idCol || string.IsNullOrWhiteSpace(line) || IsTableSeparator(line))
                {
                    continue;
                }
                string name = line.Substring(0, idCol).Trim();
                string[] tokens = line.Substring(idCol).TrimStart().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
                if (tokens.Length == 0 || string.IsNullOrEmpty(name))
                {
                    continue;
                }
                if (!LooksLikePackageId(tokens[0]))
                {
                    continue;
                }
                // 版本紧跟 Id；匹配列的值形如 "Tag: vscode"，以字母开头的标签不当作版本
                string version = tokens.Length > 1 && tokens[1].Length > 0 && char.IsDigit(tokens[1][0]) ? tokens[1] : "";
                list.Add(new WingetSearchResult(tokens[0], name, version));
            }
            if (list.Count > 0)
            {
                return list;
            }
            // 表头存在但一行都没解析出来 → 继续走启发式兜底
        }
        // 启发式兜底：无表头可定位时，按行切分并定位含点号的 Id 列。
        // 防呆：帮助横幅（"Windows 程序包管理器(预览) v1.30.100-preview"）、用法提示、
        // 帮助链接这类内容绝不能被当成候选包（2026-08-29 用户反馈的假结果来源）。
        foreach (string line in array)
        {
            if (string.IsNullOrWhiteSpace(line) || !line.Any(char.IsLetterOrDigit) || (line.Contains("Name") && (line.Contains("Id") || line.Contains("ID"))))
            {
                continue;
            }
            if (line.Contains("使用情况") || line.Contains("程序包管理器") || line.Contains("aka.ms")
                || line.Contains("usage:", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }
            string[] tokens = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (tokens.Length < 3)
            {
                continue;
            }
            for (int j = 0; j < tokens.Length; j++)
            {
                if (tokens[j].Contains('.') && tokens[j].Length >= 4 && LooksLikePackageId(tokens[j]))
                {
                    string packageId = tokens[j];
                    string name = (j > 0) ? string.Join(" ", tokens, 0, j) : packageId;
                    string version = (j + 1 < tokens.Length) ? tokens[j + 1] : "";
                    list.Add(new WingetSearchResult(packageId, name, version));
                    break;
                }
            }
        }
        return list;
    }

    /// <summary>
    /// 在表头行中定位 ID 列的起始字符位（中英文表头均可）。
    /// 必须同一行同时出现 ID 与 版本/Version 表头才算表格头——
    /// 避免帮助文本里零散的 "ID" 字样被误认为列头。
    /// </summary>
    private static int FindIdColumnIndex(string[] lines)
    {
        foreach (string line in lines)
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }
            List<(string Token, int Start)> tokens = TokenizeWithPositions(line);
            bool hasVersionHeader = tokens.Any(t => t.Token is "版本" || t.Token.Equals("Version", StringComparison.OrdinalIgnoreCase));
            if (!hasVersionHeader)
            {
                continue;
            }
            foreach ((string Token, int Start) t in tokens)
            {
                if (t.Token.Equals("ID", StringComparison.OrdinalIgnoreCase))
                {
                    return t.Start;
                }
            }
        }
        return -1;
    }

    /// <summary>把一行按空白切分为 token 并记录每个 token 的起始列位（表格列对齐切分用）。</summary>
    private static List<(string Token, int Start)> TokenizeWithPositions(string line)
    {
        var result = new List<(string, int)>();
        int i = 0;
        while (i < line.Length)
        {
            if (char.IsWhiteSpace(line[i]))
            {
                i++;
                continue;
            }
            int start = i;
            while (i < line.Length && !char.IsWhiteSpace(line[i]))
            {
                i++;
            }
            result.Add((line.Substring(start, i - start), start));
        }
        return result;
    }

    /// <summary>表头下方的分隔线（一整行横杠）。</summary>
    private static bool IsTableSeparator(string line)
    {
        return line.Length > 0 && line.All(c => c == '-' || char.IsWhiteSpace(c));
    }

    /// <summary>
    /// 候选包 Id 防呆：必须形如 "a.b"（点号两侧非空、含字母、不含路径/协议字符），
    /// 且不能是版本样式（v1.2.3 / 1.2.3-preview）——帮助横幅与错误提示不再被误判成包。
    /// </summary>
    private static bool LooksLikePackageId(string token)
    {
        if (string.IsNullOrWhiteSpace(token) || !token.Contains('.'))
        {
            return false;
        }
        // winget Id 不含协议/路径字符；URL（https://aka.ms/...）必须排除
        if (token.Contains(':') || token.Contains('/') || token.Contains('\\'))
        {
            return false;
        }
        int dot = token.IndexOf('.');
        if (dot == 0 || dot == token.Length - 1)
        {
            return false;
        }
        if (!token.Any(char.IsLetter))
        {
            return false;
        }
        // 版本样式排除：可选 v 前缀 + 数字段用点分隔（末段可带 -后缀），
        // 如 v1.30.100-preview、1.2.3-beta、0.100-preview。注意后缀（-preview）含字母，
        // 不能简单按“只含数字/点/连字符”判断。
        string t = token.StartsWith("v", StringComparison.OrdinalIgnoreCase) ? token.Substring(1) : token;
        if (t.Length > 0 && char.IsDigit(t[0]))
        {
            string[] segs = t.Split('.');
            string last = segs[^1];
            int hyphen = last.IndexOf('-');
            string lastDigits = hyphen >= 0 ? last.Substring(0, hyphen) : last;
            bool versionLike = segs.Take(segs.Length - 1).All(s => s.Length > 0 && s.All(char.IsDigit))
                && lastDigits.Length > 0 && lastDigits.All(char.IsDigit);
            if (versionLike)
            {
                return false;
            }
        }
        return true;
    }
}
