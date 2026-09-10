using System;
using System.Text.RegularExpressions;

namespace UiEditor.Core;

/// <summary>
/// 字节级 XAML「外科手术」写回器。Spike #1 已证 <c>XDocument.Save</c> 会重排全文件（补 <c>&lt;?xml?&gt;</c>、
/// 标签前塞空格、折叠多行属性）→ 改一处=全文件 diff，不可用于"逐次看 diff"。故写回**只按字节 splice**：
/// 定位目标元素开标签区间，区间内替换/插入单个属性，区间外一个字节不动 → 改一处 = diff 一行。
/// <para>前提：开标签内属性值不得含 <c>&gt;</c>（本项目 XAML 满足）。splice 不新增换行，源行号稳定。</para>
/// </summary>
public static class XamlSplicer
{
    /// <summary>按 <c>x:Name</c> 定位目标元素开标签 [start,endExclusive)。缺失/非唯一即抛（拒绝盲改）。</summary>
    public static (int Start, int End) LocateTagByName(string raw, string xName)
    {
        if (xName.IndexOf('"') >= 0 || xName.IndexOf('>') >= 0)
        {
            throw new ArgumentException("非法 x:Name");
        }

        string marker = "x:Name=\"" + xName + "\"";
        int idx = raw.IndexOf(marker, StringComparison.Ordinal);
        if (idx < 0)
        {
            throw new InvalidOperationException($"未找到 x:Name=\"{xName}\"");
        }

        if (raw.IndexOf(marker, idx + marker.Length, StringComparison.Ordinal) >= 0)
        {
            throw new InvalidOperationException($"x:Name=\"{xName}\" 非唯一，拒绝盲改");
        }

        return BoundTag(raw, idx);
    }

    /// <summary>按源行号（1-based，指向开标签起始行）定位开标签——用于无 x:Name 的元素。</summary>
    public static (int Start, int End) LocateTagByLine(string raw, int line)
    {
        int idx = StartIndexOfLine(raw, line);
        int lt = raw.IndexOf('<', idx);
        if (lt < 0)
        {
            throw new InvalidOperationException($"第 {line} 行未找到元素起始 '<'");
        }

        return BoundTag(raw, lt);
    }

    private static (int, int) BoundTag(string raw, int insideIdx)
    {
        int lt = raw.LastIndexOf('<', insideIdx);
        int gt = raw.IndexOf('>', insideIdx);
        if (lt < 0 || gt < 0)
        {
            throw new InvalidOperationException("开标签边界不完整");
        }

        return (lt, gt + 1);
    }

    private static int StartIndexOfLine(string raw, int line)
    {
        int pos = 0;
        int cur = 1;
        while (cur < line)
        {
            int nl = raw.IndexOf('\n', pos);
            if (nl < 0)
            {
                throw new InvalidOperationException($"行号 {line} 越界");
            }

            pos = nl + 1;
            cur++;
        }

        return pos;
    }

    /// <summary>读目标元素某属性当前值（供检视器播种；缺失返回 null）。</summary>
    public static string? GetAttributeValue(string raw, int start, int end, string attr)
    {
        string tag = raw.Substring(start, end - start);
        Match m = Regex.Match(tag, "(^|\\s)" + Regex.Escape(attr) + "=\"([^\"]*)\"");
        return m.Success ? m.Groups[2].Value : null;
    }

    /// <summary>把开标签区间 [start,end) 内 <c>attr</c> 设为 <c>value</c>（有则换值，无则在标签名后插入），返回整篇新文本。</summary>
    public static string SetAttributeInTag(string raw, int start, int end, string attr, string value)
    {
        if (value.IndexOf('"') >= 0 || value.IndexOf('>') >= 0)
        {
            throw new ArgumentException("属性值含非法字符");
        }

        string tag = raw.Substring(start, end - start);
        Match m = Regex.Match(tag, "(^|\\s)" + Regex.Escape(attr) + "=\"[^\"]*\"");
        string newTag;
        if (m.Success)
        {
            newTag = tag.Remove(m.Index, m.Length)
                        .Insert(m.Index, m.Groups[1].Value + attr + "=\"" + value + "\"");
        }
        else
        {
            // 插到标签名之后（'<' + name 之后），保证属性顺序稳定
            int afterName = 1;
            while (afterName < tag.Length && !char.IsWhiteSpace(tag[afterName]) && tag[afterName] != '/' && tag[afterName] != '>')
            {
                afterName++;
            }

            newTag = tag.Insert(afterName, " " + attr + "=\"" + value + "\"");
        }

        return raw.Substring(0, start) + newTag + raw.Substring(end);
    }

    /// <summary>便捷：按 x:Name 设置属性。</summary>
    public static string SetAttributeByName(string raw, string xName, string attr, string value)
    {
        (int s, int e) = LocateTagByName(raw, xName);
        return SetAttributeInTag(raw, s, e, attr, value);
    }
}
