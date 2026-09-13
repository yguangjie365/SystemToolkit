using System.Text;
using System.Text.RegularExpressions;

namespace SystemToolkit.Tests.Architecture;

/// <summary>
/// Win32 互操作守卫（落地计划 B2，源报告 P0-3「CsWin32 强制令」）。
/// <para>
/// 背景：本项目 Win32 互操作长期手写，NET-6 局域网扫描**两次实踩**签名/布局坑
/// （<c>GetIpNetTable2</c> 多塞参数恒得 87、<c>SendARP</c> 的 <c>SrcIP</c> 传值恒得 1168）——
/// 根因都是"文档/记忆 vs 真实签名"的落差。CsWin32 从**官方 Win32 metadata** 编译期生成
/// 签名 / 结构体 / 常量，这类错误在源头消失（spike 已于 2026-09-13 逐字节验证布局一致，
/// 见 <c>Network/LanNeighborLayoutLockTests</c>）。
/// </para>
/// <para>
/// 本守卫把「禁止凭记忆写 Windows 原生 API 互操作」（AGENTS §二·五）从**纪律条文**
/// 升级为**机器约束**：<c>src/**</c> 下出现 <c>[DllImport]</c>/<c>[LibraryImport]</c>
/// 而 (文件, 导出符号) 不在 <see cref="Frozen"/> 白名单内 → 测试变红。
/// </para>
/// <para>
/// <b>范围与取舍</b>：
/// <list type="bullet">
/// <item>只扫 <c>src/</c>（产品代码）。测试工程的互操作探针不拦；CsWin32 生成的代码落在
/// <c>obj/</c>，本就随 <c>obj/bin</c> 一并排除。</item>
/// <item>白名单按 <b>(文件, 导出符号)</b> 而非行号——行号会漂，符号名是稳定标识。</item>
/// <item>符号取 <c>EntryPoint</c>（有则优先）否则取 C# 方法名：<c>DriverBootCriticalQuerier</c>
/// 的 <c>EntryPoint = "CM_Get_Class_PropertyW"</c> 与 C# 方法名 <c>CM_Get_Class_Property</c>
/// 刻意不同，必须按导出名记账。</item>
/// <item>注释内的同形文本<b>不算</b>违规（注释会被剥离）——否则在注释里写"原实现用
/// <c>[DllImport]</c>，现改为生成器"这种迁移说明就会误报。⚠️ 已知局限：<b>普通字符串字面量</b>内
/// 的同形文本仍会误报（字符串要原样保留才能读到 <c>EntryPoint</c> 的名字）；实际概率极低，
/// 真踩到时把该文本改写即可，不要为此给守卫加例外。</item>
/// </list>
/// </para>
/// <para>
/// 检测逻辑抽为纯函数 <see cref="NativeInteropGuard.FindSymbols"/>，并内建
/// 「样本注入=红 / 干净样本=绿」反向验证（03 §4.1：未经反向验证的守门等于没守门）。
/// </para>
/// </summary>
public class NativeInteropGuardTests
{
    /// <summary>声明起始：<c>[DllImport(</c> 或 <c>[LibraryImport(</c>（含换行写法）。</summary>
    private const string DeclarePattern = @"\[\s*(?:DllImport|LibraryImport)\s*\(";

    private static readonly Regex DeclareRegex = new(DeclarePattern, RegexOptions.Compiled);

    private static readonly Regex EntryPointRegex = new(
        @"EntryPoint\s*=\s*""(?<name>[^""]+)""", RegexOptions.Compiled);

