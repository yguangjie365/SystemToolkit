using System.Security.Cryptography;

namespace SystemToolkit.Core.Utilities;

/// <summary>SHA-256 哈希与「复制-哈希」一体工具（Backup 域共用，流式处理大文件）。</summary>
public static class Sha256Hasher
{
    /// <summary>流式读写的缓冲区大小（1 MiB）。</summary>
    public const int ChunkSize = 1048576;

    /// <summary>计算文件 SHA-256，返回小写十六进制文本。</summary>
    public static string HashFile(string path)
    {
        using var source = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1048576);
        return Convert.ToHexString(SHA256.HashData(source)).ToLowerInvariant();
    }

    /// <summary>计算流内容的 SHA-256（读至流尾但不关闭流），返回小写十六进制文本。</summary>
    public static string HashStream(Stream stream)
    {
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }

    /// <summary>边复制边计算源数据 SHA-256（单遍 I/O，不做二次整读校验）。</summary>
    /// <param name="src">源文件路径。</param>
    /// <param name="dst">目标文件路径（已存在则覆盖）。</param>
    /// <returns>(复制的字节数, 源数据 SHA-256 小写十六进制)。</returns>
    public static (long Size, string SourceHash) CopyAndHash(string src, string dst)
    {
        using var source = new FileStream(src, FileMode.Open, FileAccess.Read, FileShare.Read, ChunkSize);
        using var dest = new FileStream(dst, FileMode.Create, FileAccess.Write, FileShare.None, ChunkSize);
        using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        byte[] buffer = new byte[ChunkSize];
        long totalBytes = 0L;
        int bytesRead;
        while ((bytesRead = source.Read(buffer, 0, buffer.Length)) > 0)
        {
            dest.Write(buffer, 0, bytesRead);
            sha.AppendData(buffer, 0, bytesRead);
            totalBytes += bytesRead;
        }
        return (Size: totalBytes, SourceHash: Convert.ToHexString(sha.GetHashAndReset()).ToLowerInvariant());
    }
}
