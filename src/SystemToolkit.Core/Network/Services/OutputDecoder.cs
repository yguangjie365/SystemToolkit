using System.Text;

namespace SystemToolkit.Core.Network.Services;

/// <summary>
/// 子进程输出解码的<b>自适应择优</b>（【真机修复 2026-08-31】）。
/// <para>
/// 【实证】netsh 与 ipconfig 在同一台机器上的重定向输出<b>编码不一致</b>：
/// netsh 输出可用 UTF-8 正确解码（中文标签解析成功），而 ipconfig 输出是 GBK
/// ——用固定单一编码解码必然一边乱码（截图实证「已成功刷新 DNS 解析缓存」变 FFFD 串）。
/// </para>
/// <para>
/// 算法：收集原始字节，结束后对候选编码逐一解码、统计 U+FFFD（替换符）数量，
/// 取<b>替换符最少</b>者（平手取候选顺序靠前者——OEM 优先）。
/// 关键区分度：GBK 字节被 UTF-8 解码会产生替换符（如「Windows IP 配置」→「Windows IP ？???」），
/// 而候选中正确的编码解出零替换符；反向场景（UTF-8 字节被 GBK 解）因 GBK 覆盖面广
/// 可能零替换符——此时按候选顺序（OEM/ACP 优先）兜住，与「正确编码在前」的概率一致。
/// </para>
/// </summary>
public static class OutputDecoder
{
    /// <summary>构造候选编码列表（去重，OEM → ACP → GBK(936) → UTF-8）。</summary>
    public static IReadOnlyList<Encoding> BuildCandidates(uint oemCp, uint acp)
    {
        var list = new List<Encoding>(4);
        void Add(uint codePage)
        {
            try
            {
                var encoding = Encoding.GetEncoding((int)codePage);
                if (!list.Contains(encoding))
                {
                    list.Add(encoding);
                }
            }
            catch (Exception)
            {
                // 单个候选不可用（如代码页数据缺失）直接跳过，UTF-8 兜底在下面
            }
        }

        Add(oemCp);
        Add(acp);
        Add(936);
        if (!list.Contains(Encoding.UTF8))
        {
            list.Add(Encoding.UTF8);
        }

        return list;
    }

    /// <summary>从候选中择优：替换符（U+FFFD）最少的编码；平手取顺序靠前者。</summary>
    public static Encoding PickBest(byte[] bytes, IReadOnlyList<Encoding> candidates)
    {
        // 【真机修复·二轮】严格 UTF-8 校验优先（实证：同机 netsh 输出是合法 UTF-8、
        // ipconfig 是 GBK——两工具编码真的不同）。UTF-8 是自同步编码，结构校验可
        // 100% 区分两者；而 FFFD 计数法对「UTF-8 数据被 GBK 解」是盲的（GBK 覆盖面广，
        // 解 UTF-8 中文常零替换符但全是错字 → 中文标签变错字 → 解析全失败，实测踩过）。
        if (IsValidUtf8(bytes))
        {
            return Encoding.UTF8;
        }

        // 非 UTF-8（如 GBK）：在候选里按替换符计数择优（GBK 数据 → 936 解出零替换符）
        Encoding best = candidates[0];
        int bestCount = int.MaxValue;
        foreach (Encoding candidate in candidates)
        {
            if (candidate.CodePage == 65001)
            {
                continue; // 已被 IsValidUtf8 排除：非 UTF-8 数据再试 UTF-8 必产生替换符
            }

            int count = CountReplacements(candidate.GetString(bytes));
            if (count < bestCount)
            {
                best = candidate;
                bestCount = count;
                if (count == 0)
                {
                    break; // 零替换符已是最优
                }
            }
        }

        return best;
    }

    /// <summary>
    /// 严格 RFC 3629 UTF-8 序列校验（拒绝越界 continuation / 过长编码 / 代理区）。
    /// 全 ASCII 视为合法（ASCII 是 UTF-8 子集，两种解码结果一致）。
    /// </summary>
    public static bool IsValidUtf8(byte[] bytes)
    {
        int i = 0;
        while (i < bytes.Length)
        {
            byte b = bytes[i];
            int required; // 后续 continuation 字节数

            // 首字节范围：拒绝 C0-C1（过长编码）、F5-FF（越界 U+10FFFF 与孤立 continuation）
            bool secondOk = true;
            if (b <= 0x7F)
            {
                required = 0;
            }
            else if (b is >= 0xC2 and <= 0xDF)
            {
                required = 1;
            }
            else if (b is >= 0xE0 and <= 0xEF)
            {
                required = 2;
                if (i + 1 >= bytes.Length)
                {
                    return false;
                }

                // 拒绝过长编码（E0 后续须 A0-BF）与代理区（ED 后续须 80-9F）
                secondOk = b switch
                {
                    0xE0 => bytes[i + 1] is >= 0xA0 and <= 0xBF,
                    0xED => bytes[i + 1] is >= 0x80 and <= 0x9F,
                    _ => bytes[i + 1] is >= 0x80 and <= 0xBF,
                };
            }
            else if (b is >= 0xF0 and <= 0xF4)
            {
                required = 3;
                if (i + 1 >= bytes.Length)
                {
                    return false;
                }

                // 拒绝超过 U+10FFFF（F0 后续须 90-BF；F4 后续须 80-8F）
                secondOk = b switch
                {
                    0xF0 => bytes[i + 1] is >= 0x90 and <= 0xBF,
                    0xF4 => bytes[i + 1] is >= 0x80 and <= 0x8F,
                    _ => bytes[i + 1] is >= 0x80 and <= 0xBF,
                };
            }
            else
            {
                return false;
            }

            if (!secondOk)
            {
                return false;
            }

            for (int j = 1; j <= required; j++)
            {
                if (i + j >= bytes.Length || bytes[i + j] is not (>= 0x80 and <= 0xBF))
                {
                    return false;
                }
            }

            i += required + 1;
        }

        return true;
    }

    /// <summary>统计解码结果中的替换符数量。</summary>
    public static int CountReplacements(string text)
    {
        int count = 0;
        foreach (char c in text)
        {
            if (c == '\uFFFD')
            {
                count++;
            }
        }

        return count;
    }

    /// <summary>
    /// 把整段文本按行切分并逐行回调（去 \r）。
    /// 【真机反馈】<b>跳过纯空白行</b>——ipconfig /renew 的输出含大量空行，
    /// 逐行加前缀后把日志刷成满屏「[修复] 」空行，阅读性极差；段落分隔
    /// 由「适配器名：」等内容行自然承担。
    /// </summary>
    public static void ForEachLine(string text, Action<string> onLine)
    {
        foreach (string rawLine in text.Split('\n'))
        {
            string line = rawLine.TrimEnd('\r');
            if (line.Trim().Length == 0)
            {
                continue;
            }

            onLine(line);
        }
    }
}
