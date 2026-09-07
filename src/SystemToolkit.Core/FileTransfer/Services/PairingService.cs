using System.Security.Cryptography;

namespace SystemToolkit.Core.FileTransfer.Services;

/// <summary>
/// 一次性配对码服务（V0.5 批次二新增，Web 通道与桌面 TCP 通道共用）。
/// <para>
/// 契约（承旧工程 Web 通道内联实现的语义，2026-09-06 抽取为独立服务）：
/// 6 位、字母表去 0/O/1/I（防人工误读）、惰性生成与到期轮换（默认 10 分钟）、
/// 成功消费立即失效（一次性）、常量时间比较防时序侧信道。
/// 配对码只用于换取长期凭据（Web token / 会话），绝不进入长期访问 URL。
/// </para>
/// </summary>
public sealed class PairingService
{
    /// <summary>配对码字母表（去除易混淆的 0/O/1/I）。</summary>
    public const string CodeAlphabet = "23456789ABCDEFGHJKMNPQRSTUVWXYZ";

    /// <summary>配对码长度。</summary>
    public const int CodeLength = 6;

    private readonly object _gate = new();
    private readonly TimeSpan _lifetime;
    private string _code = string.Empty;
    private DateTimeOffset _expiresAt = DateTimeOffset.MinValue;

    /// <summary>构造配对码服务。<paramref name="lifetime"/> 缺省 10 分钟（测试可缩短）。</summary>
    public PairingService(TimeSpan? lifetime = null)
    {
        _lifetime = lifetime ?? TimeSpan.FromMinutes(10);
    }

    /// <summary>当前有效配对码（惰性生成；到期后下一次读取自动轮换）。</summary>
    public string CurrentCode
    {
        get
        {
            lock (_gate)
            {
                EnsureFreshCodeLocked();
                return _code;
            }
        }
    }

    /// <summary>当前配对码的过期时间。</summary>
    public DateTimeOffset ExpiresAt
    {
        get
        {
            lock (_gate)
            {
                EnsureFreshCodeLocked();
                return _expiresAt;
            }
        }
    }

    /// <summary>
    /// 校验并消费配对码：正确且未过期 → 返回 true 并立即失效（一次性）；
    /// 错误 / 已过期 → 返回 false 且不影响当前码。比较忽略大小写（手机键盘自动大写）。
    /// </summary>
    public bool TryConsume(string? code)
    {
        if (string.IsNullOrWhiteSpace(code) || code.Length != CodeLength)
        {
            return false;
        }

        lock (_gate)
        {
            if (DateTimeOffset.UtcNow >= _expiresAt)
            {
                return false;
            }

            if (!CryptographicOperations.FixedTimeEquals(
                    EncodingAsciiPad(_code), EncodingAsciiPad(code.ToUpperInvariant())))
            {
                return false;
            }

            // 一次性：成功即失效——下一位配对者（含重放者）拿旧码必然失败
            _code = string.Empty;
            _expiresAt = DateTimeOffset.MinValue;
            return true;
        }
    }

    /// <summary>生成新配对码并顺延有效期（调用方须持有 _gate）。</summary>
    private void EnsureFreshCodeLocked()
    {
        if (!string.IsNullOrEmpty(_code) && DateTimeOffset.UtcNow < _expiresAt)
        {
            return;
        }

        // REVIEW-3 A-6：拒绝采样——旧取模法 256%31=8 使前 8 个字母概率略高，削弱 6 位码熵
        int alphabetLen = CodeAlphabet.Length;
        int limit = byte.MaxValue - byte.MaxValue % alphabetLen; // 可整除映射的上界（248）
        Span<byte> random = stackalloc byte[CodeLength * 2]; // 预留重采样余量
        RandomNumberGenerator.Fill(random);
        char[] chars = new char[CodeLength];
        int ri = 0;
        for (int i = 0; i < CodeLength; i++)
        {
            byte b;
            do
            {
                if (ri >= random.Length)
                {
                    // 极小概率耗尽预取字节：续采一批（拒绝采样期望消耗 <2 字节/字符）
                    RandomNumberGenerator.Fill(random);
                    ri = 0;
                }
                b = random[ri++];
            }
            while (b >= limit);
            chars[i] = CodeAlphabet[b % alphabetLen];
        }

        _code = new string(chars);
        _expiresAt = DateTimeOffset.UtcNow.Add(_lifetime);
    }

    /// <summary>定长 ASCII 填充为 32 字节，供常量时间比较（长度不同的输入不抛异常）。</summary>
    private static byte[] EncodingAsciiPad(string text)
    {
        byte[] buffer = new byte[32];
        for (int i = 0; i < text.Length && i < buffer.Length; i++)
        {
            buffer[i] = (byte)text[i];
        }

        return buffer;
    }
}
