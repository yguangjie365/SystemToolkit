using SystemToolkit.Core.Music.Models;
using SystemToolkit.Core.Music.Services;
using Xunit;

namespace SystemToolkit.Tests.Music;

/// <summary>
/// 🟠-17（UI-v2 2026-09-19）：歌词入口的零宽字符收口。
/// <para>
/// 行级清理（<c>FinalizeLineDurations</c>）只覆盖**有时间标签的行**；
/// <c>Parse</c> 的无时间标签兜底分支（<c>PlainText</c>）与 <c>translation</c> 原先不过滤 ⇒
/// 重影取句可能整段取到零宽、行宽计算偏差。本组用例锁定入口即剥。
/// </para>
/// </summary>
public sealed class LyricParserInvisibleEntryTests
{
    private const string ZWSP = "\u200b";   // ZERO WIDTH SPACE
    private const string ZWNJ = "\u200c";   // ZERO WIDTH NON-JOINER
    private const string WJ = "\u2060";     // WORD JOINER

    [Fact]
    public void Parse_PlainTextPath_StripsInvisible()
    {
        // 无时间标签 → 走 PlainText 兜底（行级清理覆盖不到）
        LyricDocument doc = LyricParser.Parse("前" + ZWSP + "奏" + ZWNJ + "开" + WJ + "始", null);

        Assert.Equal("前奏开始", doc.PlainText);
        Assert.Empty(doc.Lines);
    }

    [Fact]
    public void Parse_TimedLines_StripsInvisible_AndDropsInvisibleOnlyLine()
    {
        LyricDocument doc = LyricParser.Parse(
            "[00:01.00]a" + ZWSP + "b\n[00:02.00]" + ZWSP + "\n[00:03.00]c", null);

        Assert.Equal(2, doc.Lines.Count);
        Assert.Equal("ab", doc.Lines[0].Text);
        Assert.Equal("c", doc.Lines[1].Text);
    }

    [Fact]
    public void Parse_Translation_StripsInvisible()
    {
        LyricDocument doc = LyricParser.Parse("[00:01.00]hello", "[00:01.00]你" + ZWSP + "好");

        Assert.Single(doc.Lines);
        Assert.Equal("你好", doc.Lines[0].Translation);
    }

    [Fact]
    public void ParseYrc_StripsInvisible()
    {
        // 行头 [startMs,durMs]，词标签 (ws,wd,0)词
        LyricDocument doc = LyricParser.ParseYrc("[" + ZWSP + "1000,500](0,500,0)你" + ZWSP + "好", null);

        Assert.Single(doc.Lines);
        Assert.Equal("你好", doc.Lines[0].Text);
    }
}
