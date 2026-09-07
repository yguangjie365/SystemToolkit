using SystemToolkit.Core.Software.Services;

namespace SystemToolkit.Tests;

/// <summary>
/// WingetService 解析逻辑回归测试（全量 winget list 本地匹配 FindPackageRow）。
/// </summary>
public class WingetServiceTests
{
    [Fact]
    public void FindPackageRow_FullListLocatedById_InstalledHasNoAvailableVersion()
    {
        const string list = """
            7-Zip    7zip.7zip        24.09           winget
            Git      Git.Git          2.47.0          winget
            """;
        (bool found, string? version, string? available) = WingetService.FindPackageRow(list, "7zip.7zip");
        Assert.True(found);
        Assert.Equal("24.09", version);
        Assert.Null(available);
    }

    [Fact]
    public void FindPackageRow_HasAvailableUpdateVersion()
    {
        const string list = """
            Git      Git.Git          2.47.0   2.47.1  winget
            """;
        (bool found, string? version, string? available) = WingetService.FindPackageRow(list, "Git.Git");
        Assert.True(found);
        Assert.Equal("2.47.0", version);
        Assert.Equal("2.47.1", available);
    }

    [Fact]
    public void FindPackageRow_UninstalledPackageNotInTable()
    {
        const string list = """
            Git      Git.Git          2.47.0          winget
            """;
        (bool found, string? version, string? available) = WingetService.FindPackageRow(list, "Not.Installed.Pkg");
        Assert.False(found);
        Assert.Null(version);
        Assert.Null(available);
    }

    [Fact]
    public void FindPackageRow_IdIsSubstring_DoesNotFalseMatch()
    {
        // "Git" 是 "GitHub.GitHubDesktop" 的子串，但 Id 之后不是空白分隔 → 不应匹配
        const string list = """
            GitHub Desktop    GitHub.GitHubDesktop    3.4.5    winget
            """;
        (bool found, string? _, string? _) = WingetService.FindPackageRow(list, "Git");
        Assert.False(found);
    }

    [Fact]
    public void FindPackageRow_ParsesMsstoreRow()
    {
        // msstore 行含 "Microsoft Store" 来源列，可用版本可能为 Unknown
        const string list = """
            Windows Terminal   9N0DX20HK701    1.20.1          Microsoft Store
            """;
        (bool found, string? version, string? available) = WingetService.FindPackageRow(list, "9N0DX20HK701");
        Assert.True(found);
        Assert.Equal("1.20.1", version);
        Assert.Null(available);
    }

    [Fact]
    public void ParseSearchResults_ParsesJsonOutput()
    {
        // winget search --output json（winget 1.6+）真实结构
        const string json = """
            {
              "Matches": [
                {
                  "Package": {
                    "Id": "7zip.7zip",
                    "Name": "7-Zip",
                    "Version": "24.09",
                    "Source": "winget",
                    "Description": "7-Zip is a file archiver with a high compression ratio"
                  }
                },
                {
                  "Package": {
                    "Id": "Git.Git",
                    "Name": "Git",
                    "Version": "2.47.0",
                    "Source": "winget"
                  }
                }
              ],
              "TotalMatches": 2
            }
            """;
        List<WingetSearchResult> results = WingetService.ParseSearchResults(json);
        Assert.Equal(2, results.Count);
        Assert.Equal(new WingetSearchResult("7zip.7zip", "7-Zip", "24.09"), results[0]);
        Assert.Equal(new WingetSearchResult("Git.Git", "Git", "2.47.0"), results[1]);
    }

    [Fact]
    public void ParseSearchResults_JsonMissingVersion_DoesNotCrash()
    {
        const string json = """
            { "Matches": [ { "Package": { "Id": "Pkg.Id", "Name": "Some Pkg" } } ] }
            """;
        List<WingetSearchResult> results = WingetService.ParseSearchResults(json);
        WingetSearchResult single = Assert.Single(results);
        Assert.Equal("Pkg.Id", single.Id);
        Assert.Equal("", single.Version);
    }

