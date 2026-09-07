using SystemToolkit.Core.FileTransfer.Models;

namespace SystemToolkit.Core.FileTransfer.Services;

/// <summary>
/// Web 文件服务接口：通过 HTTP 暴露共享目录，供手机浏览器访问。
/// 由 Kestrel 实现具体逻辑（定义在模块层，Core 仅声明契约）。
/// </summary>
public interface IFileWebServer : IAsyncDisposable
{
    /// <summary>Web 服务是否正在运行。</summary>
    bool IsRunning { get; }

    /// <summary>当前监听端口。</summary>
    int Port { get; }

    /// <summary>是否启用 HTTPS（自签证书）。<c>false</c> 表示仅 HTTP 明文，访问令牌可能被同网段嗅探。</summary>
    bool IsHttps { get; }

    /// <summary>本机访问 URL（localhost，供「打开网页」按钮使用）。</summary>
    string Url { get; }

    /// <summary>
    /// 局域网访问 URL（二维码内容），形如 http://&lt;局域网IP&gt;:&lt;端口&gt;/?c=&lt;配对码&gt;。
    /// <para>
    /// 这里放的是**短期配对码**而不是长期令牌：二维码极易被截图、被旁人拍到，
    /// 长期令牌一旦外泄即永久失守。手机扫码后用配对码换取令牌并自行保存，令牌不再出现在 URL 里。
    /// </para>
    /// 未运行时为空串。
    /// </summary>
    string LanUrl { get; }

    /// <summary>本次运行周期内的访问令牌（每次 StartAsync 重新生成）。</summary>
    string Token { get; }

    /// <summary>
    /// 当前有效的配对码（过期时惰性轮换）。未运行/未生成时为空串。
    /// </summary>
    string PairCode { get; }

    /// <summary>
    /// 启动 Web 服务。
    /// </summary>
    /// <param name="settings">传输配置。</param>
    /// <param name="shareDirectory">共享目录路径。</param>
    /// <param name="ct">取消令牌。</param>
    Task StartAsync(TransferSettings settings, string shareDirectory, CancellationToken ct = default);

    /// <summary>停止 Web 服务。</summary>
    Task StopAsync();

    /// <summary>
    /// 浏览共享目录。
    /// </summary>
    IEnumerable<FileShareEntry> Browse(string? relativePath = null);

    /// <summary>
    /// 添加文件到共享目录（供 Web 端上传写入）。
    /// </summary>
    Task WriteUploadedFileAsync(string fileName, Stream content, CancellationToken ct = default);
}
