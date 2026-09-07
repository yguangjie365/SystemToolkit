using System.Globalization;

namespace SystemToolkit.Core.Drivers;

/// <summary>
/// 驱动版本比较（四段式 a.b.c.d，Windows 驱动版本号的标准形态）。
/// 按 ulong 段逐一比较——杜绝字符串字典序误判（"8.10" 必须 &gt; "8.9"）。
/// </summary>
public static class DriverVersion
{
    /// <summary>
    /// 比较两个版本串。解析失败的段按 0 处理；null/空串视为最低。
    /// 返回值语义同 IComparable：负 = a &lt; b，0 = 相等，正 = a &gt; b。
    /// </summary>
    public static int Compare(string? a, string? b)
    {
        ulong[] pa = Parse(a);
        ulong[] pb = Parse(b);
        int len = Math.Max(pa.Length, pb.Length);
        for (int i = 0; i < len; i++)
        {
            ulong left = i < pa.Length ? pa[i] : 0;
            ulong right = i < pb.Length ? pb[i] : 0;
            if (left != right)
            {
                return left.CompareTo(right);
            }
        }

        return 0;
    }

    /// <summary>按 '.' 拆段并转 ulong；缺失/非法段记 0（"31.0.101" → [31,0,101]）。</summary>
    private static ulong[] Parse(string? version)
    {
        if (string.IsNullOrWhiteSpace(version))
        {
            return Array.Empty<ulong>();
        }

        string[] parts = version.Split('.');
        var result = new List<ulong>(parts.Length);
        foreach (string part in parts)
        {
            _ = ulong.TryParse(part.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out ulong value);
            result.Add(value);
        }

        return result.ToArray();
    }
}
