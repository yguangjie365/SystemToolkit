namespace SystemToolkit.Core.Music.Services;

/// <summary>
/// 播放器内 10 段均衡器配置（OM-7，路线 B：仅音乐播放器自己的声音过 EQ）。
/// </summary>
/// <remarks>
/// <para><b>为何是纯数据模型</b>：EQ 的 DSP 链（每段一个 peaking filter + 首尾 shelf）
/// 由引擎实现侧以 NAudio <c>BiQuadFilter</c> 级联构建（滤波系数算法属 NAudio/MIT，
/// 不自研 DSP——参考纪律）；本模型承载<b>用户可调的状态</b>：开关、总增益（preamp）、
/// 10 段增益（dB）。VM/UI 只读写本模型，引擎侧经 <c>ApplyEqualizer</c> 重建滤波链。</para>
/// <para>频点表取标准 10 段 ISO 倍频程（63 Hz–16 kHz）；增益钳制 ±12 dB（越界静默钳制，
/// 防止极端值造成削波/损坏听力的输入）。预设见 <see cref="Presets"/>。</para>
/// </remarks>
public sealed class EqProfile
{
    /// <summary>频段数（与 <see cref="Frequencies"/> 等长）。</summary>
    public const int BandCount = 10;

    /// <summary>增益钳制上限（dB）。</summary>
    public const double MaxGainDb = 12.0;

    /// <summary>标准 10 段中心频率（Hz，升序唯一）。</summary>
    public static readonly int[] Frequencies = [63, 125, 250, 500, 1000, 2000, 4000, 8000, 12000, 16000];

    /// <summary>EQ 是否启用（false = 直通，滤波链不生效）。</summary>
    public bool Enabled { get; set; }

    /// <summary>总增益（preamp，dB）。</summary>
    public double PreampDb { get; set; }

    private readonly double[] _gains = new double[BandCount];

    /// <summary>
    /// 获取/设置某段增益（dB，自动钳制到 ±<see cref="MaxGainDb"/>）。
    /// </summary>
    /// <param name="index">0–9，对应 <see cref="Frequencies"/>。</param>
    public double this[int index]
    {
        get => index is >= 0 and < BandCount ? _gains[index] : 0;
        set
        {
            if (index is >= 0 and < BandCount)
            {
                _gains[index] = Math.Clamp(value, -MaxGainDb, MaxGainDb);
            }
        }
    }

    /// <summary>全平（所有增益 0 dB、preamp 0、默认关）的工厂。</summary>
    public static EqProfile Flat() => new();

    /// <summary>
    /// 从 10 个增益（dB）构造（顺序对应 <see cref="Frequencies"/>）。
    /// </summary>
    public static EqProfile FromGains(IEnumerable<double> gains)
    {
        var profile = new EqProfile();
        double[] array = gains.ToArray();
        for (int i = 0; i < Math.Min(BandCount, array.Length); i++)
        {
            profile[i] = array[i]; // 索引器内钳制
        }

        return profile;
    }

    /// <summary>导出 10 段增益副本（引擎链重建用，防外部引用漂移）。</summary>
    public double[] GainsCopy() => (double[])_gains.Clone();

    /// <summary>按名称取预设；未知名称返回 null（UI 用它回退「自定义」态）。</summary>
    public static EqPreset? FindPreset(string name) =>
        Presets.FirstOrDefault(p => p.Name == name);

    /// <summary>
    /// 内置预设（名称 + 增益 + preamp；值域均在 ±12 dB 内）。
    /// </summary>
    public sealed record EqPreset(string Name, IReadOnlyList<double> Gains, double Preamp);

    /// <summary>内置预设表。频点 = <see cref="Frequencies"/>。</summary>
    public static IReadOnlyList<EqPreset> Presets { get; } =
    [
        new("流行",  [1.0, 1.5, 2.0, 1.0, -0.5, -1.0, 0.0, 1.0, 1.5, 1.0], 0),
        new("摇滚",  [3.0, 2.0, 0.5, -1.0, -1.5, 0.0, 1.5, 2.5, 3.0, 3.0], 0),
        new("电子",  [2.5, 2.0, 1.5, 0.5, 0.0, -0.5, 0.5, 1.5, 2.0, 2.0], 0),
        new("古典",  [3.0, 2.0, 0.0, -1.5, -1.0, -1.0, -1.0, 0.0, 2.0, 3.0], 0),
        new("人声",  [-2.0, -1.5, -1.0, 0.0, 1.5, 2.5, 3.0, 2.0, 0.5, -1.0], 0),
        new("重低音", [4.0, 4.0, 3.0, 2.0, 1.0, 0.5, 0.0, 0.0, 0.0, 0.0], 0),
    ];
}
