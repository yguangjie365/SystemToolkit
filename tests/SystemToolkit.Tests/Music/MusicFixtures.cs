using System.Text;

namespace SystemToolkit.Tests.Music;

/// <summary>
/// 运行时合成音频文件的测试设施。
/// </summary>
/// <remarks>
/// <para><b>为什么不签入真实音频</b>：<c>git ls-files</c> 确认本仓库零个二进制音频夹具。
/// 签入 mp3/flac 样本会把二进制塞进 git 历史（永久膨胀、无法 diff、无法审查），
/// 而标签读取要验证的是<b>容器结构解析</b>，不是编解码质量——
/// 手写的静音容器完全够用，且每个字节都在测试代码里可审。</para>
/// <para><b>标签写入交给 TagLibSharp 自己</b>：夹具只负责造出合法容器，
/// 标签/封面由被测读取器的同一个库写回去（<c>TagLib.File.Create(...).Save()</c>），
/// 这样测试验证的是「本项目能否读回业界工具写出的标签」，而不是自造格式自读。</para>
/// </remarks>
public static class MusicFixtures
{
    /// <summary>MPEG1 Layer III / 128 kbps / 44100 Hz / 无填充的帧长：144 × 128000 ÷ 44100 = 417。</summary>
    private const int Mp3FrameLength = 417;

    /// <summary>构造 PCM WAV（16-bit）。静音样本：TagLib 只按 data 大小 ÷ byteRate 推算时长。</summary>
    /// <param name="seconds">时长（秒）。</param>
    /// <param name="sampleRate">采样率。</param>
    /// <param name="channels">声道数。</param>
    /// <param name="bitsPerSample">位深。</param>
    /// <returns>完整 RIFF/WAVE 字节。</returns>
    public static byte[] BuildWavBytes(double seconds = 0.5, int sampleRate = 44100, int channels = 2, int bitsPerSample = 16)
    {
        int blockAlign = channels * bitsPerSample / 8;
        int byteRate = sampleRate * blockAlign;
        int dataSize = (int)(sampleRate * seconds) * blockAlign;

        using var ms = new MemoryStream();
        using var bw = new BinaryWriter(ms);
        bw.Write(Encoding.ASCII.GetBytes("RIFF"));
        bw.Write(36 + dataSize);
        bw.Write(Encoding.ASCII.GetBytes("WAVE"));
        bw.Write(Encoding.ASCII.GetBytes("fmt "));
        bw.Write(16);
        bw.Write((ushort)1); // PCM
        bw.Write((ushort)channels);
        bw.Write(sampleRate);
        bw.Write(byteRate);
        bw.Write((ushort)blockAlign);
        bw.Write((ushort)bitsPerSample);
        bw.Write(Encoding.ASCII.GetBytes("data"));
        bw.Write(dataSize);
        bw.Write(new byte[dataSize]);
        bw.Flush();
        return ms.ToArray();
    }

    /// <summary>
    /// 构造 FLAC。
    /// </summary>
    /// <remarks>
    /// FLAC 的时长 = totalSamples ÷ sampleRate，<b>写在 STREAMINFO 里</b>，
    /// 因此时长可以精确控制（MP3 只能按「文件大小 ÷ 码率」估算，误差不可控）。
    /// <para><b>实测坑</b>：只写 STREAMINFO 不写音频帧时，TagLib 的
    /// <c>first_frame_offset</c> 停在 -1，<c>Duration</c> 恒为 0——尽管 totalSamples 是对的。
    /// 故默认补一个伪音频帧同步码（<c>0xFF 0xF8</c> = FLAC 的 14 位同步字 <c>0x3FFE</c>）
    /// 让 TagLib 能定位到音频起点。TagLib 只找偏移、不解码，填充字节无需合法。</para>
    /// </remarks>
    /// <param name="totalSamples">总采样点数（÷ sampleRate 即时长）。</param>
    /// <param name="sampleRate">采样率（20 bit）。</param>
    /// <param name="channels">声道数（3 bit，存 channels-1）。</param>
    /// <param name="bitsPerSample">位深（5 bit，存 bitsPerSample-1）。</param>
    /// <param name="withAudioFrame">
    /// 是否在元数据块后补一个伪音频帧。置 false 可模拟
    /// 「下载被截断：标签与 STREAMINFO 完好，但没有音频数据」的真实残缺文件。
    /// </param>
    /// <returns>fLaC + STREAMINFO（+ 伪音频帧）字节；MD5 全零——TagLib 不校验。</returns>
    public static byte[] BuildFlacBytes(
        ulong totalSamples = 44100,
        int sampleRate = 44100,
        int channels = 2,
        int bitsPerSample = 16,
        bool withAudioFrame = true)
    {
        using var ms = new MemoryStream();
        using var bw = new BinaryWriter(ms);
        bw.Write(Encoding.ASCII.GetBytes("fLaC"));
        bw.Write((byte)0x80); // 最高位 = 最后一个元数据块；低 7 位 = 块类型 0（STREAMINFO）
        WriteBigEndian(bw, 34, 3); // 块长度 34
        WriteBigEndian(bw, 4096, 2); // min block size
        WriteBigEndian(bw, 4096, 2); // max block size
        WriteBigEndian(bw, 0, 3); // min frame size（0 = 未知）
        WriteBigEndian(bw, 0, 3); // max frame size
        ulong packed = ((ulong)sampleRate << 44)
            | ((ulong)(channels - 1) << 41)
            | ((ulong)(bitsPerSample - 1) << 36)
            | (totalSamples & 0xF_FFFF_FFFFUL);
        WriteBigEndian(bw, packed, 8);
        bw.Write(new byte[16]); // MD5 签名
        if (withAudioFrame)
        {
            byte[] frame = new byte[64];
            frame[0] = 0xFF;
            frame[1] = 0xF8;
            bw.Write(frame);
        }

        bw.Flush();
        return ms.ToArray();
    }

