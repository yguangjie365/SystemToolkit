namespace SystemToolkit.Core.Overview.Models;

/// <summary>
/// 概览输出的**文案契约**：面板标签与行键的字面量集中在此，采集侧（<c>OverviewService.BuildOsPanel</c>）
/// 与消费侧（<c>OverviewViewModel</c> 里"按文案定位面板/行"的抽取逻辑）**共用同一常量**。
/// <para>
/// 🟠 V14-O5（2026-09-14 登记）：消费侧此前写的是裸字面量（<c>p.Label == "操作系统"</c>），
/// 与采集侧之间只是**隐式约定** —— 上游改文案，消费侧静默失效（概览副标题里的「操作系统 /
/// 运行时长」整段消失），且没有任何守卫会红。提为常量后改文案只需改这一处，两侧自动跟随。
/// </para>
/// <para>
/// 🔴 改动本类的值 = 改动用户可见文案。行键同时进<b>磁盘快照缓存</b>（只改键名不改
/// <see cref="OverviewItem"/> 的序列化契约）：改键名后旧缓存里查不到该行，表现为"副标题少一段"，
/// 直到下一次全量采集覆盖缓存。
/// </para>
/// </summary>
public static class OverviewLabels
{
    /// <summary>系统信息面板的标签（同时也是该面板内「操作系统」行的行键 —— 采集侧两处用同一字面量）。</summary>
    public const string OperatingSystem = "操作系统";

    /// <summary>系统信息面板内「运行时长」行的行键。</summary>
    public const string Uptime = "运行时长";
}
