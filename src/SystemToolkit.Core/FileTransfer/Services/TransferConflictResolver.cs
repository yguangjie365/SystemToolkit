using SystemToolkit.Core.FileTransfer.Models;

namespace SystemToolkit.Core.FileTransfer.Services;

/// <summary>
/// 落定路径与同名冲突的**唯一裁决处**（两条通道共用）。
/// <para>
/// 🔴 为什么必须收敛到一处：改名前这段逻辑在本仓存在**两份**（电脑通道 <c>FileTransferService.GetUniqueDestination</c>
/// 与手机通道 <c>FileWebServer.GetUniqueDestination</c>）。冲突策略一旦只改一份，两条通道就会出现
/// 「同一次操作、两台设备上结果不同」——用户无从理解，也无从信任。
/// </para>
/// <para>
/// 线程语义：命名探测（<c>File.Exists</c> → 返回路径）与随后的 <c>File.Move</c> 之间存在竞态窗口，
/// 调用方必须自行处理（电脑通道由单连接串行握手保证，手机通道有 Move 失败换序号重试）。
/// </para>
/// </summary>
public static class TransferConflictResolver
{
    /// <summary>
    /// 按策略解析落定计划。
    /// <para>
    /// <see cref="TransferConflictPolicy.Ask"/> **不应直接传进来**：询问属于交互层职责，
    /// 调用方须先把「询问」解析成具体策略（用户选择，或无法询问时的降级值）。
    /// 这里为防御起见把 Ask 当作 Rename 处理——与降级口径一致，不会因漏解析而误覆盖文件。
    /// </para>
    /// </summary>
    /// <param name="directory">目标目录。</param>
    /// <param name="fileName">目标文件名（须已做安全化处理）。</param>
    /// <param name="policy">已解析的策略（非 Ask）。</param>
    public static ConflictPlan Resolve(string directory, string fileName, TransferConflictPolicy policy)
    {
        string target = Path.Combine(directory, fileName);
        if (!File.Exists(target))
        {
            return new ConflictPlan(ConflictResolution.Fresh, target);
        }

        return policy switch
        {
            TransferConflictPolicy.Skip => new ConflictPlan(ConflictResolution.Skip, target),
            TransferConflictPolicy.Overwrite => new ConflictPlan(ConflictResolution.Overwrite, target),
            _ => new ConflictPlan(ConflictResolution.Renamed, GetUniqueDestination(directory, fileName)),
        };
    }

    /// <summary>目标目录内取不冲突的落定路径：同名时追加 " (n)" 序号，绝不覆盖已有文件。</summary>
    public static string GetUniqueDestination(string directory, string fileName)
    {
        string candidate = Path.Combine(directory, fileName);
        if (!File.Exists(candidate))
        {
            return candidate;
        }

        string ext = Path.GetExtension(fileName);
        string stem = Path.GetFileNameWithoutExtension(fileName);
        for (int i = 1; ; i++)
        {
            candidate = Path.Combine(directory, $"{stem} ({i}){ext}");
            if (!File.Exists(candidate))
            {
                return candidate;
            }
        }
    }

    /// <summary>策略的中文标签（UI 下拉与日志共用，避免各处自造措辞）。</summary>
    public static string Describe(TransferConflictPolicy policy) => policy switch
    {
        TransferConflictPolicy.Ask => "询问",
        TransferConflictPolicy.Skip => "跳过",
        TransferConflictPolicy.Overwrite => "覆盖",
        _ => "自动改名",
    };

    /// <summary>单个已解析动作的中文说明（确认门弹窗要如实告诉用户"这一份会发生什么"）。</summary>
    public static string Describe(ConflictResolution resolution) => resolution switch
    {
        ConflictResolution.Fresh => "无同名文件",
        ConflictResolution.Renamed => "同名，将改名保存",
        ConflictResolution.Overwrite => "同名，将覆盖原文件",
        ConflictResolution.Skip => "同名，将跳过不接收",
        _ => resolution.ToString(),
    };
}
