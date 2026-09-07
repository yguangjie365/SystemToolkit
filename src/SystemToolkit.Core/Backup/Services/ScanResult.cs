namespace SystemToolkit.Core.Backup.Services;

/// <summary>一次源扫描的结果：文件条目列表 + 空目录相对路径列表。</summary>
/// <param name="Files">扫描发现的文件条目（绝对路径 + 快照内相对路径 + 字节数），已按相对路径排序。</param>
/// <param name="EmptyDirs">空目录的相对路径列表（用于恢复后重建空目录结构）。</param>
public sealed record ScanResult(List<ScannedFile> Files, List<string> EmptyDirs);
