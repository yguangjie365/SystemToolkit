using System.Text;
using SystemToolkit.Core.Network.Services;

namespace SystemToolkit.Tests;

/// <summary>
/// 输出解码自适应择优测试（真机修复：netsh 与 ipconfig 重定向输出编码不一致：
/// netsh 可 UTF-8 解析、ipconfig 是 GBK——固定单一编码必然一边乱码）。
/// 核心区分度：GBK 字节被 UTF-8 解码必产生 U+FFFD 替换符，正确的 936 解码为零；
/// 反向（UTF-8 字节被 GBK 解）因 GBK 覆盖面广可能零替换符——靠严格 UTF-8 校验优先兜住。
/// 自旧工程移植，L15 英文命名。
/// </summary>
public class OutputDecoderTests
{
    // cp936 可用性依赖 CodePagesEncodingProvider 注册（CommandRunner 静态构造也会注册，
    // 但 xUnit 测试类的静态初始化时序不可依赖——本类自带注册，完全自包含）
    static OutputDecoderTests()
    {
        try
        {
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        }
        catch (Exception)
        {
            // 重复注册等场景静默忽略
        }
    }

    [Fact]
    public void GbkBytes_Picks936_DecodesChineseCorrectly()
    {
        // 真机 ipconfig 输出形态：GBK 编码的「已成功刷新 DNS 解析程序缓存」
        byte[] gbk = Encoding.GetEncoding(936).GetBytes("已成功刷新 DNS 解析程序缓存");

        // 真机候选：OEMCP=65001（beta UTF-8 场景）→ ACP=936 → 去重后 [UTF-8, GBK]
        IReadOnlyList<Encoding> candidates = OutputDecoder.BuildCandidates(65001, 936);

        Encoding best = OutputDecoder.PickBest(gbk, candidates);

        Assert.Equal(Encoding.GetEncoding(936), best);
        Assert.Equal("已成功刷新 DNS 解析程序缓存", best.GetString(gbk));
    }

    [Fact]
    public void GbkBytes_Utf8Decode_ProducesReplacements()
    {
        byte[] gbk = Encoding.GetEncoding(936).GetBytes("已成功刷新 DNS 解析程序缓存");

        Assert.True(OutputDecoder.CountReplacements(Encoding.UTF8.GetString(gbk)) > 0,
            "GBK 中文被 UTF-8 解码应产生替换符——否则择优失去区分度");
        Assert.Equal(0, OutputDecoder.CountReplacements(Encoding.GetEncoding(936).GetString(gbk)));
    }

    [Fact]
    public void Utf8Data_WinsEvenWhen936First_BlindSpotRegression()
    {
        // 【真机修复·二轮】FFFD 计数法盲区：GBK 解 UTF-8 中文常零替换符但全是错字
        //（netsh 中文标签变错字 → 解析全失败，实测踩过）——严格 UTF-8 校验必须压过候选顺序
        byte[] utf8 = Encoding.UTF8.GetBytes("TCP 全局参数\n接收窗口自动调节级别    : normal");

        IReadOnlyList<Encoding> candidates = OutputDecoder.BuildCandidates(936, 936); // [936, UTF-8]，936 在前
        Encoding best = OutputDecoder.PickBest(utf8, candidates);

        Assert.Equal(Encoding.UTF8, best);
        Assert.Contains("接收窗口自动调节级别", best.GetString(utf8));
    }

    [Fact]
    public void IsValidUtf8_ValidChinesePasses_GbkAndMalformedRejected()
    {
        Assert.True(OutputDecoder.IsValidUtf8(Encoding.UTF8.GetBytes("TCP 全局参数: normal")));
        Assert.True(OutputDecoder.IsValidUtf8([]));
        Assert.True(OutputDecoder.IsValidUtf8("plain ascii"u8.ToArray()));

        // GBK「网」= CD F8：CD 是合法双字节起点但 F8 非法 continuation → 整体非法
        Assert.False(OutputDecoder.IsValidUtf8(new byte[] { 0xCD, 0xF8 }));
        // 孤立 continuation byte
        Assert.False(OutputDecoder.IsValidUtf8(new byte[] { 0x80 }));
        // 过长编码 C0
        Assert.False(OutputDecoder.IsValidUtf8(new byte[] { 0xC0, 0xAF }));
        // 代理区 ED A0
        Assert.False(OutputDecoder.IsValidUtf8(new byte[] { 0xED, 0xA0, 0x80 }));
        // 越界 U+10FFFF（F4 90+）
        Assert.False(OutputDecoder.IsValidUtf8(new byte[] { 0xF4, 0x90, 0x80, 0x80 }));
    }

    [Fact]
    public void AsciiOutput_AnyCandidate_YieldsReplacementFreeText()
    {
        byte[] ascii = "DNS request timed out."u8.ToArray();

        Encoding best = OutputDecoder.PickBest(ascii, OutputDecoder.BuildCandidates(936, 936));

        Assert.Equal(0, OutputDecoder.CountReplacements(best.GetString(ascii)));
    }

    [Fact]
    public void EmptyOutput_ReturnsFirstCandidate_NoThrow()
    {
        Encoding best = OutputDecoder.PickBest([], OutputDecoder.BuildCandidates(936, 936));

        Assert.NotNull(best);
    }

    [Fact]
    public void BuildCandidates_Dedupes_Utf8AlwaysLast()
    {
        IReadOnlyList<Encoding> candidates = OutputDecoder.BuildCandidates(936, 936);

        Assert.Equal(2, candidates.Count);              // 936 + UTF-8（936 去重）
        Assert.Equal(Encoding.UTF8, candidates[^1]);    // UTF-8 兜底在末位
    }

    [Fact]
    public void ForEachLine_SplitsLines_TrimsCr_SkipsBlank()
    {
        // 【真机反馈】ipconfig /renew 输出含大量空行——空行不回调，日志不再满屏空内容
        var lines = new List<string>();
        OutputDecoder.ForEachLine("第一行\r\n\r\n\n   \n第二行\n第三行", lines.Add);

        Assert.Equal(new[] { "第一行", "第二行", "第三行" }, lines);
    }
}
