namespace SystemToolkit.Core.Network.Models;

/// <summary>诊断步骤状态。</summary>
public enum DiagStatus
{
    /// <summary>排队中，本轮尚未执行。</summary>
    Pending,
    /// <summary>正在执行。</summary>
    Running,
    /// <summary>执行完成且通过。</summary>
    Success,
    /// <summary>执行完成但未通过（原因见 Detail）。</summary>
    Failed,

    /// <summary>前置条件不满足（如无网关可测），本轮未执行。</summary>
    Skipped,
}

/// <summary>
/// 诊断步的**机器可读标识**（跨层契约：Core 产出 / UI 对位刷新 / 结论规则共用同一组常量）。
/// <para>
/// 🔴 v20-NM-🟠-1（2026-09-16 修复）：此前 <see cref="DiagStepResult.Step"/> 直接携带**中文展示文案**
/// （"适配器"/"网关"/"公网"/"DNS 解析"/"丢包量化"），Core 与 UI 各自硬编码字面量、靠注释约定"逐字一致"：
/// 任何一侧改字（哪怕只改一个字）都会让 UI 的 <c>Steps.First(s =&gt; s.Step == r.Step)</c> 抛
/// <c>InvalidOperationException</c>，用户看到的是面向开发者的 "Sequence contains no matching element"。
/// </para>
/// <para>
/// 现改为：<b>标识</b>用本类的 ASCII 常量（跨层唯一真源，编译期即可防错），
/// <b>展示文案</b>由 UI 侧按标识映射（见 NetManager 的 <c>NetDiagnosticsTabViewModel</c>）。
/// 标识与文案解耦后，改文案不再有触碰契约的风险。
/// </para>
/// </summary>
public static class DiagStep
{
    /// <summary>① 适配器（是否有已连接网卡）。</summary>
    public const string Adapter = "adapter";
    /// <summary>② 网关（默认网关可达性）。</summary>
    public const string Gateway = "gateway";
    /// <summary>③ 公网（固定 IP 可达性，判 ICMP 与链路）。</summary>
    public const string PublicNet = "public";
    /// <summary>④ DNS 解析（域名可否解析）。</summary>
    public const string Dns = "dns";
    /// <summary>⑤ 丢包量化（10 包统计，区分"断网"与"网烂"）。</summary>
    public const string Quantify = "quantify";
}

/// <summary>一步诊断的结果快照。</summary>
public sealed record DiagStepResult(
    string Step,
    DiagStatus Status,
    string Detail,
    long ElapsedMs)
{
    /// <summary>
    /// 步骤的展示文案（由 UI 按 <see cref="Step"/> 标识映射；Core 不预设中文，避免"文案即契约"）。
    /// 默认空串 = "本层不提供文案"，UI 必须自行兜底。
    /// </summary>
    public string Label { get; init; } = "";
}
