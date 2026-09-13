using SystemToolkit.Core.Backup.Models;
using SystemToolkit.Core.Backup.Services;
using Xunit;

namespace SystemToolkit.Tests;

/// <summary>
/// 备份后读回校验（落地计划 B5a）：确定性抽样 + 报告语义。
/// <para>
/// 🔴 本文件最重要的一条是 <see cref="Report_SampledPass_IsNotReportedAsFullPass"/>：
/// **抽样通过不得被当作"校验通过"报出去**。备份的既有实现只在复制时算**源**哈希
/// （"写入字节即源字节"），目标盘是否真的写对从未验证 —— 若这里再把抽样结果说成"通过"，
/// 用户拿到的是一个**未经验证的承诺**（状态欺骗）。
/// </para>
/// </summary>
public sealed class BackupPostVerifyTests
{
    private static List<FileEntry> Entries(int count)
    {
        var list = new List<FileEntry>(count);
        for (int i = 0; i < count; i++)
        {
            list.Add(new FileEntry { RelativePath = $"f{i}.bin", Size = 1, Sha256 = "00" });
        }

        return list;
    }

    [Fact]
    public void SelectSample_ZeroOrNegative_MeansFull()
    {
        List<FileEntry> all = Entries(10);

        Assert.Equal(10, SnapshotVerifier.SelectSample(all, 0).Count);
        Assert.Equal(10, SnapshotVerifier.SelectSample(all, -1).Count);
    }

    [Fact]
    public void SelectSample_LargerThanTotal_MeansFull()
    {
        List<FileEntry> all = Entries(3);

        Assert.Equal(3, SnapshotVerifier.SelectSample(all, 99).Count);
    }

    [Fact]
    public void SelectSample_TakesRequestedCount_SpreadOverList()
    {
        List<FileEntry> all = Entries(100);

        List<FileEntry> sample = SnapshotVerifier.SelectSample(all, 4);

        Assert.Equal(4, sample.Count);
        Assert.Equal(new[] { "f0.bin", "f25.bin", "f50.bin", "f75.bin" }, sample.Select(e => e.RelativePath).ToArray());
    }

    [Fact]
    public void SelectSample_IsDeterministic()
    {
        // 刻意不用随机：同一份清单每次取同一批样本，问题可复现
        List<FileEntry> all = Entries(37);

        Assert.Equal(
            SnapshotVerifier.SelectSample(all, 5).Select(e => e.RelativePath).ToArray(),
            SnapshotVerifier.SelectSample(all, 5).Select(e => e.RelativePath).ToArray());
    }

    [Fact]
    public void SelectSample_NeverExceedsTotal()
    {
        for (int total = 1; total <= 20; total++)
        {
            List<FileEntry> all = Entries(total);
            for (int size = 1; size <= 25; size++)
            {
                List<FileEntry> sample = SnapshotVerifier.SelectSample(all, size);
                Assert.True(sample.Count <= total, $"total={total} size={size}");
                Assert.True(sample.Count <= size, $"total={total} size={size}");
            }
        }
    }

    [Fact]
    public void Report_FullPass_SaysVerified()
    {
        var report = new SnapshotVerifyReport(100, 100, 0, 0, Array.Empty<string>()) { Checked = 100 };

        Assert.True(report.Success);
        Assert.False(report.IsSampled);
        Assert.Equal("校验通过（100/100 个文件哈希一致）", report.Message);
    }

    [Fact]
    public void Report_SampledPass_IsNotReportedAsFullPass()
    {
        // 🔴 抽样通过必须显式声明"未做完整校验"，否则就是把未验证的承诺说成已验证
        var report = new SnapshotVerifyReport(10_000, 200, 0, 0, Array.Empty<string>()) { Checked = 200 };

        Assert.True(report.Success);
        Assert.True(report.IsSampled);
        Assert.Contains("抽样", report.Message, StringComparison.Ordinal);
        Assert.Contains("未做完整校验", report.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Report_SampledFailure_ReportsCountsAndSampleSize()
    {
        var report = new SnapshotVerifyReport(500, 3, 1, 0, new[] { "f9.bin：哈希不一致" }) { Checked = 4 };

        Assert.False(report.Success);
        Assert.Contains("已查 4/500", report.Message, StringComparison.Ordinal);
        Assert.Contains("不一致 1", report.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Report_MissingFile_FailsEvenWhenSampled()
    {
        var report = new SnapshotVerifyReport(50, 3, 0, 1, new[] { "f2.bin：文件缺失" }) { Checked = 4 };

        Assert.False(report.Success);
    }

    [Fact]
    public void ChecksumStatuses_HasSkippedForUnverifiedSnapshots()
    {
        // 抽样通过 / 未校验的诚实落点：不能是 passed
        Assert.Equal("skipped", ChecksumStatuses.Skipped);
        Assert.NotEqual(ChecksumStatuses.Passed, ChecksumStatuses.Skipped);
    }
}
