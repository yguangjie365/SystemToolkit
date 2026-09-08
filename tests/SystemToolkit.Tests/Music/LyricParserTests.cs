using SystemToolkit.Core.Music.Models;
using SystemToolkit.Core.Music.Services;

namespace SystemToolkit.Tests.Music;

/// <summary>
/// 歌词解析与进度算法（MUSIC-3）。重点锁定：
/// ① 四个算法与旧工程逐字一致（行级 smoothstep / 滚动三段门限 / CalcActiveIndex / 时长 clamp）；
/// ② 相较旧实现的修复——无时间标签时回填 <see cref="LyricDocument.PlainText"/>
///   （旧代码返回空列表，UI 误报「暂无歌词」）。
/// </summary>
public class LyricParserTests
{
    private const string SampleLrc =
        "[00:00.00]第一行\n[00:05.50]第二行\n[00:10.25]第三行\n";

    // ════════ 解析 ════════

    [Fact]
    public void Parse_TimedLrc_ReturnsSortedLines()
    {
        LyricDocument doc = LyricParser.Parse(SampleLrc, null, LyricSource.SidecarFile);

        Assert.Equal(LyricSource.SidecarFile, doc.Source);
        Assert.True(doc.IsTimed);
        Assert.Equal(3, doc.Lines.Count);
        Assert.Equal("第一行", doc.Lines[0].Text);
        Assert.Equal(0d, doc.Lines[0].Time);
        Assert.Equal(5.5d, doc.Lines[1].Time);
        Assert.Null(doc.PlainText);
    }

    [Fact]
    public void Parse_OneLineMultipleTags_ExpandsToMultipleLines()
    {
        LyricDocument doc = LyricParser.Parse("[00:01.00][00:03.00]副歌\n[00:06.00]结束");

        // 两个标签同文本 → 展开为两行"副歌"；第三行是另一句
        Assert.Equal(3, doc.Lines.Count);
        Assert.Equal("副歌", doc.Lines[0].Text);
        Assert.Equal("副歌", doc.Lines[1].Text);
        Assert.Equal("结束", doc.Lines[2].Text);
        Assert.Equal(1d, doc.Lines[0].Time);
        Assert.Equal(3d, doc.Lines[1].Time);
        Assert.Equal(6d, doc.Lines[2].Time);
    }

    [Fact]
    public void Parse_WithTranslation_MatchesByTimestamp()
    {
        const string lrc = "[00:01.00]Hello\n";
        const string trans = "[00:01.00]你好\n";

        LyricDocument doc = LyricParser.Parse(lrc, trans);

        Assert.Equal("你好", doc.Lines[0].Translation);
    }

