using SystemToolkit.Core.FileTransfer.Models;
using SystemToolkit.Core.FileTransfer.Services;

namespace SystemToolkit.Tests;

/// <summary>
/// 传输历史测试（自旧工程移植，L15 英文命名）。核心守卫：①跨会话能读回
/// ②超限只留最近的 ③文件损坏时降级为空而不是让模块起不来。
/// </summary>
public class TransferHistoryServiceTests
{
    private static TransferHistoryService NewService(out string dir)
    {
        dir = Path.Combine(Path.GetTempPath(), "stkft-history", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return new TransferHistoryService(dir);
    }

    private static TransferHistoryEntry MakeEntry(int index, DateTimeOffset finishedAt) => new()
    {
        TaskId = "task-" + index,
        FileName = $"file{index}.bin",
        FileSize = 1024L * index,
        Direction = index % 2 == 0 ? TransferDirection.Send : TransferDirection.Receive,
        PeerEndpoint = "192.168.1.20:18889",
        Status = index % 5 == 0 ? TransferStatus.Failed : TransferStatus.Completed,
        StartedAt = finishedAt.AddSeconds(-10),
        FinishedAt = finishedAt,
        TransferredBytes = 1024L * index,
    };

    [Fact]
    public void Append_PersistedAcrossInstances()
    {
        TransferHistoryService service = NewService(out string dir);
        try
        {
            var when = new DateTimeOffset(2026, 9, 2, 10, 0, 0, TimeSpan.Zero);
            service.Append(MakeEntry(1, when));

            // 用另一个实例读取，模拟重启后的场景
            IReadOnlyList<TransferHistoryEntry> reloaded = new TransferHistoryService(dir).Load();

            Assert.Single(reloaded);
            Assert.Equal("file1.bin", reloaded[0].FileName);
            Assert.Equal(TransferDirection.Receive, reloaded[0].Direction);
            Assert.Equal(when, reloaded[0].FinishedAt);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void Load_OrderedByFinishedAtDescending()
    {
        TransferHistoryService service = NewService(out string dir);
        try
        {
            var baseTime = new DateTimeOffset(2026, 9, 2, 10, 0, 0, TimeSpan.Zero);
            service.Append(MakeEntry(1, baseTime));
            service.Append(MakeEntry(2, baseTime.AddMinutes(5)));
            service.Append(MakeEntry(3, baseTime.AddMinutes(2)));

            IReadOnlyList<TransferHistoryEntry> list = service.Load();

            Assert.Equal(3, list.Count);
            Assert.Equal("file2.bin", list[0].FileName); // 最新
            Assert.Equal("file3.bin", list[1].FileName);
            Assert.Equal("file1.bin", list[2].FileName);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void Append_OverMaxEntries_KeepsMostRecent()
    {
        TransferHistoryService service = NewService(out string dir);
        try
        {
            var baseTime = new DateTimeOffset(2026, 9, 2, 10, 0, 0, TimeSpan.Zero);
            for (int i = 0; i < TransferHistoryService.MaxEntries + 20; i++)
            {
                service.Append(MakeEntry(i, baseTime.AddMinutes(i)));
            }

            IReadOnlyList<TransferHistoryEntry> list = service.Load();

            Assert.Equal(TransferHistoryService.MaxEntries, list.Count);
            // 最旧的（i=0）必须已被丢弃
            Assert.DoesNotContain(list, e => e.FileName == "file0.bin");
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void CorruptedHistoryFile_ReturnsEmpty_NoThrow()
    {
        TransferHistoryService service = NewService(out string dir);
        try
        {
            File.WriteAllText(service.FilePath, "{ 这不是合法 JSON ]]]");

            // 历史读不出来不应该拖垮文件互传模块
            Assert.Empty(service.Load());
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void Clear_RemovesAllRecords()
    {
        TransferHistoryService service = NewService(out string dir);
        try
        {
            service.Append(MakeEntry(1, DateTimeOffset.Now));
            service.Clear();

            Assert.Empty(service.Load());
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}