    /// <summary>
    /// 2026-09-13 存量冻结（B2 认领时逐个回源实测 = **12 处**）。
    /// <para>
    /// ⚠️ 计数被纠正过两次，留痕以免下轮重蹈：源报告 §C1 写「6 处 DllImport」（另把
    /// <c>LanNeighborInterop</c> 的手解偏移单列，未计入）→ 我首轮只按 <c>\[DllImport</c> 走了一遍 grep
    /// 得 9 处并**漏掉了 <c>LanNeighborInterop.cs</c> 的 3 处**（SendARP / GetIpNetTable2 / FreeMibTable）
    /// → 本守卫首跑当场抓出 → 真值 **12 处**。
    /// <b>教训：清单类数字以守卫实扫为准，不要以单遍检索为准。</b>
    /// </para>
    /// <para>存量**有测试锁、不强制迁移**（迁移无收益却要重验签名）；新增一律走 CsWin32。</para>
    /// </summary>
    private static readonly (string File, string Symbol)[] Frozen =
    {
        ("src/SystemToolkit.Core/Drivers/DriverBootCriticalQuerier.cs", "CM_Get_Class_PropertyW"),
        ("src/SystemToolkit.Core/Drivers/DriverClassResolver.cs", "SetupGetInfDriverStoreLocation"),
        ("src/SystemToolkit.Core/Network/LanScan/LanNeighborInterop.cs", "FreeMibTable"),
        ("src/SystemToolkit.Core/Network/LanScan/LanNeighborInterop.cs", "GetIpNetTable2"),
        ("src/SystemToolkit.Core/Network/LanScan/LanNeighborInterop.cs", "SendARP"),
        ("src/SystemToolkit.Core/Network/Services/CommandRunner.cs", "GetACP"),
        ("src/SystemToolkit.Core/Network/Services/CommandRunner.cs", "GetOEMCP"),
        ("src/SystemToolkit.Core/Network/Services/WinInetInterop.cs", "InternetSetOption"),
        ("src/SystemToolkit.Core/Utilities/UserFolders.cs", "SHGetKnownFolderPath"),
        ("src/SystemToolkit.ElevatedHelper/DeviceClassNameReader.cs", "SetupDiGetClassDescriptionW"),
        ("src/SystemToolkit.ElevatedHelper/NetRunner.cs", "GetACP"),
        ("src/SystemToolkit.ElevatedHelper/NetRunner.cs", "GetOEMCP"),
    };

    // ---------------- 反向验证自检（03 §4.1） ----------------

    [Theory]
    [InlineData("[DllImport(\"kernel32.dll\")]\nprivate static extern uint GetOEMCP();", "GetOEMCP")]
    // EntryPoint 优先于方法名：两者刻意不同（EntryPoint 带 W 后缀）
    [InlineData(
        "[DllImport(\"cfgmgr32.dll\", EntryPoint = \"CM_Get_Class_PropertyW\")]\nprivate static extern uint CM_Get_ClassProperty(ref Guid g);",
        "CM_Get_Class_PropertyW")]
    // 属性跨行、带 return: 修饰——真实存在的三种形态都要能解析
    [InlineData(
        "[DllImport(\"wininet.dll\",\n    SetLastError = true)]\n[return: MarshalAs(UnmanagedType.Bool)]\nprivate static extern bool InternetSetOption(IntPtr h, int o);",
        "InternetSetOption")]
    [InlineData("[LibraryImport(\"kernel32.dll\")]\ninternal static partial uint GetTickCount();", "GetTickCount")]
    // 注释里的同形文本不算违规（否则迁移说明一写就红）
    [InlineData("// [DllImport(\"wininet.dll\")] 已迁至 CsWin32\nprivate static extern bool X();", "")]
    [InlineData("/* [DllImport(\"wininet.dll\")] */\nprivate static extern bool X();", "")]
    [InlineData(
        "/// <summary>原实现用 [DllImport(\"wininet.dll\")]，现改为生成器。</summary>\nprivate static extern bool X();",
        "")]
    // 干净样本：既无互操作也无同形文本
    [InlineData("private static uint GetOemCodePage() => 65001;", "")]
    public void NativeInteropGuard_DetectorFunction_SampleInjection_ReverseVerification(
        string sample, string expectedSymbol)
    {
        List<string> symbols = NativeInteropGuard.FindSymbols(sample);

        if (expectedSymbol.Length == 0)
        {
            Assert.Empty(symbols);
            return;
        }

        Assert.Equal(new[] { expectedSymbol }, symbols);
    }

    // ---------------- 全仓门禁 ----------------

