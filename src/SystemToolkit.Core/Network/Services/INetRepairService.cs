namespace SystemToolkit.Core.Network.Services;

/// <summary>
/// 网络修复：步骤目录 + 单项执行 + 一键安全序列。
/// </summary>
public interface INetRepairService
{
    /// <summary>修复步骤目录（v1 共 5 项，含管理员 / 重启标记与命令预览）。</summary>
    IReadOnlyList<Models.RepairStepDescriptor> Steps { get; }

    /// <summary>
    /// 执行单项修复，返回退出码（0 成功）。
    /// </summary>
    /// <param name="stepId">步骤 Id（见 <see cref="Steps"/>）；未知 Id 抛 <see cref="ArgumentException"/>。</param>
    /// <param name="onLine">输出回调（含命令回显），供 VM 挂接日志面板。</param>
    /// <param name="adapter">
    /// 目标适配器名：renew / bounce 可省略——省略（或留空）时<b>自动选用首选已连接适配器</b>并在日志写明；
    /// 没有已连接适配器时直接失败（退出码 -1，不启动进程）。其余步骤忽略此参数。
    /// </param>
    /// <param name="ct">取消令牌；取消后中止当前步骤并向上传播（bounce 在禁用 / 启用间隙被取消时，网卡会停留在禁用态）。</param>
    Task<int> ExecuteAsync(string stepId, Action<string> onLine, string? adapter = null, CancellationToken ct = default);

    /// <summary>
    /// 一键安全序列：<c>flushdns → renew</c>，<b>失败即停</b>；排除一切需管理员 / 需重启项（设计文档 §4.3）。
    /// 返回<b>已成功执行</b>的步骤 Id 列表（空 = 第一步就失败）。序列结束后的「自动复诊断」由 ViewModel 触发。
    /// </summary>
    Task<IReadOnlyList<string>> RunSafeSequenceAsync(Action<string> onLine, CancellationToken ct = default);
}
