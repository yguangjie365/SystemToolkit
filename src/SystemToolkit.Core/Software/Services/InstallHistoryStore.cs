using System.Text.Json;
using SystemToolkit.Core.Software.Models;
using SystemToolkit.Core.Utilities;

namespace SystemToolkit.Core.Software.Services;

/// <summary>
/// 安装历史的落盘（默认 <c>%LOCALAPPDATA%\SystemToolkit\env\install-history.json</c>，与其它软件清单同目录）。
/// <para>
/// 🔴 为什么不用日志当历史源：见 <see cref="InstallHistoryEntry"/> 的说明（`AppLog` 零读取 API + 保留期 + 10MB 滚动）。
/// 纪律与其它清单一致：写盘走 <see cref="AtomicFile"/>、写失败**上抛**（调用方决定是提示还是降级留痕）、
/// 读取损坏先备份真身再按空继续（不阻断启动）。
/// </para>
/// </summary>
public sealed class InstallHistoryStore
{
    /// <summary>文件名。</summary>
    public const string FileName = "install-history.json";

    /// <summary>损坏备份保留份数。</summary>
    private const int CorruptBackupKeep = 3;

    private static readonly JsonSerializerOptions JsonOpts = new JsonSerializerOptions
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
    };

    private readonly Action<string> _log;

    /// <summary>存储目录。</summary>
    public string StorageDirectory { get; }

    /// <summary>清单文件完整路径。</summary>
    public string FilePath => Path.Combine(StorageDirectory, FileName);

    /// <summary>注入存储目录与日志回调（缺省用统一配置根）。</summary>
    public InstallHistoryStore(string? envDir = null, Action<string>? log = null)
    {
        StorageDirectory = envDir ?? Path.Combine(
            System.Environment.GetFolderPath(System.Environment.SpecialFolder.LocalApplicationData),
            "SystemToolkit", "env");
        _log = log ?? ((Action<string>)delegate (string msg)
        {
            Console.WriteLine("[history] " + msg);
        });
    }

    /// <summary>加载安装历史（文件缺失或损坏都返回可用清单，不抛）。</summary>
    public InstallHistoryLog Load()
    {
        try
        {
            if (!File.Exists(FilePath))
            {
                return new InstallHistoryLog();
            }

            InstallHistoryLog? log = JsonSerializer.Deserialize<InstallHistoryLog>(File.ReadAllText(FilePath), JsonOpts);
            if (log is null)
            {
                _log("安装历史内容为空，按空历史处理");
                return new InstallHistoryLog();
            }

            int dropped = log.Sanitize();
            if (dropped > 0)
            {
                _log($"安装历史剔除 {dropped} 条无效记录（缺包 Id）");
            }

            return log;
        }
        catch (Exception ex)
        {
            BackupCorruptFile();
            _log("安装历史损坏，已重建为空历史（原文件已备份）：" + ex.Message);
            return new InstallHistoryLog();
        }
    }

    /// <summary>覆盖保存安装历史（原子写）。写盘失败上抛，调用方须显式处理。</summary>
    public void Save(InstallHistoryLog log)
    {
        try
        {
            Directory.CreateDirectory(StorageDirectory);
            AtomicFile.WriteAllText(FilePath, JsonSerializer.Serialize(log, JsonOpts));
        }
        catch (Exception ex)
        {
            _log("保存安装历史失败：" + ex.Message);
            throw new IOException($"保存安装历史失败：{FilePath}", ex);
        }
    }

    /// <summary>把损坏的真身另存为带时间戳的备份（失败只留痕，不打断加载）。</summary>
    private void BackupCorruptFile()
    {
        try
        {
            string backup = $"{FilePath}.corrupt_{DateTime.Now:yyyyMMdd_HHmmss}.json";
            File.Copy(FilePath, backup, overwrite: true);

            foreach (string stale in Directory.GetFiles(StorageDirectory, FileName + ".corrupt_*")
                         .OrderByDescending(f => f, StringComparer.Ordinal)
                         .Skip(CorruptBackupKeep))
            {
                File.Delete(stale);
            }
        }
        catch (Exception ex)
        {
            _log("安装历史损坏备份失败：" + ex.Message);
        }
    }
}