    [Fact]
    public void ParseSearchResults_NonJsonOutput_FallsBackToTextParsing()
    {
        // 旧版 winget 表格输出 / 非 JSON 错误信息 → 回退到文本行切分
        const string text = """
            名称   Id          版本      源
            7-Zip  7zip.7zip   24.09     winget
            Git    Git.Git     2.47.0    winget
            """;
        List<WingetSearchResult> results = WingetService.ParseSearchResults(text);
        Assert.Contains(new WingetSearchResult("7zip.7zip", "7-Zip", "24.09"), results);
        Assert.Contains(new WingetSearchResult("Git.Git", "Git", "2.47.0"), results);
    }

    [Fact]
    public void ParseSearchResults_EmptyInput_ReturnsEmptyList()
    {
        Assert.Empty(WingetService.ParseSearchResults(""));
        Assert.Empty(WingetService.ParseSearchResults("   \n  "));
    }

    [Fact]
    public void ParseSearchResults_ChineseHeaders_ParsedByColumnPosition()
    {
        // 本机 winget v1.30.100-preview 实测表格输出：中表头 + 匹配列（Tag:/Moniker: 前缀）
        const string text = """
            名称                                  ID                                        版本         匹配                               源
            --------------------------------------------------------------------------------------------------------------------------
            Microsoft Visual Studio Code          Microsoft.VisualStudioCode                1.135.0      Moniker: vscode                    winget
            Codium                                Alex313031.Codium                         1.93.1.24277 Tag: vscode                        winget
            gh-usage                              Kukisama.gh-usage                         1.2.3        Tag: vscode                        winget
            """;
        List<WingetSearchResult> results = WingetService.ParseSearchResults(text);
        Assert.Equal(3, results.Count);
        Assert.Contains(new WingetSearchResult("Microsoft.VisualStudioCode", "Microsoft Visual Studio Code", "1.135.0"), results);
        Assert.Contains(new WingetSearchResult("Alex313031.Codium", "Codium", "1.93.1.24277"), results);
        Assert.Contains(new WingetSearchResult("Kukisama.gh-usage", "gh-usage", "1.2.3"), results);
    }

    [Fact]
    public void ParseSearchResults_NameContainsDots_DoesNotMisSplit()
    {
        // 回归守卫：名称列本身含点号（".NET SDK"）时，"首个含点号 token 即 Id" 的启发式
        // 会把 ".NET" 当成 Id；列位切分必须以表头 ID 列为准
        const string text = """
            名称                    ID                        版本      源
            ------------------------------------------------------------------
            .NET SDK                Microsoft.DotNet.SDK.8    8.0.100   winget
            """;
        WingetSearchResult single = Assert.Single(WingetService.ParseSearchResults(text));
        Assert.Equal(new WingetSearchResult("Microsoft.DotNet.SDK.8", ".NET SDK", "8.0.100"), single);
    }

    [Fact]
    public void ParseSearchResults_HelpText_ProducesNoCandidatePackages()
    {
        // 回归守卫（2026-08-29 用户反馈）：winget 不识别参数时打印用法帮助，
        // 帮助横幅 "Windows 程序包管理器(预览) v1.30.100-preview" 曾被解析成假候选包
        const string text = """
            Windows 程序包管理器(预览) v1.30.100-preview
            © 2026 Microsoft。保留所有权利。

            当前命令无法识别参数名称: "--output"

            从配置的源搜索程序包。

            使用情况: winget search [[-q] <query>] [<选项>]

            [winget]   可在此外找到更多帮助: "https://aka.ms/winget-command-search"
            [winget]   --no-proxy                禁止对此执行使用代理
            [winget]   --proxy                   设置要用于此执行的代理
            """;
        Assert.Empty(WingetService.ParseSearchResults(text));
    }

    [Fact]
    public void ParseSearchResults_NoMatchFooter_ReturnsEmpty()
    {
        const string text = """
            名称   ID   版本   源
            --------------------
            没有找到与输入匹配的内容。
            """;
        Assert.Empty(WingetService.ParseSearchResults(text));
    }

    [Theory]
    [InlineData("v1.30.100-preview")]
    [InlineData("1.135.0")]
    [InlineData("https://aka.ms/winget-command-search")]
    [InlineData("criteria.")]
    [InlineData("1.2.3-beta")]
    public void LooksLikePackageId_VersionAndUrlStyleTokens_NotMisjudged(string token)
    {
        // 通过公开入口间接验证：单个 token 的行永远解析不出候选包
        string text = $"名称  ID  版本  源\n{token}  {token}  {token}  {token}";
        Assert.Empty(WingetService.ParseSearchResults(text));
    }