    /// <summary>
    /// 构造一段<b>确定性</b>损坏的字节：4096 个全部小于 <c>0xFE</c> 的字节。
    /// </summary>
    /// <remarks>
    /// 不用随机字节做损坏夹具——随机数据有可观概率撞上伪 MPEG 同步字（<c>0xFF</c> 开头），
    /// 测试结果会随种子漂移。本夹具一个 <c>0xFF</c> 都没有，任何音频格式的同步字都不可能命中，
    /// 因此「必须失败」是确定结论而非概率结论。
    /// </remarks>
    public static byte[] BuildSyncFreeBytes(int length = 4096)
    {
        byte[] bytes = new byte[length];
        for (int i = 0; i < bytes.Length; i++)
        {
            bytes[i] = (byte)(i % 0xFE);
        }

        return bytes;
    }

    /// <summary>
    /// 构造 CBR MP3：<paramref name="frameCount"/> 个内容相同的有效帧头 + 零填充。
    /// </summary>
    /// <remarks>
    /// 帧头 <c>FF FB 90 00</c> = 同步字 + MPEG1 + Layer III + 无 CRC + 128 kbps + 44100 Hz
    /// + 无填充 + 立体声。每帧等长 417 字节，同步点落在 0/417/834…，是合法 CBR 流。
    /// TagLib 会用它估算时长，故本夹具的时长断言只验证「&gt; 0」而不验证具体值。
    /// </remarks>
    /// <param name="frameCount">帧数（38 帧 ≈ 1 秒）。</param>
    /// <returns>MP3 字节。</returns>
    public static byte[] BuildMp3Bytes(int frameCount = 38)
    {
        byte[] bytes = new byte[frameCount * Mp3FrameLength];
        for (int i = 0; i < frameCount; i++)
        {
            int offset = i * Mp3FrameLength;
            bytes[offset] = 0xFF;
            bytes[offset + 1] = 0xFB;
            bytes[offset + 2] = 0x90;
            bytes[offset + 3] = 0x00;
        }

        return bytes;
    }

    /// <summary>一张 1×1 的合法 PNG（用作内嵌封面夹具——解码不需要，但字节须是真实图片格式）。</summary>
    public static byte[] OnePixelPng { get; } = Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8AAAwAB/AF+p7RLAAAAAElFTkSuQmCC");

    /// <summary>按大端序写入 <paramref name="byteCount"/> 个字节。</summary>
    private static void WriteBigEndian(BinaryWriter bw, ulong value, int byteCount)
    {
        for (int shift = (byteCount - 1) * 8; shift >= 0; shift -= 8)
        {
            bw.Write((byte)((value >> shift) & 0xFF));
        }
    }
}

/// <summary>
/// 一次性临时目录（GUID 命名，Dispose 时递归删除）。
/// </summary>
/// <remarks>
/// 沿用备份域 <c>TestHelpers.MakeConfig</c> 的隔离思路，但做成 <see cref="IDisposable"/>：
/// 音乐扫描的测试每个都要造多层目录树，手写 try/finally 会淹没断言。
/// 删除失败时静默——临时目录残留不影响测试结论，抛异常反而会掩盖真正的失败原因。
/// </remarks>
public sealed class TempDir : IDisposable
{
    /// <summary>临时目录绝对路径。</summary>
    public string Path { get; }

    /// <summary>创建并落盘一个临时目录。</summary>
    /// <param name="prefix">目录名前缀（便于人工排查残留）。</param>
    public TempDir(string prefix = "st_music")
    {
        Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), prefix + "_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path);
    }

    /// <summary>把字节写到临时目录下的相对路径（自动建父目录），返回绝对路径。</summary>
    /// <param name="relativePath">相对 <see cref="Path"/> 的路径（可用 <c>/</c> 分隔多级）。</param>
    /// <param name="bytes">文件内容。</param>
    /// <returns>写入后的绝对路径。</returns>
    public string Write(string relativePath, byte[] bytes)
    {
        string full = Resolve(relativePath);
        string? parent = System.IO.Path.GetDirectoryName(full);
        if (!string.IsNullOrEmpty(parent))
        {
            Directory.CreateDirectory(parent);
        }

        File.WriteAllBytes(full, bytes);
        return full;
    }

    /// <summary>在临时目录下创建（可能多级的）子目录，返回绝对路径。</summary>
    /// <param name="relativePath">相对路径。</param>
    /// <returns>目录绝对路径。</returns>
    public string CreateDir(string relativePath)
    {
        string full = Resolve(relativePath);
        Directory.CreateDirectory(full);
        return full;
    }

    /// <summary>解析相对路径为绝对路径。</summary>
    /// <param name="relativePath">相对路径。</param>
    /// <returns>绝对路径。</returns>
    public string Resolve(string relativePath) => System.IO.Path.Combine(Path, relativePath);

    /// <inheritdoc/>
    public void Dispose()
    {
        try
        {
            if (Directory.Exists(Path))
            {
                Directory.Delete(Path, true);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
