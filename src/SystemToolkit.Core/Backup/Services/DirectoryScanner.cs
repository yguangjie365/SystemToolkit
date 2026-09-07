using System.IO.Enumeration;
using System.Security.Cryptography;
using System.Text;
using SystemToolkit.Core.Backup.Models;
using SystemToolkit.Core.Contracts;
using SystemToolkit.Core.Utilities;

namespace SystemToolkit.Core.Backup.Services;

/// <summary>
/// 备份扫描：把用户配置的一条源路径（文件/目录）或多条源，展开为 <see cref="ScanResult.Files"/>
///（相对路径 + 绝对路径 + 字节数）+ <see cref="ScanResult.EmptyDirs"/>（用于还原后重建空目录结构）。
/// <para>
/// 两个关键设计：<b>循环引用防御</b>（NormalizeDirectory 去重 visited + 警告跳过），
/// <b>重名冲突消除</b>（多源同名前缀加 SHA1 短哈希，多源路径合并时 RelativePath 碰撞追加短哈希后缀）。
/// </para>
/// </summary>
public static class DirectoryScanner
{
    /// <summary>
    /// 排除判定（纯函数，2026-09-07 新增，旧版无此能力）：模式支持 <c>*</c> / <c>?</c> 通配，
    /// 命中以下任一条件即排除——
    /// ① 相对路径的任一段（目录名 / 文件名）匹配模式（如 <c>node_modules</c>、<c>*.tmp</c>）；
    /// ② 多段模式作为路径前缀命中（如 <c>bin/Debug</c> 命中 <c>bin/Debug/x.dll</c>）。
    /// </summary>
    public static bool IsExcluded(string relativePath, IReadOnlyList<string>? patterns)
    {
        if (patterns is not { Count: > 0 })
        {
            return false;
        }

        string rel = (relativePath ?? "").Replace('\\', '/').TrimStart('/');
        if (rel.Length == 0)
        {
            return false;
        }

        string[] segments = rel.Split('/');
        foreach (string raw in patterns)
        {
            string pattern = (raw ?? "").Trim().Replace('\\', '/').Trim('/');
            if (pattern.Length == 0)
            {
                continue;
            }

            foreach (string segment in segments)
            {
                if (FileSystemName.MatchesSimpleExpression(pattern, segment, ignoreCase: true))
                {
                    return true;
                }
            }

            if (pattern.Contains('/')
                && (rel.StartsWith(pattern + "/", StringComparison.OrdinalIgnoreCase)
                    || FileSystemName.MatchesSimpleExpression(pattern, rel, ignoreCase: true)))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>单源扫描：单个文件直接返回单个条目；目录递归 Walk（本地函数，访问 closure 变量）。</summary>
    /// <param name="sourcePath">源文件或源目录。</param>
    /// <param name="logger">日志（缺省空实现）。</param>
    /// <param name="excludePatterns">排除模式（可选；见 <see cref="IsExcluded"/>）。</param>
    public static ScanResult ScanSingleSource(string sourcePath, ILogger? logger = null, IReadOnlyList<string>? excludePatterns = null)
    {
        ILogger log = logger ?? NullLogger.Instance;
        var files = new List<ScannedFile>();
        var emptyDirs = new List<string>();
        bool hasExclude = excludePatterns is { Count: > 0 };
        if (File.Exists(sourcePath))
        {
            // 单文件源也走排除判定（相对路径即文件名）
            if (hasExclude && IsExcluded(Path.GetFileName(sourcePath), excludePatterns))
            {
                return new ScanResult(files, emptyDirs);
            }

            files.Add(new ScannedFile(sourcePath, Path.GetFileName(sourcePath), new FileInfo(sourcePath).Length));
            return new ScanResult(files, emptyDirs);
        }
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        Walk(sourcePath, "");
        files.Sort((ScannedFile a, ScannedFile b) => string.CompareOrdinal(a.RelativePath, b.RelativePath));
        emptyDirs.Sort(StringComparer.Ordinal);
        return new ScanResult(files, emptyDirs);

        // DFS 递归扫描。参数 relPrefix 是相对于 sourcePath 的 POSIX 路径；空串代表顶层。
        //（本地函数不能挂 XML 文档注释，故用普通注释——见 CS1587）
        void Walk(string dirPath, string relPrefix)
        {
            string normalized;
            try
            {
                normalized = PathUtil.NormalizeDirectory(dirPath);
            }
            catch
            {
                return;
            }
            if (!visited.Add(normalized))
            {
                log.Warn("检测到目录循环引用，已跳过：" + dirPath);
                return;
            }

            List<FileSystemInfo> entries;
            try
            {
                // REVIEW-3 A-4：EnumerateFileSystemInfos 的 Attributes/Length 直接来自枚举数据
                //（FindFirstFile/FindNextFile 返回值），替代旧的 GetAttributes + FileInfo 两次额外 stat；
                // 目录内排序保留——DFS 警告顺序与循环引用检测的确定性依赖它
                entries = new DirectoryInfo(dirPath).EnumerateFileSystemInfos()
                    .OrderBy((FileSystemInfo e) => e.FullName, StringComparer.OrdinalIgnoreCase).ToList();
            }
            catch (Exception ex)
            {
                log.Warn($"无法读取目录：{dirPath}（{ex.Message}）");
                return;
            }

            if (entries.Count == 0)
            {
                emptyDirs.Add(string.IsNullOrEmpty(relPrefix) ? "" : relPrefix.Replace('\\', '/'));
                return;
            }

            foreach (FileSystemInfo entry in entries)
            {
                string entryName = entry.Name;
                string rel = string.IsNullOrEmpty(relPrefix) ? entryName : (relPrefix + "/" + entryName);
                // 排除判定放在属性访问之前：命中的目录整棵子树都不再递归（省 IO）
                if (hasExclude && IsExcluded(rel, excludePatterns))
                {
                    continue;
                }

                try
                {
                    FileAttributes attrs = entry.Attributes;
                    if ((attrs & FileAttributes.Directory) != FileAttributes.None)
                    {
                        if ((attrs & FileAttributes.ReparsePoint) != FileAttributes.None)
                        {
                            log.Warn("跳过符号链接/junction 目录（默认不跟随）：" + entry.FullName);
                        }
                        else
                        {
                            Walk(entry.FullName, rel);
                        }
                    }
                    else if ((attrs & FileAttributes.ReparsePoint) != FileAttributes.None)
                    {
                        log.Warn("跳过符号链接文件（默认不跟随）：" + entry.FullName);
                    }
                    else
                    {
                        files.Add(new ScannedFile(entry.FullName, rel.Replace('\\', '/'), ((FileInfo)entry).Length));
                    }
                }
                catch (Exception ex)
                {
                    log.Warn($"跳过无法访问的条目：{entry.FullName}（{ex.Message}）");
                }
            }
        }
    }

    /// <summary>
    /// 多源扫描：先解决源之间的"显示前缀冲突"（A/B 前缀同名时，后者加 6 位 SHA1 后缀
    /// 如 SourceName_a8c910），再调用 <see cref="ScanSingleSource"/>，最后合并结果并消除
    /// RelativePath 冲突（合并后同名时按 SourcePath 加 8 位短哈希后缀解冲突）。
    /// </summary>
    public static ScanResult ScanMultiSources(BackupRule rule, ILogger? logger = null)
    {
        IReadOnlyList<string> sources = rule.Sources();
        IReadOnlyList<string>? excludes = rule.ExcludePatterns;
        if (sources.Count == 1)
        {
            return ScanSingleSource(sources[0], logger, excludes);
        }

        // 阶段 1：为每个源分配 unique 显示前缀（prefix → source，反向 source → prefix）
        var prefixToSource = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var sourceToPrefix = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (string src in sources)
        {
            string prefix = SourcePrefix(src);
            if (prefixToSource.TryGetValue(prefix, out string? existingSrc)
                && !string.Equals(existingSrc, src, StringComparison.OrdinalIgnoreCase))
            {
                string suffix = Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(src)))
                    .Substring(0, 6).ToLowerInvariant();
                prefix = prefix + "_" + suffix;
            }
            prefixToSource[prefix] = src;
            sourceToPrefix[src] = prefix;
        }

        // 阶段 2：各源独立扫描 → 把相对路径加上源前缀 → 合并
        var mergedFiles = new List<ScannedFile>();
        var mergedEmptyDirs = new List<string>();
        foreach (string src in sources)
        {
            string prefix = sourceToPrefix[src];
            ScanResult srcResult = ScanSingleSource(src, logger, excludes);
            foreach (ScannedFile file in srcResult.Files)
            {
                mergedFiles.Add(new ScannedFile(
                    file.SourcePath,
                    string.IsNullOrEmpty(file.RelativePath) ? prefix : (prefix + "/" + file.RelativePath),
                    file.Length));
            }
            foreach (string emptyDir in srcResult.EmptyDirs)
            {
                mergedEmptyDirs.Add(string.IsNullOrEmpty(emptyDir) ? prefix : (prefix + "/" + emptyDir));
            }
        }

        // 阶段 3：排序 + 去 RelativePath 冲突（多源合并后，prefix 相同的文件会撞名）
        mergedFiles.Sort((ScannedFile a, ScannedFile b) => string.CompareOrdinal(a.RelativePath, b.RelativePath));
        mergedEmptyDirs.Sort(StringComparer.Ordinal);

        var relPathsSeen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var dedupFiles = new List<ScannedFile>();
        foreach (ScannedFile file in mergedFiles)
        {
            if (relPathsSeen.Add(file.RelativePath))
            {
                dedupFiles.Add(file);
                continue;
            }
            // 冲突解：按 SourcePath 取 8 位 SHA1 作后缀；若仍撞（极端情况）继续叠后缀
            string srcSuffix = Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(file.SourcePath)))
                .Substring(0, 8).ToLowerInvariant();
            string finalRel = file.RelativePath + "." + srcSuffix;
            while (!relPathsSeen.Add(finalRel))
            {
                finalRel = finalRel + "." + srcSuffix;
            }
            dedupFiles.Add(new ScannedFile(file.SourcePath, finalRel, file.Length));
        }

        // 阶段 4：空目录去重（不同源下可能合并出相同路径）
        var dedupEmptyDirs = new List<string>();
        var emptyDirsSeen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string dir in mergedEmptyDirs)
        {
            if (emptyDirsSeen.Add(dir))
            {
                dedupEmptyDirs.Add(dir);
            }
        }

        return new ScanResult(dedupFiles, dedupEmptyDirs);
    }

    /// <summary>
    /// 计算单个源的"显示前缀"——用于多源模式下在相对路径前标记来源。
    /// 目录取文件夹名；单文件取文件名去扩展名；异常退化用 folder/file 兜底词。
    /// </summary>
    private static string SourcePrefix(string sourcePath)
    {
        string fileName = Path.GetFileName(sourcePath);
        if (Directory.Exists(sourcePath))
        {
            return string.IsNullOrEmpty(fileName) ? "folder" : fileName;
        }
        string nameNoExt = Path.GetFileNameWithoutExtension(sourcePath);
        return string.IsNullOrEmpty(nameNoExt) ? "file" : nameNoExt;
    }
}
