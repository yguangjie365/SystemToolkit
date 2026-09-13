using System.Text.Json;
using SystemToolkit.Core.Software.Models;
using SystemToolkit.Core.Utilities;

namespace SystemToolkit.Core.Software.Services;

/// <summary>
/// 忽略清单的落盘（默认 <c>%LOCALAPPDATA%\SystemToolkit\env\env_ignore_list.json</c>，
/// 与其它软件清单同目录）。目录可注入以便单测。
/// <para>
/// 纪律：写盘一律 <see cref="AtomicFile"/>；**写失败必须上抛**（让调用方能如实提示，
/// 不能"看起来记住了、其实没写"）；读取损坏时先备份真身再以空清单继续（不阻断启动）。
/// </para>
/// </summary>
public sealed class PackageIgnoreStore
{
    /// <summary>清单文件名。</summary>
    public const string FileName = "env_ignore_list.json";

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
    public PackageIgnoreStore(string? envDir = null, Action<string>? log = null)
    {
        StorageDirectory = envDir ?? Path.Combine(
            System.Environment.GetFolderPath(System.Environment.SpecialFolder.LocalApplicationData),
            "SystemToolkit", "env");
        _log = log ?? ((Action<string>)delegate (string msg)
        {
            Console.WriteLine("[ignore] " + msg);
        });
    }

    /// <summary>加载忽略清单（文件缺失或损坏都返回可用清单，不抛）。</summary>
    public PackageIgnoreList Load()
    {
        try
        {
            if (!File.Exists(FilePath))
            {
                return new PackageIgnoreList();
            }

            PackageIgnoreList? list = JsonSerializer.Deserialize<PackageIgnoreList>(File.ReadAllText(FilePath), JsonOpts);
            if (list is null)
            {
                _log("忽略清单内容为空，按空清单处理");
                return new PackageIgnoreList();
            }

            int dropped = list.Sanitize();
            if (dropped > 0)
            {
                _log($"忽略清单剔除 {dropped} 条无效记录（缺包 Id）");
            }

            return list;
        }
        catch (Exception ex)
        {
            BackupCorruptFile();
            _log("忽略清单损坏，已重建为空清单（原文件已备份）：" + ex.Message);
            return new PackageIgnoreList();
        }
    }

    /// <summary>覆盖保存忽略清单（原子写）。写盘失败上抛，调用方须显式提示。</summary>
    public void Save(PackageIgnoreList list)
    {
        try
        {
            Directory.CreateDirectory(StorageDirectory);
            AtomicFile.WriteAllText(FilePath, JsonSerializer.Serialize(list, JsonOpts));
        }
        catch (Exception ex)
        {
            _log("保存忽略清单失败：" + ex.Message);
            throw new IOException($"保存忽略清单失败：{FilePath}", ex);
        }
    }

    /// <summary>把损坏的真身另存为带时间戳的备份（失败只留痕，不打断加载）。</summary>
    private void BackupCorruptFile()
    {
        try
        {
            string backup = $"{FilePath}.corrupt_{DateTime.Now:yyyyMMdd_HHmmss}.json";
            File.Copy(FilePath, backup, overwrite: true);

            // 只保留最近若干份，防每次加载都新增一份
            foreach (string stale in Directory.GetFiles(StorageDirectory, FileName + ".corrupt_*")
                         .OrderByDescending(f => f, StringComparer.Ordinal)
                         .Skip(CorruptBackupKeep))
            {
                File.Delete(stale);
            }
        }
        catch (Exception ex)
        {
            _log("忽略清单损坏备份失败：" + ex.Message);
        }
    }
}
