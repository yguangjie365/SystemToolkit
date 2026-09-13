using SystemToolkit.Core.FileTransfer.Models;
using SystemToolkit.Core.FileTransfer.Services;
using SystemToolkit.Core.Utilities;

namespace SystemToolkit.Tests;

/// <summary>
/// P3 批次（2026-09-13）的两个纯逻辑判据：端口校验与历史 CSV 导出。
/// <para>
/// 这两处都是"拼错了不报错、用起来才发现"的类型（端口只在绑定时炸、CSV 只在 Excel 里错列），
/// 所以必须由用例钉住，而不是靠肉眼看代码。
/// </para>
/// </summary>
public class PortAndCsvTests
{
    // ── 端口校验（P3 ⑱） ──

    [Theory]
    [InlineData(1023, false)]
    [InlineData(1024, true)]
    [InlineData(18889, true)]
    [InlineData(65535, true)]
    [InlineData(65536, false)]
    [InlineData(0, false)]
    [InlineData(-1, false)]
    public void PortValidator_Range(int port, bool expected)
        => Assert.Equal(expected, PortValidator.IsInRange(port));

    [Fact]
    public void PortValidator_DuplicatePorts_Rejected()
    {
        string? error = PortValidator.Validate(("TCP 端口", 18889), ("UDP 端口", 18889));
        Assert.NotNull(error);
        Assert.Contains("TCP 端口", error);
        Assert.Contains("UDP 端口", error);
    }

    [Fact]
    public void PortValidator_OutOfRange_NamesTheOffender()
    {
        string? error = PortValidator.Validate(("Web 端口", 18890), ("HTTPS 端口", 80));
        Assert.NotNull(error);
        Assert.Contains("HTTPS 端口", error); // 必须指出是哪一个，否则用户得两处试
    }

    [Fact]
    public void PortValidator_AllValid_ReturnsNull()
        => Assert.Null(PortValidator.Validate(("TCP 端口", 18889), ("UDP 端口", 18888)));

    // ── 历史 CSV（P3 ⑮） ──

    private static TransferHistoryEntry Entry(
        string fileName, string peer = "192.168.1.23:18889", string? reasonCode = null,
        TransferStatus status = TransferStatus.Completed)
        => new()
        {
            TaskId = "t",
            FileName = fileName,
            FileSize = 1024,
            Direction = TransferDirection.Receive,
            PeerEndpoint = peer,
            Status = status,
            StartedAt = DateTimeOffset.UtcNow.AddSeconds(-3),
            FinishedAt = DateTimeOffset.UtcNow,
            TransferredBytes = 1024,
            ReasonCode = reasonCode,
        };

    [Fact]
    public void Csv_HasHeader_AndOneLinePerEntry()
    {
        string csv = TransferHistoryCsv.Build([Entry("a.txt"), Entry("b.txt")]);
        string[] lines = csv.Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(3, lines.Length); // 表头 + 2 行
        Assert.Equal(TransferHistoryCsv.Header, lines[0]);
        Assert.Contains("a.txt", lines[1]);
        Assert.Contains("b.txt", lines[2]);
    }

    /// <summary>
    /// 转义：文件名里的逗号/引号/换行必须被正确包裹——不转义会让整行错列，
    /// 用户看到的表格"像乱的"，却以为是软件坏了。
    /// </summary>
    [Fact]
    public void Csv_EscapesCommaQuoteAndNewline()
    {
        string csv = TransferHistoryCsv.Build([Entry("a,b\"c\nd.txt")]);
        string dataLine = csv.Split("\r\n", StringSplitOptions.RemoveEmptyEntries)[1];
        Assert.Contains("\"a,b\"\"c\nd.txt\"", dataLine);

        // 用最朴素的方式复核：按 RFC 4180 解析后，字段数应与表头一致
        int headerColumns = TransferHistoryCsv.Header.Split(',').Length;
        Assert.Equal(headerColumns, CountCsvColumns(dataLine));
    }

    [Fact]
    public void Csv_CarriesReasonCode_ForSkippedRows()
    {
        string csv = TransferHistoryCsv.Build(
            [Entry("dup.txt", reasonCode: TransferReasonCodes.ConflictSkip, status: TransferStatus.Skipped)]);
        Assert.Contains(TransferReasonCodes.ConflictSkip, csv);
        Assert.Contains("已跳过", csv);
    }

    /// <summary>按 RFC 4180 数一行的字段数（引号内的逗号与换行不算分隔）。</summary>
    private static int CountCsvColumns(string line)
    {
        int columns = 1;
        bool inQuotes = false;
        for (int i = 0; i < line.Length; i++)
        {
            char c = line[i];
            if (c == '"')
            {
                if (inQuotes && i + 1 < line.Length && line[i + 1] == '"')
                {
                    i++; // 转义引号
                    continue;
                }
                inQuotes = !inQuotes;
            }
            else if (c == ',' && !inQuotes)
            {
                columns++;
            }
        }
        return columns;
    }
}
