using SystemToolkit.Core.Music.Services;

namespace SystemToolkit.Tests.Music;

/// <summary>OM-7 EQ 数据模型：频点表 / 钳制 / 预设合法域。</summary>
public class EqProfileTests
{
    [Fact]
    public void FrequencyTable_HasTenUniqueAscendingBands()
    {
        Assert.Equal(EqProfile.BandCount, EqProfile.Frequencies.Length);
        for (int i = 1; i < EqProfile.Frequencies.Length; i++)
        {
            Assert.True(EqProfile.Frequencies[i] > EqProfile.Frequencies[i - 1],
                $"频点应严格升序（第 {i} 项）");
        }
    }

    [Fact]
    public void DefaultProfile_IsFlatAndDisabled()
    {
        var p = EqProfile.Flat();

        Assert.False(p.Enabled);
        Assert.Equal(0, p.PreampDb);
        for (int i = 0; i < EqProfile.BandCount; i++)
        {
            Assert.Equal(0, p[i]);
        }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(4)]
    [InlineData(9)]
    public void GainBeyondClamp_IsSilentlyClamped(int index)
    {
        var p = EqProfile.Flat();
        p[index] = 40; // 越界 +12
        Assert.Equal(EqProfile.MaxGainDb, p[index]);
        p[index] = -40; // 越界 -12
        Assert.Equal(-EqProfile.MaxGainDb, p[index]);
    }

    [Fact]
    public void OutOfRangeIndex_ReadsZeroAndIgnoresWrite()
    {
        var p = EqProfile.Flat();
        Assert.Equal(0, p[-1]);
        Assert.Equal(0, p[10]);
        p[10] = 5;
        Assert.Equal(0, p[0]); // 未污染正常段
    }

    [Fact]
    public void FromGains_ClampsAndPreservesOrder()
    {
        var p = EqProfile.FromGains([0, 1, 2, 3, 4, 5, 6, 7, 8, 99]);
        Assert.Equal(99 > EqProfile.MaxGainDb ? EqProfile.MaxGainDb : 99, p[9]);
        Assert.Equal(5, p[5]);
        Assert.Equal(2, p[2]);
    }

    [Fact]
    public void GainsCopy_IsIndependentSnapshot()
    {
        var p = EqProfile.FromGains(Enumerable.Range(0, EqProfile.BandCount).Select(i => (double)i));
        double[] copy = p.GainsCopy();
        p[0] = 11; // 改源不影响快照
        Assert.Equal(0, copy[0]);
        Assert.Equal(11, p[0]);
    }

    [Fact]
    public void AllPresets_HaveTenBandsAndInRangeGains()
    {
        Assert.NotEmpty(EqProfile.Presets);
        foreach (EqProfile.EqPreset preset in EqProfile.Presets)
        {
            Assert.Equal(EqProfile.BandCount, preset.Gains.Count);
            foreach (double gain in preset.Gains)
            {
                Assert.InRange(gain, -EqProfile.MaxGainDb, EqProfile.MaxGainDb);
            }

            Assert.InRange(preset.Preamp, -EqProfile.MaxGainDb, EqProfile.MaxGainDb);
        }
    }

    [Fact]
    public void PresetLookup_HitsKnownAndMissesUnknown()
    {
        Assert.NotNull(EqProfile.FindPreset("流行"));
        Assert.Equal("摇滚", EqProfile.FindPreset("摇滚")!.Name);
        Assert.Null(EqProfile.FindPreset("自定义不存在"));
    }
}