    // ==================================================================
    // 参数构造（ArgumentList）：命令注入防护回归
    // ==================================================================

    [Fact]
    public void BuildIdArgs_ArgumentsSplitPerItem_NoStringConcatenation()
    {
        List<string> args = WingetService.BuildIdArgs("install", "Git.Git", source: null,
            silent: true, acceptPackageAgreements: true);

        Assert.Equal(
            new[] { "install", "--id", "Git.Git", "--exact", "--silent",
                    "--accept-package-agreements", "--accept-source-agreements",
                    "--disable-interactivity" },
            args);
    }

    /// <summary>
    /// S5/S6（REVIEW-2026-08-30）回归守卫：id 来自导入的 JSON 或用户输入。参数表模式
    /// （不做字符串拼接）是第一层防线；第二层拦截真实攻击面——以 - 开头（被解析为选项）、
    /// 引号、控制字符、空值/超长。注意：$(calc) 等元字符在 ArgumentList 模式下是惰性
    /// 单参数（无 shell 解析），属放行侧（见 BuildIdArgs_ValidId_PassesWhitelist）。
    /// </summary>
    [Theory]
    [InlineData("evil\"; --force")]     // 含引号
    [InlineData("id\" --uninstall")]    // 含引号
    [InlineData("--manifest")]          // 选项注入
    [InlineData("-x")]                  // 单个 - 开头同样拒绝
    [InlineData("id\twithTab")]         // 控制字符
    public void BuildIdArgs_MaliciousId_RejectedByWhitelist(string maliciousId)
    {
        ArgumentException ex = Assert.Throws<ArgumentException>(() =>
            WingetService.BuildIdArgs("install", maliciousId, source: null,
                silent: true, acceptPackageAgreements: true));

        Assert.Contains("非法的包 Id", ex.Message);
    }

    [Fact]
    public void BuildIdArgs_MaliciousSourceName_RejectedByWhitelist()
    {
        ArgumentException ex = Assert.Throws<ArgumentException>(() =>
            WingetService.BuildIdArgs("list", "Git.Git", source: "--evil-source",
                silent: false, acceptPackageAgreements: false));

        Assert.Contains("非法的源名称", ex.Message);
    }

    [Theory]
    [InlineData("Git.Git")]
    [InlineData("Microsoft.VisualStudioCode")]
    [InlineData("9N0DX20HK701")] // msstore 9 位 id
    [InlineData("x")]
    [InlineData("a.b_c-d")]
    [InlineData("腾讯会议")]                    // 中文 Id（winget list 真实存在）
    [InlineData("{A1B2C3D4-1234-5678-90AB-CDEF12345678}")] // ARP/GUID 形态
    [InlineData("ARP\\Machine\\X64\\Some App")] // 注册表键路径形态，含空格与反斜杠
    [InlineData("$(calc)")]                     // ArgumentList 模式下是惰性单参数，无 shell
    public void BuildIdArgs_ValidId_PassesWhitelist(string id)
    {
        List<string> args = WingetService.BuildIdArgs("install", id, source: null,
            silent: true, acceptPackageAgreements: true);

        int idIndex = args.IndexOf("--id");
        Assert.True(idIndex >= 0);
        Assert.Equal(id, args[idIndex + 1]);
    }

    [Fact]
    public void BuildIdArgs_Uninstall_OmitsPackageAgreementsParam()
    {
        // uninstall 子命令不识别 --accept-package-agreements（winget v1.29.290 已验证）
        List<string> args = WingetService.BuildIdArgs("uninstall", "Git.Git", source: null,
            silent: true, acceptPackageAgreements: false);

        Assert.DoesNotContain("--accept-package-agreements", args);
        Assert.Contains("--accept-source-agreements", args);
        Assert.Contains("--silent", args);
    }

    [Fact]
    public void BuildIdArgs_ListQuery_OmitsSilentParam()
    {
        // list --id 用于查询，不需要 --silent
        List<string> args = WingetService.BuildIdArgs("list", "Git.Git", source: null,
            silent: false, acceptPackageAgreements: false);

        Assert.DoesNotContain("--silent", args);
        Assert.DoesNotContain("--accept-package-agreements", args);
    }