    /// <summary>
    /// 全仓（src/）门禁：只允许 <see cref="Frozen"/> 里冻结的那些（2026-09-13 = 12 处）。
    /// </summary>
    [Fact]
    public void NativeInteropGuard_WholeRepoSource_OnlyFrozenDeclarationsRemain()
    {
        string root = RepoRoot();
        var allowed = new HashSet<string>(
            Frozen.Select(f => f.File + "|" + f.Symbol), StringComparer.Ordinal);
        var offenders = new List<string>();

        foreach (string file in Directory.EnumerateFiles(
                     Path.Combine(root, "src"), "*.cs", SearchOption.AllDirectories))
        {
            string normalized = file.Replace('\\', '/');
            if (normalized.Contains("/obj/") || normalized.Contains("/bin/"))
            {
                continue;
            }

            string relative = Path.GetRelativePath(root, file).Replace('\\', '/');
            foreach (string symbol in NativeInteropGuard.FindSymbols(File.ReadAllText(file)))
            {
                if (!allowed.Contains(relative + "|" + symbol))
                {
                    offenders.Add($"{relative} → {symbol}");
                }
            }
        }

        Assert.True(offenders.Count == 0, """
            发现白名单之外的 Win32 互操作声明。按落地计划 B2 / 源报告 P0-3：
            **新代码禁止手写 [DllImport]/[LibraryImport]，一律走 CsWin32**
            （官方 metadata 编译期生成签名/结构体/常量，见 02 分册与 AGENTS §二·五）。

            · 若你是在**新增**互操作（如 B7 概览的 GetExtendedTcpTable / SMART IOCTL）：
                1) 目标工程引 Microsoft.Windows.CsWin32（PrivateAssets=all）+ 该工程 NativeMethods.txt 列符号；
                2) 首次引入**产品工程**时按 ADR-002 §5.2 五步登记（§5.3 依赖清单 + NOTICE）；
                3) 不要在 src/ 手写签名——这正是 NET-6 两次实踩的根因。
            · 若你是在**迁移**白名单中的存量条目之一：改完后同步把该条从本文件 Frozen 数组移除
              （只删代码不移白名单，"白名单腐化"用例会一并变红提醒你）。

            违规清单：
            """ + string.Join("\n", offenders));
    }

    /// <summary>
    /// 白名单防腐化：每一条都必须仍真实存在。
    /// <para>
    /// 否则会留下"僵尸条目"——某处互操作已删/改名，白名单却还占着位，
    /// 将来在**同一文件**新增同名互操作就会被静默放行。迁移时最容易踩。
    /// </para>
    /// </summary>
    [Fact]
    public void NativeInteropGuard_FrozenWhitelist_EveryEntryStillExists()
    {
        string root = RepoRoot();
        var stale = new List<string>();

        foreach ((string file, string symbol) in Frozen)
        {
            string full = Path.Combine(root, file.Replace('/', Path.DirectorySeparatorChar));
            if (!File.Exists(full))
            {
                stale.Add($"{file} → {symbol}（文件已不存在）");
                continue;
            }

            if (!NativeInteropGuard.FindSymbols(File.ReadAllText(full)).Contains(symbol, StringComparer.Ordinal))
            {
                stale.Add($"{file} → {symbol}（该导出符号已不在文件中）");
            }
        }

        Assert.True(stale.Count == 0, """
            本守卫的 Frozen 白名单出现**腐化条目**（代码侧已无对应声明，白名单却仍豁免）：
            """ + string.Join("\n", stale) + """

            处置：把这（些）条从 NativeInteropGuardTests.cs 的 Frozen 数组里删掉。
            留着会让"同文件新增同名互操作"被静默放行——白名单只减不增才是它该有的形状。
            """);
    }

