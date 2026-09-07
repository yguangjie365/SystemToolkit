namespace SystemToolkit.Core.Backup.Services;

/// <summary>扫描发现的一个文件：绝对源路径 + 快照内相对路径 + 字节数。</summary>
/// <param name="SourcePath">文件在磁盘上的绝对路径。</param>
/// <param name="RelativePath">快照内的 POSIX 风格相对路径（决定 files/ 目录下的存放位置）。</param>
/// <param name="Length">文件字节数（扫描时读取，备份空间预检用）。</param>
public sealed record ScannedFile(string SourcePath, string RelativePath, long Length = 0L);
