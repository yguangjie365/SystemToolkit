using System.Text.Json.Serialization;

namespace SystemToolkit.Core.FileTransfer.Models;

/// <summary>
/// 共享文件条目：本机暴露给其它设备/手机浏览的文件。
/// </summary>
public sealed class FileShareEntry
{
    /// <summary>文件名。</summary>
    public string Name { get; init; } = string.Empty;

    /// <summary>完整路径。仅服务端内部使用，绝不随 API 下发（防止泄露本机目录结构）。</summary>
    [JsonIgnore]
    public string FullPath { get; init; } = string.Empty;

    /// <summary>文件大小（字节）。</summary>
    public long Size { get; init; }

    /// <summary>最后修改时间。</summary>
    public DateTimeOffset LastModified { get; init; }

    /// <summary>是否为目录。</summary>
    public bool IsDirectory { get; init; }

    /// <summary>相对路径（相对共享根目录）。</summary>
    public string RelativePath { get; init; } = string.Empty;

    /// <summary>MIME 类型（文件传输时用于 HTTP 响应）。</summary>
    public string ContentType => IsDirectory ? "directory" : GuessContentType(Name);

    private static string GuessContentType(string fileName)
    {
        string ext = Path.GetExtension(fileName).ToLowerInvariant();
        return ext switch
        {
            ".txt" => "text/plain",
            ".pdf" => "application/pdf",
            ".zip" => "application/zip",
            ".rar" => "application/vnd.rar",
            ".7z" => "application/x-7z-compressed",
            ".jpg" or ".jpeg" => "image/jpeg",
            ".png" => "image/png",
            ".gif" => "image/gif",
            ".bmp" => "image/bmp",
            ".mp4" => "video/mp4",
            ".mkv" => "video/x-matroska",
            ".avi" => "video/x-msvideo",
            ".mp3" => "audio/mpeg",
            ".wav" => "audio/wav",
            ".flac" => "audio/flac",
            ".doc" => "application/msword",
            ".docx" => "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
            ".xls" => "application/vnd.ms-excel",
            ".xlsx" => "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
            ".ppt" => "application/vnd.ms-powerpoint",
            ".pptx" => "application/vnd.openxmlformats-officedocument.presentationml.presentation",
            ".exe" => "application/octet-stream",
            ".msi" => "application/octet-stream",
            ".json" => "application/json",
            ".xml" => "application/xml",
            ".html" or ".htm" => "text/html",
            ".css" => "text/css",
            ".js" => "application/javascript",
            _ => "application/octet-stream",
        };
    }
}
