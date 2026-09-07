using SystemToolkit.Core.Music.Models;

namespace SystemToolkit.Core.Music.Services;

/// <summary>
/// 音频标签读取契约：把第三方标签库（当前为 TagLibSharp 2.3.0）隔离在一个可替换的实现后面。
/// </summary>
/// <remarks>
/// <para><b>为什么要这层契约</b>：① 测试可用假件覆盖「MP3/FLAC/无标签/损坏文件」四条路径，
/// 不必在测试项目里塞真实音频二进制；② 标签库可换——本轮就刚把 ATL 换成 TagLibSharp
/// （ATL 的传递依赖 <c>Ude.NetStandard</c> 含 GPL 授权选项，触碰许可证红线，见 ADR-002 §5.4），
/// 有这层契约时换库只动一个文件。</para>
/// <para><b>线程模型</b>：实现必须可被多线程并发调用（<c>LocalMusicScanner</c> 用
/// <c>Parallel.ForEachAsync</c>，并行度上限 8），因此不得持有跨调用的可变状态。</para>
/// <para><b>同步阻塞</b>：TagLibSharp 没有异步 API，故本契约的方法都是同步的。
/// 调用方必须自己用 <c>Task.Run</c> 卸载到线程池，<b>不得</b>在 UI 线程直接调用。</para>
/// </remarks>
public interface IMusicTagReader
{
    /// <summary>
    /// 可解析的文件扩展名集合（含前导点，小写，如 <c>.mp3</c>）。
    /// </summary>
    /// <remarks>
    /// 格式清单只在<b>这里</b>定义一份：扫描器据此过滤文件，避免出现
    /// 「扫描器认这个扩展名、标签库不认」的错配（旧工程把清单硬编码在扫描器里，
    /// 与 ATL 的实际能力各写一份）。比较须用 <see cref="StringComparer.OrdinalIgnoreCase"/>。
    /// </remarks>
    IReadOnlySet<string> SupportedExtensions { get; }

    /// <summary>
    /// 读取一个音频文件的标签与音频属性（<b>不</b>读取图片字节）。
    /// </summary>
    /// <param name="filePath">音频文件绝对路径。</param>
    /// <returns>
    /// 成功时 <see cref="MusicTagReadResult.Success"/> 为 true 且 <see cref="MusicTagReadResult.Tags"/> 非空；
    /// 文件损坏、格式不支持、IO/权限失败时为 false 并带 <see cref="MusicTagReadResult.FailureReason"/>。
    /// <b>不抛异常</b>——大批量扫描时读到坏文件是预期内结果。
    /// </returns>
    MusicTagReadResult Read(string filePath);

    /// <summary>
    /// 按需读取一个音频文件的内嵌封面（惰性：只有 UI 真的要显示时才调）。
    /// </summary>
    /// <param name="filePath">音频文件绝对路径。</param>
    /// <returns>
    /// 封面字节与 MIME 类型；文件无封面、或读取失败时返回 null（实现须记日志，
    /// 调用方按「无封面」占位处理）。多张图片时返回正面封面（<c>PictureType.FrontCover</c>），
    /// 无正面封面则返回第一张。
    /// </returns>
    /// <remarks>
    /// 与 <see cref="Read"/> 分开是本轮的<b>刻意设计</b>：旧工程在扫描阶段就把封面转成
    /// base64 data URL 塞进歌曲对象并随曲库落盘，5000 首会让 JSON 涨到 250 MB–1 GB。
    /// 拆出来后扫描只判定 <see cref="MusicTagInfo.HasCover"/>（不读字节），
    /// 封面在列表滚动到可见时才取，内存与磁盘都可控。
    /// </remarks>
    MusicCover? ReadCover(string filePath);
}