    [Fact]
    public void BuildIdArgs_Source_AppendedAsTwoSeparateArgs()
    {
        List<string> args = WingetService.BuildIdArgs("list", "9N0DX20HK701", source: "msstore",
            silent: false, acceptPackageAgreements: false);

        int sourceIndex = args.IndexOf("--source");
        Assert.True(sourceIndex >= 0);
        Assert.Equal("msstore", args[sourceIndex + 1]);
        // 源必须成对出现，且位于参数表末尾
        Assert.Equal(args.Count - 2, sourceIndex);
    }

    [Fact]
    public void BuildIdArgs_EmptySource_NotAppended()
    {
        Assert.DoesNotContain("--source",
            WingetService.BuildIdArgs("install", "Git.Git", source: null, silent: true, acceptPackageAgreements: true));
        Assert.DoesNotContain("--source",
            WingetService.BuildIdArgs("install", "Git.Git", source: "  ", silent: true, acceptPackageAgreements: true));
    }

    // ==================================================================
    // 源切换 / 恢复（一键换源）
    // ==================================================================

    [Fact]
    public void BuildSourceResetArgs_RequiresForce_OtherwiseWingetDoesNotReallyReset()
    {
        List<string> args = WingetService.BuildSourceResetArgs("winget");

        // 微软文档：reset 会移除源，必须用 --force 才会真正执行。
        // 缺了它 winget 只打印提示并以非零码退出，源毫无变化——表现为「点了没反应」。
        Assert.Equal(["source", "reset", "winget", "--force", "--disable-interactivity"], args);
    }

    [Fact]
    public void BuildSourceAddArgs_IncludesTrustLevelTrusted_AndDisableInteractivity()
    {
        List<string> args = WingetService.BuildSourceAddArgs("winget", "https://mirrors.ustc.edu.cn/winget-source");

        Assert.Equal(
        [
            "source", "add", "winget", "https://mirrors.ustc.edu.cn/winget-source",
            "--trust-level", "trusted", "--disable-interactivity"
        ], args);
    }

    [Fact]
    public void BuildSourceRemoveArgs_IncludesDisableInteractivity()
    {
        Assert.Equal(["source", "remove", "winget-font", "--disable-interactivity"],
            WingetService.BuildSourceRemoveArgs("winget-font"));
    }

    [Theory]
    [InlineData("--evil")]
    [InlineData("-n")]
    public void BuildSourceArgs_SourceNameStartingWithDash_Rejected(string name)
    {
        // 以 - 开头的值会被 winget 解析器当成选项而非源名
        Assert.Throws<ArgumentException>(() => WingetService.BuildSourceRemoveArgs(name));
        Assert.Throws<ArgumentException>(() => WingetService.BuildSourceResetArgs(name));
        Assert.Throws<ArgumentException>(() => WingetService.BuildSourceAddArgs(name, "https://example.com"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void BuildSourceArgs_BlankSourceName_Rejected(string name)
    {
        Assert.Throws<ArgumentException>(() => WingetService.BuildSourceRemoveArgs(name));
        Assert.Throws<ArgumentException>(() => WingetService.BuildSourceResetArgs(name));
        Assert.Throws<ArgumentException>(() => WingetService.BuildSourceAddArgs(name, "https://example.com"));
    }

    [Fact]
    public void BuildSourceAddArgs_BlankUrl_Rejected()
    {
        Assert.Throws<ArgumentException>(() => WingetService.BuildSourceAddArgs("winget", "  "));
    }

    [Fact]
    public void WingetMirrors_Ustc_SourceNamesMatchOfficialDefaultsOneToOne()
    {
        // 换源与恢复必须覆盖同一批源名，否则换源后会残留镜像条目、reset 清不掉
        Assert.Equal(WingetMirrors.DefaultSourceNames, WingetMirrors.Ustc.Select(m => m.Name));
    }

    [Fact]
    public void WingetMirrors_Ustc_UrlsAreHttpsAndNonEmpty()
    {
        Assert.All(WingetMirrors.Ustc, m =>
        {
            Assert.False(string.IsNullOrWhiteSpace(m.Url));
            Assert.StartsWith("https://", m.Url, StringComparison.Ordinal);
        });
    }
}
