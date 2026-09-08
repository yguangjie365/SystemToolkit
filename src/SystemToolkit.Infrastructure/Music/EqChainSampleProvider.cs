using NAudio.Dsp;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using SystemToolkit.Core.Music.Services;

namespace SystemToolkit.Infrastructure.Music;

/// <summary>
/// 10 段均衡器采样链（OM-7）：preamp + 10 个 peaking biquad 级联的 ISampleProvider。
/// </summary>
/// <remarks>
/// <para>系数用 NAudio 自带 <see cref="BiQuadFilter"/>（RBJ Audio EQ Cookbook 公式，MIT；
/// <b>不自研 DSP</b>）。本类只做编排：逐样本乘 preamp → 依次过 10 段。</para>
/// <para><see cref="Update"/> 支持播放中<b>无缝热更</b>（只改系数不重建音频图，无爆音/无间隙）；
/// 开/关切换（直通 ↔ 链）则由引擎侧重建音频图。</para>
/// <para>API 实证（2026-09-09 反射 NAudio 2.2.1）：<c>BiQuadFilter.PeakingEQ(float, float, float, float)</c>、
/// <c>SetPeakingEq(float×4)</c>、<c>Transform(float)</c>；2.2.1 无现成 BiQuad 包装 Provider。</para>
/// </remarks>
public sealed class EqChainSampleProvider : ISampleProvider
{
    private readonly ISampleProvider _source;
    private readonly BiQuadFilter[] _filters;
    private readonly int _sampleRate;
    private float _preampLinear;

    public EqChainSampleProvider(ISampleProvider source, int sampleRate, IReadOnlyList<double> gains, double preampDb)
    {
        _source = source;
        _sampleRate = sampleRate;
        _filters = new BiQuadFilter[EqProfile.BandCount];
        Update(gains, preampDb);
    }

    /// <inheritdoc />
    public WaveFormat WaveFormat => _source.WaveFormat;

    /// <summary>
    /// 热更系数（播放中调用安全：只改滤波器系数，不动音频图）。
    /// </summary>
    public void Update(IReadOnlyList<double> gains, double preampDb)
    {
        _preampLinear = (float)Math.Pow(10, preampDb / 20.0);
        for (int i = 0; i < EqProfile.BandCount; i++)
        {
            if (_filters[i] is null)
            {
                _filters[i] = BiQuadFilter.PeakingEQ(_sampleRate, EqProfile.Frequencies[i], 1.0f, (float)gains[i]);
            }
            else
            {
                _filters[i].SetPeakingEq(_sampleRate, EqProfile.Frequencies[i], 1.0f, (float)gains[i]);
            }
        }
    }

    /// <inheritdoc />
    public int Read(float[] buffer, int offset, int count)
    {
        int read = _source.Read(buffer, offset, count);
        for (int n = offset; n < offset + read; n++)
        {
            float v = buffer[n] * _preampLinear;
            foreach (BiQuadFilter filter in _filters)
            {
                v = filter.Transform(v);
            }

            buffer[n] = v;
        }

        return read;
    }
}
