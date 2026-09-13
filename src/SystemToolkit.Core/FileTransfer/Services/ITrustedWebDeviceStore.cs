using SystemToolkit.Core.FileTransfer.Models;

namespace SystemToolkit.Core.FileTransfer.Services;

/// <summary>
/// 「已记住设备」长期凭据的持久化契约（2026-09-13 批次 P3 ⑲）。
/// <para>
/// 🔴 实现**必须**满足两条约束（写在契约里而不是实现里，因为这是安全要求不是实现细节）：
/// ① 落盘内容**不得包含明文令牌**——只存哈希；
/// ② 整个文件必须加密（本仓实现走 DPAPI CurrentUser，与音乐凭据 / Steam API Key 同范式）。
/// </para>
/// <para>
/// 读写是**整表**语义（<see cref="Load"/>/<see cref="Save"/>）：记录条数是个位到几十条，
/// 整表替换比做增量更新少一整类并发/部分写坏的坑。
/// </para>
/// </summary>
public interface ITrustedWebDeviceStore
{
    /// <summary>读取全部记录；文件缺失或损坏时返回空表（**不抛**：损坏不该让服务起不来）。</summary>
    IReadOnlyList<TrustedWebDevice> Load();

    /// <summary>整表写回（原子写）。</summary>
    void Save(IReadOnlyList<TrustedWebDevice> devices);
}
