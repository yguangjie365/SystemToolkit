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
        // 行级填充跨度 = min(字符估算演唱时长, 到下一行间隔)。给足字符使估算跨度≥间隔(10s)，
        // 则跨度=间隔=10，now=5 落在中点 → smoothstep≈0.5
        var line = new LyricLine { Time = 0, Duration = 10, Text = new string('x', 40), CharCount = 40 };
        var next = new LyricLine { Time = 10, Duration = 10, Text = "y" };

        // now = 5（含 +0.02 补偿）→ 略过半 → smoothstep 略大于 0.5
        double p = LyricParser.GetLineProgress(line, next, 5);

        Assert.InRange(p, 0.5, 0.55);
    }

    [Fact]
    public void GetLineProgress_ShortLineBeforeLongGap_CompletesBeforeGap()
    {
        // P0 修复回归：短行(4字→估算1.5s)后接长停顿(下一行在 20s)，
        // 填充应在 ~1.5s 内填满并保持，不把停顿摊进本行（旧行为会爬满 20s）
        var line = new LyricLine { Time = 0, Duration = 20, Text = "abcd", CharCount = 4 };
        var next = new LyricLine { Time = 20, Duration = 5, Text = "y" };

        Assert.Equal(1d, LyricParser.GetLineProgress(line, next, 5)); // 停顿中段：已填满
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

    // ════════ ParseYrc 逐字歌词（2026-09-09 恢复；算法对照 NexBox parseYrc） ════════

    [Fact]
    public void ParseYrc_ParsesWordsAndCharOffsets()
    {
        // 行头 [start,dur] + 圆括号词标签（网易 YRC；绝对时间戳）
        const string yrc = "[10000,4000](10000,800,0)Hello (10800,1200,0)world";

        LyricDocument doc = LyricParser.ParseYrc(yrc);

        LyricLine line = Assert.Single(doc.Lines);
        Assert.Equal(10.0, line.Time);
        Assert.Equal("Hello world", line.Text);
        Assert.NotNull(line.Words);
        Assert.Equal(2, line.Words!.Count);
        // 词1 "Hello "：区间 [0,6)，起点 10s
        Assert.Equal(0, line.Words[0].C0);
        Assert.Equal(6, line.Words[1].C0);
        Assert.Equal("Hello ", line.Words[0].Text);
    }

    [Fact]
    public void ParseYrc_AcceptsQrcAngleBracketTags()
    {
        // QQ QRC：尖括号词标签 + 相对行头的时间偏移
        const string yrc = "[2000,3000]<0,500,0>你 <500,500,0>好";

        LyricDocument doc = LyricParser.ParseYrc(yrc);

        LyricLine line = Assert.Single(doc.Lines);
        Assert.Equal("你 好", line.Text);
        Assert.NotNull(line.Words);
        // 相对偏移 0 → 绝对 2s；500 → 2.5s
        Assert.Equal(2.0, line.Words![0].T);
        Assert.Equal(2.5, line.Words[1].T);
    }

    [Fact]
    public void GetLineProgress_WordBranch_TracksCharPosition()
    {
        const string yrc = "[0,4000](0,1000,0)AB(1000,1000,0)CD(2000,2000,0)EF";
        LyricDocument doc = LyricParser.ParseYrc(yrc);
        LyricLine line = doc.Lines[0];

        // 0.5s（+0.03 补偿=0.53）：词1 "AB"（字符区间 [0,2)）唱到 53% → 1.06/6
        double p1 = LyricParser.GetLineProgress(line, null, 0.5);
        Assert.InRange(p1, 0.175, 0.179);

        // 1.5s：词2 "CD"（字符区间 [2,4)）唱到 53% → 3.06/6
        double p2 = LyricParser.GetLineProgress(line, null, 1.5);
        Assert.InRange(p2, 0.505, 0.511);

        // 5s：整行唱完
        Assert.Equal(1.0, LyricParser.GetLineProgress(line, null, 5.0));
    }

    [Fact]
    public void GetLineProgress_NoWords_FallsBackToSmoothstep()
    {
        // P0 语义：跨度 = min(字符估算演唱时长, 间隔)。12 字→估算 4.2s ≥ 间隔(Duration=4) → 跨度=4，t=2 落中点
        LyricLine line = new() { Time = 0, Duration = 4, Text = new string('a', 12), CharCount = 12 };

        double p = LyricParser.GetLineProgress(line, null, 2.0);

        Assert.InRange(p, 0.49, 0.51); // smoothstep 在 50% 处 ≈ 0.5
    }

    // ════════ 零宽不可见字符（2026-09-09 截图实证：纯零宽行渲染成空白容器堆积成大块空白） ════════

    [Fact]
    public void Parse_ZeroWidthOnlyLine_IsDropped()
    {
        // 纯零宽字符行（Trim/\s 都不匹配它们）必须被丢弃，否则渲染为空白行容器
        string lrc = "[00:01.00]\u200b\u200b\n[00:05.00]真实歌词\n[00:09.00]\u200b";

        List<LyricLine> lines = LyricParser.ParseTimedLines(lrc);

        LyricLine line = Assert.Single(lines);
        Assert.Equal("真实歌词", line.Text);
    }

    [Fact]
    public void Parse_ZeroWidthCharsInsideText_AreStripped()
    {
        string lrc = "[00:01.00]歌\u200b词\u200b行";

        List<LyricLine> lines = LyricParser.ParseTimedLines(lrc);

        LyricLine line = Assert.Single(lines);
        Assert.Equal("歌词行", line.Text);
    }
}
