using System.Text;

namespace SystemToolkit.Core.GameManager.Services;

/// <summary>
/// Steam VDF（Valve Data Format）纯手工词法解析器，逐字扫描。
/// 1:1 移植自 NexBox <c>steam.rs:VdfParser</c>（L56–L198），保留同名方法、
/// 相同边界行为与报错格式，以便后续用同一份样本字节做回归测试。
/// </summary>
/// <remarks>
/// 解析能力有限，仅覆盖 Steam 客户端在 Windows 上实际产出的 3 类 VDF 文件：
/// <list type="bullet">
/// <item><c>config/loginusers.vdf</c>（记住的用户）</item>
/// <item><c>config/libraryfolders.vdf</c> + <c>steamapps/libraryfolders.vdf</c>（新旧两种库目录）</item>
/// <item><c>steamapps/appmanifest_*.acf</c>（每个已安装游戏的清单）</item>
/// </list>
/// 注释 <c>//</c> 行内已支持（<c>skip_whitespace_and_comments</c>）。
/// </remarks>
internal enum VdfValueTag { String, Object }

internal sealed class VdfValue
{
    private VdfValueTag Tag { get; }
    private string? AsString { get; }
    private List<KeyValuePair<string, VdfValue>>? AsObject { get; }

    private VdfValue(string s) { Tag = VdfValueTag.String; AsString = s; }
    private VdfValue(List<KeyValuePair<string, VdfValue>> obj) { Tag = VdfValueTag.Object; AsObject = obj; }

    public static VdfValue MakeString(string s) => new(s);
    public static VdfValue MakeObject(List<KeyValuePair<string, VdfValue>> entries) => new(entries);

    /// <summary>按 key 取字符串值（仅对 Object 有效；找不到返回 null）。</summary>
    public string? GetStr(string key)
    {
        if (Tag != VdfValueTag.Object || AsObject is null)
            return null;
        foreach ((string? k, VdfValue? v) in AsObject)
        {
            if (k == key && v.Tag == VdfValueTag.String)
                return v.AsString;
        }
        return null;
    }

    /// <summary>
    /// 取本节点自身的字符串值（仅 String 标签有效）。
    /// 【坑】GetStr(key) 是"在 Object 的子节点里按 key 查"，对本节点是 String 的取值必须用本方法——
    /// 重写 loginusers.vdf 时曾误用 GetStr(k) 导致所有字段值被写成空串（2026-09-03 修复）。
    /// </summary>
    public string? GetString() => Tag == VdfValueTag.String ? AsString : null;

    /// <summary>按 key 取子对象（找不到返回 null）。</summary>
    public VdfValue? GetObj(string key)
    {
        if (Tag != VdfValueTag.Object || AsObject is null)
            return null;
        foreach ((string? k, VdfValue? v) in AsObject)
        {
            if (k == key)
                return v;
        }
        return null;
    }

    /// <summary>如果是 Object 返回 entries 数组，否则 null。</summary>
    public IReadOnlyList<KeyValuePair<string, VdfValue>>? GetObjEntries()
    {
        return Tag == VdfValueTag.Object ? AsObject : null;
    }
}

internal sealed class VdfParser
{
    private readonly char[] _chars;
    private int _pos;

    private VdfParser(string input)
    {
        // 规范化：去掉 UTF-8 BOM（若有）；Rust read_to_string 自动去 BOM，C# File.ReadAllText 也去，
        // 但测试用例字节可能带 BOM，这里做防御。
        if (input.Length > 0 && input[0] == '\uFEFF')
            input = input.Substring(1);
        _chars = input.ToCharArray();
        _pos = 0;
    }

    public static VdfValue Parse(string input)
    {
        var p = new VdfParser(input);
        return p.ParseRoot();
    }

    /// <summary>解析根级别（无外层大括号；遇到末尾或孤零零的 } 即结束）。</summary>
    private VdfValue ParseRoot()
    {
        SkipWhitespaceAndComments();
        var entries = new List<KeyValuePair<string, VdfValue>>();
        while (true)
        {
            SkipWhitespaceAndComments();
            if (_pos >= _chars.Length)
                break;
            if (_chars[_pos] == '}')
                break; // 安全兜底：根级别的 } 直接结束
            string key = PeekQuote()
                ? ParseQuotedString()
                : ParseUnquotedToken();
            SkipWhitespaceAndComments();
            if (_pos >= _chars.Length)
            { entries.Add(Kvp(key, VdfValue.MakeString(string.Empty))); break; }
            VdfValue value = ParseValue();
            entries.Add(Kvp(key, value));
        }
        return VdfValue.MakeObject(entries);
    }

    private VdfValue ParseValue()
    {
        SkipWhitespaceAndComments();
        if (_pos >= _chars.Length)
            throw new InvalidDataException("Unexpected end of input");
        char c = _chars[_pos];
        return c switch
        {
            '"' => VdfValue.MakeString(ParseQuotedString()),
            '{' => ParseObject(),
            _ => VdfValue.MakeString(ParseUnquotedToken()),
        };
    }

    private string ParseQuotedString()
    {
        _pos++; // 吃掉开头 "
        var sb = new StringBuilder();
        while (_pos < _chars.Length)
        {
            char c = _chars[_pos];
            switch (c)
            {
                case '"':
                    _pos++;
                    return sb.ToString();
                case '\\':
                    _pos++;
                    if (_pos < _chars.Length)
                    { sb.Append(_chars[_pos]); _pos++; }
                    break;
                default:
                    sb.Append(c);
                    _pos++;
                    break;
            }
        }
        throw new InvalidDataException("Unterminated string");
    }

    private string ParseUnquotedToken()
    {
        var sb = new StringBuilder();
        while (_pos < _chars.Length)
        {
            char c = _chars[_pos];
            if (char.IsWhiteSpace(c) || c == '{' || c == '}' || c == '"')
                break;
            sb.Append(c);
            _pos++;
        }
        if (sb.Length == 0)
            throw new InvalidDataException($"Unexpected char at pos {_pos}");
        return sb.ToString();
    }

    private VdfValue ParseObject()
    {
        _pos++; // 吃掉 {
        var entries = new List<KeyValuePair<string, VdfValue>>();
        while (true)
        {
            SkipWhitespaceAndComments();
            if (_pos >= _chars.Length)
                throw new InvalidDataException("Unexpected end of object");
            if (_chars[_pos] == '}')
            { _pos++; break; }

            string key = PeekQuote()
                ? ParseQuotedString()
                : ParseUnquotedToken();
            SkipWhitespaceAndComments();
            VdfValue value = ParseValue();
            entries.Add(Kvp(key, value));
        }
        return VdfValue.MakeObject(entries);
    }

    private void SkipWhitespaceAndComments()
    {
        while (_pos < _chars.Length)
        {
            char c = _chars[_pos];
            if (char.IsWhiteSpace(c))
            {
                _pos++;
            }
            else if (c == '/' && _pos + 1 < _chars.Length && _chars[_pos + 1] == '/')
            {
                // line comment: 跳过到行尾（或文件尾）
                while (_pos < _chars.Length && _chars[_pos] != '\n')
                    _pos++;
            }
            else
            {
                break;
            }
        }
    }

    private bool PeekQuote() => _pos < _chars.Length && _chars[_pos] == '"';

    private static KeyValuePair<string, VdfValue> Kvp(string k, VdfValue v) => new(k, v);
}
