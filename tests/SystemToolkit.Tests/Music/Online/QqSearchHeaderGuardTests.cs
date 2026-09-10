using System.Net.Http;
using SystemToolkit.Infrastructure.Music.Online;

namespace SystemToolkit.Tests.Music.Online;

/// <summary>
/// 事故守卫（2026-09-10）：QQ 搜索专用 UA 含括号，用 <c>Headers.Add</c> 添加会抛
/// <see cref="FormatException"/>（.NET 内建 User-Agent 语法解析拒绝），请求根本发不出去，
/// 现象是「搜索永远无结果、日志只有一条『搜索失败』」——排查成本极高。
/// 本测试固化两件事：① 该 UA 确实非法（所以必须用免校验通道）；② 免校验通道能加进去。
/// 反向验证：把 SearchAsync 改回 Headers.Add → 行为回归，本测试仍绿（它守的是"该用哪条通道"这一事实，
/// 真正的拦截靠代码注释 + Code Review；这里提供的是可执行的证据与文档）。
/// </summary>
public class QqSearchHeaderGuardTests
{
    [Fact]
    public void QqSearchUserAgent_IsNotValidHeaderToken_MustUseWithoutValidation()
    {
        // ① 证明它非法：Headers.Add 走严格解析 → 抛 FormatException
        var strict = new HttpRequestMessage();
        Assert.Throws<FormatException>(() => strict.Headers.Add("User-Agent", QQMusicOnlineClient.QqSearchUa));

        // ② 证明免校验通道可用（生产代码正是这么加的）
        var loose = new HttpRequestMessage();
        Assert.True(loose.Headers.TryAddWithoutValidation("User-Agent", QQMusicOnlineClient.QqSearchUa));
        Assert.Contains(QQMusicOnlineClient.QqSearchUa, loose.Headers.GetValues("User-Agent"));
    }
}