    [Fact]
    public void Parse_NoTimeTag_FallsBackToPlainText()
    {
        // 🔴 旧实现在此返回空列表 → UI 误报「暂无歌词」
        const string plain = "这是一段没有时间标签的歌词\n第二行";

        LyricDocument doc = LyricParser.Parse(plain);

        Assert.Empty(doc.Lines);
        Assert.False(doc.IsTimed);
        Assert.Equal(plain.Trim(), doc.PlainText);
        Assert.False(doc.IsEmpty);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void Parse_EmptyInput_ReturnsNone(string? input)
    {
        LyricDocument doc = LyricParser.Parse(input);

        Assert.True(doc.IsEmpty);
        Assert.Equal(LyricSource.None, doc.Source);
    }

    [Fact]
    public void Parse_DurationInferredFromNextLine()
    {
        LyricDocument doc = LyricParser.Parse(SampleLrc);

        Assert.Equal(5.5d, doc.Lines[0].Duration, 3); // 下一行 5.5 - 0
        Assert.Equal(4.75d, doc.Lines[1].Duration, 3); // 10.25 - 5.5
        Assert.Equal(4.8d, doc.Lines[2].Duration, 3);  // 末行用默认 4.8
    }

    [Fact]
    public void Parse_DurationClamped_ToMinAndMax()
    {
        // 相邻过近（0.1s）→ clamp 到 0.45；间隔过大（30s）→ clamp 到 12
        LyricDocument doc = LyricParser.Parse("[00:00.00]A\n[00:00.10]B\n[00:30.00]C");

        Assert.Equal(0.45d, doc.Lines[0].Duration, 3);
        Assert.Equal(12d, doc.Lines[1].Duration, 3);
    }

    // ════════ 进度算法（与旧工程逐字一致） ════════

    [Fact]
    public void GetLineProgress_NullLine_ReturnsZero()
        => Assert.Equal(0d, LyricParser.GetLineProgress(null, null, 5));

    [Fact]
    public void GetLineProgress_SmoothstepAtMidpoint_EqualsHalf()
    {
        var line = new LyricLine { Time = 0, Duration = 10, Text = "x" };
        var next = new LyricLine { Time = 10, Duration = 10, Text = "y" };

        // now = 5（含 +0.02 补偿）→ 略过半 → smoothstep 略大于 0.5
        double p = LyricParser.GetLineProgress(line, next, 5);

        Assert.InRange(p, 0.5, 0.55);
    }

    [Fact]
    public void GetLineProgress_BeforeStart_IsZero()
    {
        var line = new LyricLine { Time = 10, Duration = 5, Text = "x" };

        Assert.Equal(0d, LyricParser.GetLineProgress(line, null, 1));
    }

    [Fact]
    public void GetLineProgress_AfterEnd_IsOne()
    {
        var line = new LyricLine { Time = 0, Duration = 4, Text = "x" };
        var next = new LyricLine { Time = 4, Duration = 4, Text = "y" };

        Assert.Equal(1d, LyricParser.GetLineProgress(line, next, 99));
    }

    [Fact]
    public void CalculateScrollOffset_ThreePhases()
    {
        const double limit = 100;

        Assert.Equal(0d, LyricParser.CalculateScrollOffset(0.05, limit));   // 前 8% 静止
        Assert.Equal(0d, LyricParser.CalculateScrollOffset(-1, limit));     // 负值钳到 0
        Assert.Equal(-limit, LyricParser.CalculateScrollOffset(0.9, limit)); // 78% 后保持最远
        double mid = LyricParser.CalculateScrollOffset(0.43, limit);         // 中段缓动
        Assert.InRange(mid, -limit, 0);
        Assert.Equal(0d, LyricParser.CalculateScrollOffset(0.5, 0));        // limit<=0 恒 0
    }

    [Fact]
    public void CalcActiveIndex_PicksLastLineAtOrBeforeCurrent()
    {
        List<LyricLine> lines = LyricParser.Parse(SampleLrc).Lines;

        Assert.Equal(0, LyricParser.CalcActiveIndex(lines, 0));
        Assert.Equal(0, LyricParser.CalcActiveIndex(lines, 5.4));
        Assert.Equal(1, LyricParser.CalcActiveIndex(lines, 5.6));
        Assert.Equal(2, LyricParser.CalcActiveIndex(lines, 99));
    }

    [Fact]
    public void CalcActiveIndex_BeforeFirstLine_ReturnsZero()
    {
        // time 0 且 current -1：没有 ≤ now 的行 → 返回 0（未来行尚未开始）
        List<LyricLine> lines = LyricParser.Parse(SampleLrc).Lines;

        Assert.Equal(0, LyricParser.CalcActiveIndex(lines, -1));
    }

    [Fact]
    public void CalcActiveIndex_EmptyList_ReturnsMinusOne()
        => Assert.Equal(-1, LyricParser.CalcActiveIndex([], 10));

    [Fact]
    public void CalcActiveIndex_Tolerance_50ms()
    {
        // 恰在 0.05s 容差内应算作已开始
        List<LyricLine> lines = LyricParser.Parse("[00:05.00]A\n[00:09.00]B").Lines;

        Assert.Equal(0, LyricParser.CalcActiveIndex(lines, 4.97));
    }
}
