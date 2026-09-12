using System.Text.Json;
using SystemToolkit.Core.Utilities;

namespace SystemToolkit.Core.Network.SplitRoute;

/// <summary>台账加载三态（同 LanBaselineStore 语义：损坏不炸，UI 给引导）。</summary>
public enum SplitLedgerLoadStatus
{
    /// <summary>正常读取。</summary>
    Ok = 0,

    /// <summary>无台账（未应用态）。</summary>
    Missing = 1,

    /// <summary>JSON 损坏/版本不认识——恢复操作拒绝盲动。</summary>
    Corrupted = 2,
}

/// <summary>
/// 分流台账持久化：<c>%LOCALAPPDATA%\SystemToolkit\net\split-route-ledger.json</c>（AtomicFile）。
/// 台账是「只删自建」纪律的事实来源——重启自检、守护清退、恢复回滚全部以它为准。
/// </summary>
public sealed class SplitLedgerStore
{
    /// <summary>当前格式版本。</summary>
    public const int FormatVersion = 1;

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private readonly string _filePath;

    /// <summary>缺省落应用数据目录；测试注入 temp 路径。</summary>
    public SplitLedgerStore(string? filePath = null)
    {
        _filePath = filePath ?? Path.Combine(
            System.Environment.GetFolderPath(System.Environment.SpecialFolder.LocalApplicationData),
            "SystemToolkit", "net", "split-route-ledger.json");
    }

    /// <summary>落盘路径（诊断用）。</summary>
    public string FilePath => _filePath;

    /// <summary>读取台账：Missing/Corrupted 均不抛。</summary>
    public (SplitLedgerLoadStatus State, SplitLedger? Ledger) Load()
    {
        if (!File.Exists(_filePath))
        {
            return (SplitLedgerLoadStatus.Missing, null);
        }

        try
        {
            SplitLedger? loaded = JsonSerializer.Deserialize<SplitLedger>(
                File.ReadAllText(_filePath), JsonOptions);
            return loaded is null or { Version: not FormatVersion } || loaded.Routes is null
                ? (SplitLedgerLoadStatus.Corrupted, null)
                : (SplitLedgerLoadStatus.Ok, loaded);
        }
        catch (JsonException)
        {
            return (SplitLedgerLoadStatus.Corrupted, null);
        }
        catch (IOException)
        {
            return (SplitLedgerLoadStatus.Corrupted, null);
        }
    }

    /// <summary>原子落盘台账。</summary>
    public void Save(SplitLedger ledger) =>
        AtomicFile.WriteAllText(_filePath, JsonSerializer.Serialize(ledger, JsonOptions));

    /// <summary>清除台账（恢复成功后；应用自有数据文件，非用户文档）。</summary>
    public void Clear()
    {
        if (File.Exists(_filePath))
        {
            File.Delete(_filePath);
        }
    }
}
