using System.Text.RegularExpressions;
using SystemToolkit.Core.FileTransfer.Services.Protocol;

namespace SystemToolkit.Tests.Architecture;

/// <summary>
/// 协议消息类型覆盖守卫（反模式 ㊱ 机器化；v8 审查 🟡-12 沉淀，2026-09-14）。
/// <para>
/// <b>病根</b>：枚举里出现一个**既无生产者也无消费者**的成员时，代码读起来像"这条路已实现"，
/// 而实际收发两侧都会把它当未知类型静默丢弃 —— 属"静默失败形状"，与 B10/🟠-7 那批
/// "名单制守卫 = 静默不检测"同源。v8 报告用人工检索才发现 <c>ChunkAck</c> 是唯一这样的成员
/// （全 <c>src/</c> 零引用）。本守卫把这条人工检索变成机器判据。
/// </para>
/// <para>
/// <b>判据</b>：<see cref="TransferMessageType"/> 的每个成员，必须在 <c>src/</c> 里至少出现一次
/// <c>TransferMessageType.&lt;成员名&gt;</c>（生产者或消费者皆可），**除非**它被显式登记进
/// <see cref="ReservedNotImplemented"/> 并写明理由。新增成员忘接线 ⇒ 直接红。
/// </para>
/// <para>
/// 🔴 反向验证：把 <c>ChunkAck</c> 从 <see cref="ReservedNotImplemented"/> 移除 → 本用例变红；
/// 或在枚举里加一个假成员 → 同样变红。
/// </para>
/// </summary>
public class ProtocolMessageTypeCoverageGuardTests
{
    /// <summary>
    /// 显式登记的「设计预留、当前未实现」成员（**必须写明理由**，空清单合法）。
    /// <para>登记 = 声明"它无引用是**有意**的"；不登记 = 断言"它必须有引用"。</para>
    /// </summary>
    private static readonly Dictionary<string, string> ReservedNotImplemented = new(StringComparer.Ordinal)
    {
        ["ChunkAck"] =
            "原始协议 8 型之一（Docs/30-模块设计/05 记载），实现侧改用 HandshakeAck 断点 + CompleteAck 全量哈希校验，"
            + "未采用逐分片确认；保留是为不破坏已声明的协议面（JsonStringEnumConverter 按名序列化）",
    };

    /// <summary>协议枚举的**全部**成员（反射取，不手抄 —— 手抄的清单会随枚举漂移）。</summary>
    private static readonly string[] AllMembers =
        Enum.GetNames<TransferMessageType>();

    /// <summary><c>src</c> 下全部 .cs 的合并文本（两条用例共用，只读一次）。</summary>
    private static readonly string SrcText = string.Concat(
        Directory.EnumerateFiles(Path.Combine(RepoRoot(), "src"), "*.cs", SearchOption.AllDirectories)
            .Where(p => !p.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                && !p.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .Select(File.ReadAllText));

    /// <summary>限定引用判据：<c>TransferMessageType.&lt;成员名&gt;</c>（枚举定义处是裸成员名，不会误命中自身）。</summary>
    private static bool IsReferencedInSrc(string member) =>
        Regex.IsMatch(SrcText, @"TransferMessageType\." + Regex.Escape(member) + @"\b");

    [Fact]
    public void EveryMessageType_MustBeReferencedInSrc_OrExplicitlyReserved()
    {
        var unreferenced = new List<string>();
        foreach (string member in AllMembers)
        {
            if (!IsReferencedInSrc(member) && !ReservedNotImplemented.ContainsKey(member))
            {
                unreferenced.Add(member);
            }
        }

        Assert.True(
            unreferenced.Count == 0,
            "以下协议消息类型在 src/ 里**零引用**（既无生产者也无消费者）——"
            + "要么接线，要么登记进 ProtocolMessageTypeCoverageGuardTests.ReservedNotImplemented 并写明理由：\n"
            + string.Join("\n", unreferenced));
    }

    /// <summary>
    /// 预留清单**双向**核对：登记的成员必须真的存在且真的零引用。
    /// <para>
    /// 否则清单会腐化成"遮羞布"——成员接线后还挂在预留清单里，下一次新增无引用成员时
    /// 就无法靠"清单里没有"判红。反向验证：给某个已接线成员（如 <c>Cancel</c>）加一条预留登记 → 本用例变红。
    /// </para>
    /// </summary>
    [Fact]
    public void ReservedList_HasNoStaleEntries()
    {
        var problems = new List<string>();
        foreach (string member in ReservedNotImplemented.Keys)
        {
            if (!AllMembers.Contains(member, StringComparer.Ordinal))
            {
                problems.Add($"{member}：枚举里已无此成员（删除/改名？请同步清理预留清单）");
                continue;
            }

            if (IsReferencedInSrc(member))
            {
                problems.Add($"{member}：已接线（src/ 里有引用），应从预留清单移除");
            }
        }

        Assert.True(problems.Count == 0, string.Join("\n", problems));
    }

    /// <summary>
    /// 判据自检：正则必须能区分"限定引用"与"枚举定义处的裸成员名"，且不放过前缀相同的成员。
    /// <para>只断言上面两条全绿，无法排除"判据恒真/恒假"。</para>
    /// </summary>
    [Fact]
    public void Detector_DistinguishesQualifiedReferenceFromDeclaration()
    {
        Assert.Matches(new Regex(@"TransferMessageType\.Cancel\b"), "case TransferMessageType.Cancel:");
        Assert.Matches(new Regex(@"TransferMessageType\.ChunkAck\b"), "Type = TransferMessageType.ChunkAck,");
        Assert.DoesNotMatch(new Regex(@"TransferMessageType\.Cancel\b"), "    Cancel,"); // 定义处裸名不算引用
        Assert.DoesNotMatch(new Regex(@"TransferMessageType\.Cancel\b"), "TransferMessageType.CancelX"); // 词边界
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
}