    private static string RepoRoot()
    {
        DirectoryInfo? dir = new(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "SystemToolkit.sln")))
        {
            dir = dir.Parent;
        }

        return dir?.FullName ?? throw new InvalidOperationException("未定位到仓库根（SystemToolkit.sln）");
    }

    /// <summary>
    /// 扫描函数集中于此（<c>internal</c> 供反向验证直测；03 §十：判据放宽到 internal 才钉得住实现）。
    /// </summary>
    internal static class NativeInteropGuard
    {
        /// <summary>返回源码中每一条互操作声明的**导出符号**；无法解析的记 <c>&lt;unparsed&gt;</c>。</summary>
        public static List<string> FindSymbols(string source)
        {
            string code = StripComments(source);
            var symbols = new List<string>();

            foreach (Match match in DeclareRegex.Matches(code))
            {
                int open = match.Index + match.Length - 1;
                int close = FindClosingParen(code, open);
                if (close < 0)
                {
                    symbols.Add("<unparsed>");
                    continue;
                }

                string arguments = code.Substring(open + 1, close - open - 1);
                symbols.Add(ResolveSymbol(arguments, code, close + 1) ?? "<unparsed>");
            }

            return symbols;
        }

        /// <summary>
        /// 导出符号解析：<c>EntryPoint</c> 优先，否则取声明的方法名。
        /// <para>
        /// 不能靠 <c>extern</c> 关键字定位：<c>DllImport</c> 声明带 <c>extern</c>，而
        /// <c>LibraryImport</c> 声明是 <c>partial</c>——本守卫**首跑**即因此把该样本判成 <c>&lt;unparsed&gt;</c>。
        /// 故改为「跳过属性组 → 取声明里第一个 <c>(</c> 之前最近的标识符」，
        /// 顺带兼容 <c>[return: MarshalAs(...)]</c> 这类夹在属性与声明之间、自带括号的写法。
        /// </para>
        /// </summary>
        private static string? ResolveSymbol(string attributeArguments, string code, int afterAttribute)
        {
            Match entryPoint = EntryPointRegex.Match(attributeArguments);
            if (entryPoint.Success)
            {
                return entryPoint.Groups["name"].Value;
            }

            int cursor = afterAttribute;

            // 本特性组形如 [DllImport(...)]：收尾是 ")]"，先跳过那个 ']'，
            // 否则紧随其后的 [return: MarshalAs(...)] 会被当成声明（本守卫第二次跑就是这么红的）
            int attributeBracket = code.IndexOf(']', cursor);
            if (attributeBracket >= 0)
            {
                cursor = attributeBracket + 1;
            }

            // 再跳过后续其它特性组——它们同样带括号，会干扰"第一个 ( 之前是方法名"的判定
            while (true)
            {
                while (cursor < code.Length && char.IsWhiteSpace(code[cursor]))
                {
                    cursor++;
                }

                if (cursor >= code.Length || code[cursor] != '[')
                {
                    break;
                }

                int closeBracket = code.IndexOf(']', cursor);
                if (closeBracket < 0)
                {
                    break;
                }

                cursor = closeBracket + 1;
            }

            int semicolon = code.IndexOf(';', cursor);
            int paren = code.IndexOf('(', cursor);
            if (semicolon < 0 || paren < 0 || paren > semicolon)
            {
                return null;
            }

            int end = paren - 1;
            while (end >= cursor && char.IsWhiteSpace(code[end]))
            {
                end--;
            }

            int start = end;
            while (start >= 0 && (char.IsLetterOrDigit(code[start]) || code[start] == '_'))
            {
                start--;
            }

            return end > start ? code.Substring(start + 1, end - start) : null;
        }

        /// <summary>括号配平（属性参数可跨行、可含嵌套括号）。</summary>
        private static int FindClosingParen(string code, int openIndex)
        {
            int depth = 0;
            for (int i = openIndex; i < code.Length; i++)
            {
                if (code[i] == '(')
                {
                    depth++;
                }
                else if (code[i] == ')')
                {
                    depth--;
                    if (depth == 0)
                    {
                        return i;
                    }
                }
            }

            return -1;
        }

        /// <summary>
        /// 剥离注释，**原样保留**字符串/字符字面量。
        /// <para>
        /// 保留字面量是必需的：导出名常常写在字面量里（<c>EntryPoint = "CM_Get_Class_PropertyW"</c>），
        /// 抹掉就取不到符号。代价是"普通字符串里的同形文本"仍会命中（见类注释的已知局限）。
        /// </para>
        /// </summary>
        private static string StripComments(string source)
        {
            var builder = new StringBuilder(source.Length);
            int i = 0;

            while (i < source.Length)
            {
                char current = source[i];
                char next = i + 1 < source.Length ? source[i + 1] : '\0';

                if (current == '/' && next == '/')
                {
                    while (i < source.Length && source[i] != '\n')
                    {
                        i++;
                    }
                }
                else if (current == '/' && next == '*')
                {
                    i += 2;
                    while (i + 1 < source.Length && !(source[i] == '*' && source[i + 1] == '/'))
                    {
                        i++;
                    }

                    i = Math.Min(i + 2, source.Length);
                    builder.Append('\n'); // 保留一个换行，避免把前后两行粘成一行
                }
                else if (current == '@' && next == '"')
                {
                    i = CopyLiteral(source, i, builder, verbatim: true, quote: '"');
                }
                else if (current == '"' || current == '\'')
                {
                    i = CopyLiteral(source, i, builder, verbatim: false, quote: current);
                }
                else
                {
                    builder.Append(current);
                    i++;
                }
            }

            return builder.ToString();
        }

        /// <summary>原样拷贝一个字面量（含定界符与转义），返回其后的下标。</summary>
        private static int CopyLiteral(string source, int start, StringBuilder builder, bool verbatim, char quote)
        {
            int i = start;
            if (verbatim)
            {
                builder.Append('@');
                i++;
            }

            builder.Append(quote);
            i++;

            while (i < source.Length)
            {
                char current = source[i];

                if (verbatim && current == quote && i + 1 < source.Length && source[i + 1] == quote)
                {
                    builder.Append(current).Append(quote);
                    i += 2;
                    continue;
                }

                if (!verbatim && current == '\\' && i + 1 < source.Length)
                {
                    builder.Append(current).Append(source[i + 1]);
                    i += 2;
                    continue;
                }

                builder.Append(current);
                i++;

                if (current == quote)
                {
                    break;
                }
            }

            return i;
        }
    }
}
